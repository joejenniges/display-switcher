using System.Diagnostics;
using System.Runtime.InteropServices;
using DisplaySwitcher.Native;
using static DisplaySwitcher.Native.AudioNative;

namespace DisplaySwitcher;

public sealed record AudioDevice(string Id, string Name);

internal static class AudioManager
{
    static readonly Dictionary<string, ERole> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["console"] = ERole.eConsole,
        ["multimedia"] = ERole.eMultimedia,
        ["communications"] = ERole.eCommunications,
    };

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static AudioDevice? GetDefault(ERole role = ERole.eMultimedia)
    {
        if (Enumerator().GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var device) != 0 || device is null)
            return null;
        return Describe(device);
    }

    public static List<AudioDevice> ListActive()
    {
        var list = new List<AudioDevice>();
        if (Enumerator().EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var devices) != 0)
            return list;
        devices.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (devices.Item(i, out var device) == 0 && Describe(device) is { } d)
                list.Add(d);
        }
        return list;
    }

    // role name -> endpoint id for every role that has a default.
    public static Dictionary<string, string> CaptureDefaults()
    {
        var saved = new Dictionary<string, string>();
        foreach (var (name, role) in Roles)
        {
            if (GetDefault(role) is { } d)
                saved[name] = d.Id;
        }
        return saved;
    }

    public static List<string> RestoreDefaults(Dictionary<string, string> saved, int timeoutMs)
    {
        var warnings = new List<string>();
        foreach (var group in saved.Where(kv => Roles.ContainsKey(kv.Key)).GroupBy(kv => kv.Value))
        {
            var roles = group.Select(kv => Roles[kv.Key]).ToArray();
            if (!SetDefault(group.Key, timeoutMs, roles))
                warnings.Add($"Saved audio device is no longer available ({string.Join(", ", group.Select(kv => kv.Key))})");
        }
        return warnings;
    }

    public static bool SetDefault(string deviceId, int timeoutMs, params ERole[] roles)
    {
        // The TV's HDMI audio endpoint only appears after the display path is lit, and lags
        // the display by a second or two.
        var sw = Stopwatch.StartNew();
        while (!IsActive(deviceId))
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Log.Warn($"Audio device {deviceId} not active after {timeoutMs}ms");
                return false;
            }
            Thread.Sleep(250);
        }

        var policy = (IPolicyConfig)new PolicyConfigClient();
        bool ok = true;
        foreach (var role in roles)
        {
            int hr = policy.SetDefaultEndpoint(deviceId, role);
            if (hr != 0)
            {
                Log.Warn($"SetDefaultEndpoint({deviceId}, {role}) returned 0x{hr:X8}");
                ok = false;
            }
        }
        return ok;
    }

    static bool IsActive(string deviceId) =>
        Enumerator().GetDevice(deviceId, out var device) == 0
        && device.GetState(out uint state) == 0
        && state == DEVICE_STATE_ACTIVE;

    static AudioDevice? Describe(IMMDevice device)
    {
        if (device.GetId(out var id) != 0)
            return null;
        return new AudioDevice(id, FriendlyName(device) ?? id);
    }

    static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0)
            return null;
        var key = PKEY_Device_FriendlyName;
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.data1) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }
}
