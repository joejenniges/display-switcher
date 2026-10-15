using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DisplaySwitcher;

// ---- settings.yaml: written by the Settings window; hand edits are read at startup

public sealed class Settings
{
    public ListenSettings Listen { get; set; } = new();
    public string? Token { get; set; }
    public AutoRevertSettings AutoRevert { get; set; } = new();
    public SwitchingSettings Switching { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();

    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public Settings Clone() => new()
    {
        Listen = new()
        {
            Enabled = Listen.Enabled,
            Host = Listen.Host,
            Port = Listen.Port,
            AllowedHosts = [.. Listen.AllowedHosts ?? []],
            WindowsEndpoint = Listen.WindowsEndpoint,
        },
        Token = Token,
        AutoRevert = new() { Enabled = AutoRevert.Enabled, Seconds = AutoRevert.Seconds },
        Switching = new()
        {
            SettleTimeoutMs = Switching.SettleTimeoutMs,
            SettleDelayMs = Switching.SettleDelayMs,
            AudioTimeoutMs = Switching.AudioTimeoutMs,
        },
        Notifications = new() { OnSuccess = Notifications.OnSuccess, OnApiFailure = Notifications.OnApiFailure },
    };

    // WHY: copy into the existing instance instead of swapping references. LayoutSwitcher
    // and TrayApp hold this object and read it on every switch, so edits apply immediately.
    public void CopyFrom(Settings other)
    {
        var c = other.Clone();
        Listen = c.Listen;
        Token = c.Token;
        AutoRevert = c.AutoRevert;
        Switching = c.Switching;
        Notifications = c.Notifications;
    }

    public const string Header = """
        # display-switcher settings. Edit through tray > Settings; hand edits are read at startup.
        #
        # listen.enabled        run the HTTP API (Home Assistant)
        # listen.host           0.0.0.0 = all interfaces, 127.0.0.1 = this PC only
        # listen.allowedHosts   extra Host header names to accept (IP addresses, localhost and this
        #                       PC's name always work); others are rejected to block DNS rebinding
        # listen.windowsEndpoint  enable GET /windows (lists every window title; for debugging)
        # token                 when set, requests except GET /health need "Authorization: Bearer <token>"
        # autoRevert            tray switches only: switch back after N seconds unless "Keep" is clicked
        # switching.*Ms         settle timeout, extra delay before moving windows, audio device wait
        # notifications         onSuccess: notify after every switch; onApiFailure: error dialog when
        #                       an API-triggered switch fails (tray-triggered failures always show one)

        """;
}

public sealed class ListenSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8765;
    public List<string> AllowedHosts { get; set; } = [];
    public bool WindowsEndpoint { get; set; }
}

public sealed class AutoRevertSettings
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 15;
}

public sealed class SwitchingSettings
{
    public int SettleTimeoutMs { get; set; } = 15000;
    public int SettleDelayMs { get; set; } = 1500;
    public int AudioTimeoutMs { get; set; } = 10000;
}

public sealed class NotificationSettings
{
    public bool OnSuccess { get; set; }
    public bool OnApiFailure { get; set; } = true;
}

// ---- layouts.yaml: learned layouts plus per-layout behaviour; rewritten by /learn

public sealed class LayoutsFile
{
    public Dictionary<string, LayoutDef> Layouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string name, out string key, out LayoutDef def)
    {
        foreach (var kv in Layouts)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                key = kv.Key;
                def = kv.Value;
                return true;
            }
        }
        key = name;
        def = null!;
        return false;
    }

    public const string Header = """
        # display-switcher layouts. Rewritten by /learn and the tray "Learn" menu: edits to
        # windows / saveOnLeave / launch survive a re-learn, comments do not.
        #
        # windows:      restore  - put windows back where they were when this layout was last left
        #               minimize - minimize every window on entry (land on the desktop)
        #               none     - leave windows alone
        # saveOnLeave:  snapshot window positions and default audio devices when leaving this
        #               layout, so entering it again with "windows: restore" puts them back
        # launch:       programs or URIs started after switching, e.g.
        #                 launch:
        #                   - path: steam://open/bigpicture
        # audio:        playback device made default on entry when there is no saved audio to restore
        # display:      captured display configuration. Do not edit; re-learn instead.

        """;
}

public sealed class LayoutDef
{
    public string Windows { get; set; } = WindowModes.None;
    public bool SaveOnLeave { get; set; }
    public List<LaunchItem> Launch { get; set; } = [];
    public AudioTarget? Audio { get; set; }
    public DisplayLayout? Display { get; set; }

