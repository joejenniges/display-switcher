using System.Security.AccessControl;
using System.Security.Principal;

namespace DisplaySwitcher;

internal static class Program
{
    const string MutexName = @"Local\DisplaySwitcher";
    const string ShowEventName = @"Local\DisplaySwitcher.Show";

    [STAThread]
    static int Main(string[] args)
    {
        // First thing: sets per-monitor V2 DPI awareness (from the csproj). It has to happen
        // before any window or display query, or coordinates get virtualized.
        ApplicationConfiguration.Initialize();

        bool elevate = Has(args, "--elevate");
        string? configDir = ArgValue(args, "--config-dir");

        // Helper verbs, run elevated by the Settings window and the installer.
        if (Has(args, "--register-startup") || Has(args, "--unregister-startup"))
        {
            Log.Init(new ConfigStore(configDir).LogPath);
            bool ok = Has(args, "--register-startup") ? StartupTask.Register(out var error) : StartupTask.Unregister(out error);
            if (!ok)
                Log.Warn($"Startup task change failed: {error}");
            return ok ? 0 : 1;
        }

        if (elevate && !Elevation.IsElevated())
            return Elevation.Relaunch(args) ? 0 : 1;

        var mutex = TryAcquireSingleInstance();
        if (mutex is null)
        {
            SignalRunningInstance();
            return 0;
        }

        // Launched unelevated (Start menu, installer) while the elevated logon task exists:
        // start through the task instead, which gives admin rights without a UAC prompt.
        if (!Elevation.IsElevated() && !Has(args, "--no-task") && configDir is null && StartupTask.IsRegisteredForThisExe())
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            if (StartupTask.Run())
                return 0;
            mutex = TryAcquireSingleInstance();
            if (mutex is null)
                return 0;
        }

        using (mutex)
            return Run(configDir);
    }

    static int Run(string? configDir)
    {
        var store = new ConfigStore(configDir);
        Log.Init(store.LogPath);
        Log.Info($"Starting {Application.ProductVersion}; config {store.Dir}; elevated={Elevation.IsElevated()}");

        DisplayManager.VerifyStructLayout();

        Settings settings;
        try
        {
            settings = store.LoadSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings.yaml is invalid", ex);
            MessageBox.Show($"settings.yaml is invalid:\n{ex.Message}\n\n{store.SettingsPath}", "Display Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var switcher = new LayoutSwitcher(store, settings);
        using var tray = new TrayApp(store, settings, switcher, Elevation.IsElevated());
        var api = new ApiHost(settings, switcher, tray.ReportApiSwitch);
        tray.AttachApi(api);
        if (!api.Restart())
            tray.ShowApiStartupError();

        using var showEvent = CreateShowEvent();
        var wait = showEvent is null ? null
            : ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => tray.OnSecondInstance(), null, Timeout.Infinite, executeOnlyOnce: false);

        tray.ShowSetupIfNeeded();
        Application.Run(tray);

        wait?.Unregister(null);
        api.Stop();
        Log.Info("Exiting");
        return 0;
    }

    static Mutex? TryAcquireSingleInstance()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out bool created);
            if (created)
                return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Exists, created by an elevated instance whose default DACL excludes this token.
            return null;
        }
    }

    // WHY an explicit ACL: the running instance is usually elevated (logon task), and an
    // elevated token's default DACL only grants Administrators and SYSTEM. Without this an
    // unelevated second launch could not signal it.
    static EventWaitHandle? CreateShowEvent()
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!,
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, security);
        }
        catch (Exception ex)
        {
            Log.Warn($"Second-instance signal unavailable: {ex.Message}");
            return null;
        }
    }

    static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                using (handle)
                    handle.Set();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        MessageBox.Show("Display Switcher is already running. Right-click its tray icon to use it.", "Display Switcher",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
