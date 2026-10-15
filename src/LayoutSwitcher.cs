using System.Diagnostics;
using System.Text.RegularExpressions;
using DisplaySwitcher.Native;

namespace DisplaySwitcher;

public enum SwitchStatus { Switched, Learned, AlreadyActive, UnknownLayout, Busy, DisplayUnavailable, InvalidRequest, Failed }

public sealed record SwitchResult(
    SwitchStatus Status,
    string Message,
    string? From = null,
    string? Layout = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<MonitorSummary>? Monitors = null,
    AudioDevice? Audio = null)
{
    public bool Ok => Status is SwitchStatus.Switched or SwitchStatus.Learned or SwitchStatus.AlreadyActive;
}

internal sealed partial class LayoutSwitcher(ConfigStore store, Settings settings)
{
    // One switch at a time. A second request while one is running is rejected rather than
    // queued: a double-pressed HA button should not bounce the displays twice.
    readonly SemaphoreSlim gate = new(1, 1);

    public event Action<string>? LayoutChanged;

    public bool Busy => gate.CurrentCount == 0;

    public string? CurrentLayout()
    {
        var layouts = store.LoadLayouts();
        return Detect(layouts, DisplayManager.Capture()) ?? store.LoadState().CurrentLayout;
    }

    public SwitchResult Switch(string name) => Guarded(name, () => SwitchCore(name.Trim()));

    public SwitchResult Learn(string name) => Guarded(name, () => LearnCore(name.Trim()));

    // Puts back a saved snapshot without touching displays. Recovery for when a switch was
    // interrupted after the displays changed but before windows were restored.
    public SwitchResult RestoreSaved(string? name) => Guarded(name ?? "", () =>
    {
        var state = store.LoadState();
        var key = string.IsNullOrWhiteSpace(name) ? CurrentLayout() : name.Trim();
        if (key is null || !state.Saved.TryGetValue(key, out var saved))
            return new(SwitchStatus.UnknownLayout, $"No saved windows for '{key ?? "current layout"}'", Layout: key);

        var warnings = new List<string>();
        if (saved.Audio.Count > 0)
            warnings.AddRange(AudioManager.RestoreDefaults(saved.Audio, settings.Switching.AudioTimeoutMs));
        if (saved.Windows is not null)
        {
            var (restored, skipped) = WindowManager.Restore(saved.Windows);
            Log.Info($"Manual restore of '{key}': {restored} windows, skipped {skipped}");
            if (skipped > 0)
                warnings.Add($"{skipped} saved window(s) were closed, not responding, or could not be moved");
        }
        state.Saved.Remove(key);
        store.SaveState(state);
        return new(SwitchStatus.Switched, $"Restored saved windows for '{key}'", Layout: key, Warnings: warnings);
    });

    public bool HasSaved(string? name) => name is not null && store.LoadState().Saved.ContainsKey(name);

    // Dry run: is the layout known and are all its displays connected right now?
    public SwitchResult Validate(string name)
    {
        try
        {
            var layouts = store.LoadLayouts();
            if (!layouts.TryGet(name.Trim(), out var key, out var def) || def.Display is null)
                return new(SwitchStatus.UnknownLayout, $"Unknown layout '{name}'");
            DisplayManager.Prepare(def.Display);
            return new(SwitchStatus.AlreadyActive, $"Layout '{key}' can be applied", Layout: key, Monitors: def.Display.Monitors);
        }
        catch (DisplayUnavailableException ex)
        {
            return new(SwitchStatus.DisplayUnavailable, ex.Message, Layout: name);
        }
        catch (Exception ex)
        {
            Log.Error($"Validate '{name}' failed", ex);
            return new(SwitchStatus.Failed, ex.Message, Layout: name);
        }
    }