    public static LayoutDef CreateFor(string name) =>
        // The home layout is the one that gets snapshotted and restored; anything else is
        // treated like the TV case. Both are editable in layouts.yaml afterwards.
        name.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? new LayoutDef { Windows = WindowModes.Restore, SaveOnLeave = true }
            : new LayoutDef { Windows = WindowModes.Minimize, SaveOnLeave = false };
}

public static class WindowModes
{
    public const string None = "none";
    public const string Minimize = "minimize";
    public const string Restore = "restore";
}

public sealed class LaunchItem
{
    public string Path { get; set; } = "";
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }
}

public sealed class AudioTarget
{
    public string DeviceId { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class DisplayLayout
{
    // Informational and used to detect which layout is active. Not used when applying.
    public List<MonitorSummary> Monitors { get; set; } = [];

    // LUID (as captured) -> adapter device path. LUIDs are reassigned on every boot, so
    // the saved CCD structs are re-pointed at the current LUIDs via these paths.
    public Dictionary<string, string> Adapters { get; set; } = [];

    // Raw DISPLAYCONFIG_PATH_INFO[] / DISPLAYCONFIG_MODE_INFO[] as base64.
    public string Paths { get; set; } = "";
    public string Modes { get; set; } = "";
}

public sealed class MonitorSummary
{
    public string Name { get; set; } = "";
    public string GdiName { get; set; } = "";
    public uint TargetId { get; set; }
    public string AdapterPath { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public double RefreshHz { get; set; }
    public bool Primary { get; set; }

    public override string ToString() => $"{Name} {Width}x{Height}@{RefreshHz:0.##}Hz ({X},{Y}){(Primary ? " primary" : "")}";
}

// ---- state.yaml: runtime state, machine-written

public sealed class StateFile
{
    public string? CurrentLayout { get; set; }

    // Layout name -> what was on screen when we last left it. Kept on disk so a restart of
    // the app while on the TV can still restore (window handles survive an app restart,
    // not a reboot).
    public Dictionary<string, SavedContext> Saved { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const string Header = "# Runtime state written by display-switcher. Not meant for hand editing.\n";
}

public sealed class SavedContext
{
    public DateTime TakenAt { get; set; }
    public WindowSnapshot? Windows { get; set; }
    // role (console | multimedia | communications) -> endpoint id
    public Dictionary<string, string> Audio { get; set; } = [];
}

public sealed class ConfigStore
{
    static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    readonly object fileLock = new();

    public ConfigStore(string? dir)
    {
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "display-switcher");
        Directory.CreateDirectory(Dir);
    }

    public string Dir { get; }
    public string SettingsPath => Path.Combine(Dir, "settings.yaml");
    public string LayoutsPath => Path.Combine(Dir, "layouts.yaml");
    public string StatePath => Path.Combine(Dir, "state.yaml");
    public string LogPath => Path.Combine(Dir, "display-switcher.log");

    // First run (no layouts.yaml yet, or no "default" layout) is detected by the caller.
    public Settings LoadSettings()
    {
        var settings = Read<Settings>(SettingsPath);
        if (settings is null)
        {
            // First run: the API listens on the LAN, so it starts out with a token. "Copy Home
            // Assistant config" in Settings includes it.
            settings = new Settings { Token = Settings.NewToken() };
            SaveSettings(settings);
        }
        settings.Listen ??= new();
        settings.Listen.AllowedHosts ??= [];
        settings.AutoRevert ??= new();
        settings.Switching ??= new();
        settings.Notifications ??= new();
        return settings;
    }

    public void SaveSettings(Settings settings) => Write(SettingsPath, Settings.Header + Serializer.Serialize(settings));

    public LayoutsFile LoadLayouts()
    {
        var file = Read<LayoutsFile>(LayoutsPath) ?? new LayoutsFile();
        file.Layouts = new Dictionary<string, LayoutDef>(file.Layouts ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var def in file.Layouts.Values)
        {
            def.Windows = (def.Windows ?? WindowModes.None).Trim().ToLowerInvariant();
            def.Launch ??= [];
        }
        return file;
    }

    public void SaveLayouts(LayoutsFile file) => Write(LayoutsPath, LayoutsFile.Header + Serializer.Serialize(file));

    public StateFile LoadState()
    {
        var state = Read<StateFile>(StatePath) ?? new StateFile();
        state.Saved = new Dictionary<string, SavedContext>(state.Saved ?? [], StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public void SaveState(StateFile state) => Write(StatePath, StateFile.Header + Serializer.Serialize(state));

    T? Read<T>(string path) where T : class
    {
        lock (fileLock)
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : Deserializer.Deserialize<T>(text);
        }
    }

    void Write(string path, string text)
    {
        lock (fileLock)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
