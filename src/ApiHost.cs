using Microsoft.AspNetCore.Builder;

namespace DisplaySwitcher;

// Owns the Kestrel instance so the Settings window can stop, rebind or disable it without
// restarting the app.
internal sealed class ApiHost(Settings settings, LayoutSwitcher switcher, Action<SwitchResult> onApiSwitch)
{
    readonly object gate = new();
    WebApplication? app;

    public string? Error { get; private set; }
    public string? Url { get; private set; }
    public bool Running => app is not null;

    // Applies the current settings: stops any running instance, then starts one if enabled.
    // Returns false and sets Error if the listener could not be started.
    public bool Restart()
    {
        lock (gate)
        {
            StopCore();
            Error = null;
            if (!settings.Listen.Enabled)
            {
                Log.Info("API disabled");
                return true;
            }
            var url = $"http://{settings.Listen.Host}:{settings.Listen.Port}";
            try
            {
                var candidate = ApiServer.Build(settings, switcher, onApiSwitch);
                candidate.StartAsync().GetAwaiter().GetResult();
                app = candidate;
                Url = url;
                Log.Info($"Listening on {url}");
                return true;
            }
            catch (Exception ex)
            {
                // Keep the tray usable even if the port is taken.
                Error = ex.GetBaseException().Message;
                Log.Error($"API failed to start on {url}", ex);
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
            StopCore();
    }

    void StopCore()
    {
        if (app is null)
            return;
        try
        {
            app.StopAsync().Wait(TimeSpan.FromSeconds(3));
            app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warn($"API shutdown: {ex.Message}");
        }
        app = null;
        Url = null;
    }
}