    public object Status()
    {
        var layouts = store.LoadLayouts();
        var state = store.LoadState();
        var displays = DisplayManager.Capture();
        var detected = Detect(layouts, displays);
        return new
        {
            currentLayout = detected ?? state.CurrentLayout,
            detectedLayout = detected,
            busy = Busy,
            elevated = Elevation.IsElevated(),
            displays = displays.Monitors,
            audio = AudioManager.GetDefault(),
            layouts = layouts.Layouts.Select(kv => new
            {
                name = kv.Key,
                windows = kv.Value.Windows,
                saveOnLeave = kv.Value.SaveOnLeave,
                monitors = kv.Value.Display?.Monitors,
                audio = kv.Value.Audio,
            }),
            saved = state.Saved.Select(kv => new
            {
                layout = kv.Key,
                takenAt = kv.Value.TakenAt,
                windows = kv.Value.Windows?.Windows.Count ?? 0,
            }),
        };
    }

    SwitchResult Guarded(string name, Func<SwitchResult> action)
    {
        if (!gate.Wait(0))
            return new(SwitchStatus.Busy, "A layout switch is already in progress", Layout: name);
        try
        {
            return action();
        }
        catch (DisplayUnavailableException ex)
        {
            Log.Warn($"'{name}': {ex.Message}");
            return new(SwitchStatus.DisplayUnavailable, ex.Message, Layout: name);
        }
        catch (Exception ex)
        {
            Log.Error($"'{name}' failed", ex);
            return new(SwitchStatus.Failed, ex.Message, Layout: name);
        }
        finally
        {
            gate.Release();
        }
    }

    SwitchResult SwitchCore(string name)
    {
        var layouts = store.LoadLayouts();
        if (!layouts.TryGet(name, out var key, out var target))
            return new(SwitchStatus.UnknownLayout, $"Unknown layout '{name}'", Layout: name);
        if (target.Display is null)
            return new(SwitchStatus.UnknownLayout, $"Layout '{key}' has no learned display configuration", Layout: key);

        var state = store.LoadState();
        // What's actually lit wins over what we last recorded, so a manual Win+P change
        // doesn't make us snapshot the wrong layout or skip a needed switch.
        var from = Detect(layouts, DisplayManager.Capture()) ?? state.CurrentLayout;
        if (string.Equals(from, key, StringComparison.OrdinalIgnoreCase))
        {
            if (state.CurrentLayout != key)
            {
                state.CurrentLayout = key;
                store.SaveState(state);
            }
            return new(SwitchStatus.AlreadyActive, $"Layout '{key}' is already active", from, key);
        }

        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();

        // Everything that can fail on "TV is off" happens here, before any window is touched.
        var prepared = DisplayManager.Prepare(target.Display);

        WindowSnapshot? taken = null;
        if (from is not null && layouts.TryGet(from, out var fromKey, out var fromDef) && fromDef.SaveOnLeave)
        {
            taken = WindowManager.Capture();
            state.Saved[fromKey] = new SavedContext
            {
                TakenAt = DateTime.Now,
                Windows = taken,
                Audio = AudioManager.CaptureDefaults(),
            };
            store.SaveState(state);
            Log.Info($"Saved {taken.Windows.Count} windows and audio defaults for '{fromKey}'");
        }
        else if (from is null)
        {
            warnings.Add("Current layout not recognised; window positions were not saved");
        }

        bool minimized = false;
        if (target.Windows == WindowModes.Minimize)
        {
            // Before the display change, so windows don't visibly get thrown onto the TV.
            WindowManager.MinimizeAll();
            minimized = true;
        }

        try
        {
            DisplayManager.Apply(prepared);
        }
        catch
        {
            if (minimized && taken is not null)
                WindowManager.Restore(taken);
            throw;
        }

        state.CurrentLayout = key;
        store.SaveState(state);

        if (!DisplayManager.WaitForSettle(target.Display, settings.Switching.SettleTimeoutMs))
            warnings.Add("Displays did not match the learned layout before the settle timeout");
        Thread.Sleep(settings.Switching.SettleDelayMs);

        state.Saved.TryGetValue(key, out var saved);

        if (saved is { Audio.Count: > 0 })
            warnings.AddRange(AudioManager.RestoreDefaults(saved.Audio, settings.Switching.AudioTimeoutMs));
        else if (target.Audio is { DeviceId.Length: > 0 } audio
                 && !AudioManager.SetDefault(audio.DeviceId, settings.Switching.AudioTimeoutMs, ERole.eConsole, ERole.eMultimedia))
            warnings.Add($"Audio device not available: {audio.Name ?? audio.DeviceId}");

        switch (target.Windows)
        {
            case WindowModes.Restore when saved?.Windows is not null:
                var (restored, skipped) = WindowManager.Restore(saved.Windows);
                Log.Info($"Restored {restored} windows, skipped {skipped}");
                if (skipped > 0)
                        warnings.Add($"{skipped} saved window(s) were closed, not responding, or could not be moved");
                break;
            case WindowModes.Restore:
                warnings.Add("No saved windows to restore");
                break;
            case WindowModes.Minimize:
                // Second pass for anything Windows re-showed during the topology change.
                WindowManager.MinimizeAll();
                break;
        }

        if (saved is not null)
        {
            state.Saved.Remove(key);
            store.SaveState(state);
        }

        foreach (var item in target.Launch)
        {
            try
            {
                // Never elevated: layouts.yaml is writable by unelevated processes.
                ShellLauncher.Open(item.Path, item.Args, item.WorkingDir);
            }
            catch (Exception ex)
            {
                warnings.Add($"Launch failed for {item.Path}: {ex.Message}");
            }
        }

        var now = DisplayManager.Capture().Monitors;
        Log.Info($"Switched '{from ?? "unknown"}' -> '{key}' in {sw.ElapsedMilliseconds}ms; {string.Join(", ", now)}");
        foreach (var w in warnings)
            Log.Warn(w);
        LayoutChanged?.Invoke(key);
        return new(SwitchStatus.Switched, $"Switched from '{from ?? "unknown"}' to '{key}'", from, key, warnings, now);
    }

