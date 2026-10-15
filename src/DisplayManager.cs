using System.Diagnostics;
using System.Runtime.InteropServices;
using DisplaySwitcher.Native;
using static DisplaySwitcher.Native.DisplayConfigNative;

namespace DisplaySwitcher;

internal sealed class DisplayUnavailableException(string message) : Exception(message);

internal sealed class PreparedDisplay(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
{
    public DISPLAYCONFIG_PATH_INFO[] Paths { get; } = paths;
    public DISPLAYCONFIG_MODE_INFO[] Modes { get; } = modes;
}

// Captures and applies whole display topologies through the CCD API. A layout is the raw
// path/mode arrays Windows itself reports, replayed with SDC_USE_SUPPLIED_DISPLAY_CONFIG, so
// resolution, refresh rate, position, rotation and primary all come back exactly as learned.
internal static class DisplayManager
{
    public static void VerifyStructLayout()
    {
        int path = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        int mode = Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>();
        if (path != 72 || mode != 64)
            throw new InvalidOperationException($"CCD struct layout mismatch: PATH_INFO={path} (expected 72), MODE_INFO={mode} (expected 64)");
    }

    public static DisplayLayout Capture()
    {
        var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var adapters = new Dictionary<string, string>();
        void Add(LUID luid) => adapters.TryAdd(luid.ToString(), GetAdapterPath(luid));
        foreach (var p in paths)
        {
            Add(p.sourceInfo.adapterId);
            Add(p.targetInfo.adapterId);
        }
        foreach (var m in modes)
            Add(m.adapterId);

        return new DisplayLayout
        {
            Monitors = Summarize(paths, modes, adapters),
            Adapters = adapters,
            Paths = ToBase64(paths),
            Modes = ToBase64(modes),
        };
    }

    // Identifies a topology by which connectors are lit and how; used to detect the active
    // layout. Refresh is rounded so 119.88 vs 120 from rational rounding doesn't matter.
    public static string Signature(IEnumerable<MonitorSummary> monitors) =>
        string.Join(";", monitors
            .Select(m => $"{m.AdapterPath.ToUpperInvariant()}|{m.TargetId}|{m.X},{m.Y}|{m.Width}x{m.Height}|{Math.Round(m.RefreshHz)}")
            .Order(StringComparer.Ordinal));

    // Re-points the saved config at this boot's adapter LUIDs and checks every target is
    // physically present, without changing anything. Throws DisplayUnavailableException.
    public static PreparedDisplay Prepare(DisplayLayout layout)
    {
        var paths = FromBase64<DISPLAYCONFIG_PATH_INFO>(layout.Paths);
        var modes = FromBase64<DISPLAYCONFIG_MODE_INFO>(layout.Modes);
        if (paths.Length == 0)
            throw new InvalidOperationException("Layout has no display paths; re-learn it");

        var (allPaths, _) = Query(QDC_ALL_PATHS);
        var luidPaths = new Dictionary<string, string>();
        var currentByPath = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allPaths)
        {
            foreach (var luid in new[] { p.sourceInfo.adapterId, p.targetInfo.adapterId })
            {
                var key = luid.ToString();
                if (luidPaths.ContainsKey(key))
                    continue;
                try
                {
                    var adapterPath = GetAdapterPath(luid);
                    luidPaths[key] = adapterPath;
                    currentByPath.TryAdd(adapterPath, luid);
                }
                catch (InvalidOperationException)
                {
                    // Inactive/virtual adapters can refuse the name query; they can't be
                    // part of a learned layout anyway.
                    luidPaths[key] = "";
                }
            }
        }

        LUID Remap(LUID saved)
        {
            if (!layout.Adapters.TryGetValue(saved.ToString(), out var adapterPath))
                throw new InvalidOperationException($"Layout references adapter {saved} with no recorded device path; re-learn it");
            if (!currentByPath.TryGetValue(adapterPath, out var now))
                throw new DisplayUnavailableException($"Graphics adapter not present: {adapterPath}");
            return now;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].sourceInfo.adapterId = Remap(paths[i].sourceInfo.adapterId);
            paths[i].targetInfo.adapterId = Remap(paths[i].targetInfo.adapterId);
        }
        for (int i = 0; i < modes.Length; i++)
            modes[i].adapterId = Remap(modes[i].adapterId);

        foreach (var p in paths)
        {
            bool available = allPaths.Any(a =>
                SameLuid(a.targetInfo.adapterId, p.targetInfo.adapterId)
                && a.targetInfo.id == p.targetInfo.id
                && a.targetInfo.targetAvailable != 0);
            if (!available)
            {
                var name = layout.Monitors.FirstOrDefault(m => m.TargetId == p.targetInfo.id)?.Name ?? $"target {p.targetInfo.id}";
                throw new DisplayUnavailableException($"Display not detected: {name}");
            }
        }

        // WHY retry: right after a topology change (e.g. back from the TV within seconds),
        // validation returned ERROR_GEN_FAILURE (31) while monitors were still waking, then
        // succeeded a few seconds later. Genuinely invalid configs fail every attempt.
        int rc = ERROR_SUCCESS;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(1500);
            rc = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG);
            if (rc == ERROR_SUCCESS)
                break;
            Log.Warn($"SetDisplayConfig validate returned {rc} (attempt {attempt + 1})");
        }
        if (rc != ERROR_SUCCESS)
            throw new DisplayUnavailableException($"Windows rejected the layout (SetDisplayConfig validate returned {rc})");

        return new PreparedDisplay(paths, modes);
    }

    public static void Apply(PreparedDisplay display)
    {
        const uint flags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE;
        int rc = SetDisplayConfig((uint)display.Paths.Length, display.Paths, (uint)display.Modes.Length, display.Modes, flags);
        if (rc != ERROR_SUCCESS)
        {
            // Strict first so the learned mode (120Hz) is what gets applied; only let Windows
            // adjust the mode if the exact config is refused.
            Log.Warn($"SetDisplayConfig strict apply returned {rc}; retrying with SDC_ALLOW_CHANGES");
            rc = SetDisplayConfig((uint)display.Paths.Length, display.Paths, (uint)display.Modes.Length, display.Modes, flags | SDC_ALLOW_CHANGES);
        }
        if (rc != ERROR_SUCCESS)
            throw new InvalidOperationException($"SetDisplayConfig failed with error {rc}");
    }

    // SetDisplayConfig returns before user32/DWM finish rebuilding monitors, and moving windows
    // in that window lands them on a monitor that is about to disappear or not yet exist.
    public static bool WaitForSettle(DisplayLayout target, int timeoutMs)
    {
        var want = Signature(target.Monitors);
        int monitorCount = target.Monitors.Select(m => (m.X, m.Y)).Distinct().Count();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (Signature(Capture().Monitors) == want && GetSystemMetrics(SM_CMONITORS) == monitorCount)
                    return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Display query during settle failed: {ex.Message}");
            }
            Thread.Sleep(250);
        }
        return false;
    }

    static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(uint flags)
    {
        // The topology can change between sizing and querying; retry on that specific error.
        for (int attempt = 0; ; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(flags, out uint numPaths, out uint numModes);
            if (rc != ERROR_SUCCESS)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with error {rc}");
            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            rc = QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == ERROR_INSUFFICIENT_BUFFER && attempt < 5)
                continue;
            if (rc != ERROR_SUCCESS)
                throw new InvalidOperationException($"QueryDisplayConfig failed with error {rc}");
            Array.Resize(ref paths, (int)numPaths);
            Array.Resize(ref modes, (int)numModes);
            return (paths, modes);
        }
    }

    static List<MonitorSummary> Summarize(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, Dictionary<string, string> adapters)
    {
        var list = new List<MonitorSummary>();
        foreach (var p in paths)
        {
            var target = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            var summary = new MonitorSummary
            {
                Name = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName) ? $"target {p.targetInfo.id}" : target.monitorFriendlyDeviceName,
                GdiName = GetSourceName(p.sourceInfo.adapterId, p.sourceInfo.id),
                TargetId = p.targetInfo.id,
                AdapterPath = adapters[p.targetInfo.adapterId.ToString()],
                DevicePath = target.monitorDevicePath ?? "",
            };

            if (p.sourceInfo.modeInfoIdx < (uint)modes.Length && modes[p.sourceInfo.modeInfoIdx].infoType == MODE_INFO_TYPE_SOURCE)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                summary.X = sm.position.x;
                summary.Y = sm.position.y;
                summary.Width = sm.width;
                summary.Height = sm.height;
                summary.Primary = sm.position.x == 0 && sm.position.y == 0;
            }

            if (p.targetInfo.modeInfoIdx < (uint)modes.Length && modes[p.targetInfo.modeInfoIdx].infoType == MODE_INFO_TYPE_TARGET)
            {
                var v = modes[p.targetInfo.modeInfoIdx].targetVideoSignalInfo.vSyncFreq;
                summary.RefreshHz = v.Denominator == 0 ? 0 : Math.Round((double)v.Numerator / v.Denominator, 2);
            }

            list.Add(summary);
        }
        return list;
    }

    static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetName(LUID adapter, uint targetId)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        req.header.type = DEVICE_INFO_GET_TARGET_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        req.header.adapterId = adapter;
        req.header.id = targetId;
        int rc = DisplayConfigGetDeviceInfo(ref req);
        if (rc != ERROR_SUCCESS)
            Log.Warn($"Target name lookup failed for {adapter}/{targetId}: {rc}");
        return req;
    }

    static string GetSourceName(LUID adapter, uint sourceId)
    {
        var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        req.header.type = DEVICE_INFO_GET_SOURCE_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        req.header.adapterId = adapter;
        req.header.id = sourceId;
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.viewGdiDeviceName : "";
    }

    static string GetAdapterPath(LUID adapter)
    {
        var req = new DISPLAYCONFIG_ADAPTER_NAME();
        req.header.type = DEVICE_INFO_GET_ADAPTER_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>();
        req.header.adapterId = adapter;
        int rc = DisplayConfigGetDeviceInfo(ref req);
        if (rc != ERROR_SUCCESS)
            throw new InvalidOperationException($"Adapter name lookup failed for {adapter}: {rc}");
        return req.adapterDevicePath;
    }

    static bool SameLuid(LUID a, LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;

    static string ToBase64<T>(T[] items) where T : unmanaged =>
        Convert.ToBase64String(MemoryMarshal.AsBytes(items.AsSpan()));

    static T[] FromBase64<T>(string data) where T : unmanaged
    {
        var bytes = Convert.FromBase64String(data);
        if (bytes.Length % Marshal.SizeOf<T>() != 0)
            throw new InvalidOperationException($"Saved {typeof(T).Name} data is corrupt; re-learn the layout");
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }
}