    SwitchResult LearnCore(string name)
    {
        if (!LayoutName().IsMatch(name))
            return new(SwitchStatus.InvalidRequest, "Layout names are 1-64 characters of letters, digits, '-' and '_'", Layout: name);

        var display = DisplayManager.Capture();
        var audio = AudioManager.GetDefault();
        var layouts = store.LoadLayouts();
        if (!layouts.TryGet(name, out var key, out var def))
        {
            key = name.ToLowerInvariant();
            def = LayoutDef.CreateFor(key);
            layouts.Layouts[key] = def;
        }
        def.Display = display;
        def.Audio = audio is null ? null : new AudioTarget { DeviceId = audio.Id, Name = audio.Name };

        var warnings = new List<string>();
        var signature = DisplayManager.Signature(display.Monitors);
        foreach (var (other, otherDef) in layouts.Layouts)
        {
            if (other != key && otherDef.Display is not null && DisplayManager.Signature(otherDef.Display.Monitors) == signature)
                warnings.Add($"Layout '{other}' has the identical display configuration; detection can't tell them apart");
        }
        foreach (var m in display.Monitors.Where(m => m.RefreshHz < 100))
            warnings.Add($"{m.Name} is at {m.RefreshHz:0.##}Hz");

        store.SaveLayouts(layouts);
        var state = store.LoadState();
        state.CurrentLayout = key;
        store.SaveState(state);

        Log.Info($"Learned '{key}': {string.Join(", ", display.Monitors)}; audio {audio?.Name ?? "none"}");
        LayoutChanged?.Invoke(key);
        return new(SwitchStatus.Learned, $"Learned layout '{key}'", Layout: key, Warnings: warnings, Monitors: display.Monitors, Audio: audio);
    }

    static string? Detect(LayoutsFile layouts, DisplayLayout current)
    {
        var signature = DisplayManager.Signature(current.Monitors);
        foreach (var (name, def) in layouts.Layouts)
        {
            if (def.Display is not null && DisplayManager.Signature(def.Display.Monitors) == signature)
                return name;
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex LayoutName();
}
