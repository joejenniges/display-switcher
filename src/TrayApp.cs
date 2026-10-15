using DisplaySwitcher.UI;

namespace DisplaySwitcher;

internal sealed class TrayApp : ApplicationContext
{
    readonly ConfigStore store;
    readonly Settings settings;
    readonly LayoutSwitcher switcher;
    readonly ContextMenuStrip menu;
    readonly NotifyIcon icon;
    readonly SynchronizationContext ui;
    readonly bool elevated;
    ApiHost? api;
    SettingsForm? settingsForm;
    SetupWizard? wizard;

    public TrayApp(ConfigStore store, Settings settings, LayoutSwitcher switcher, bool elevated)
    {
        this.store = store;
        this.settings = settings;
        this.switcher = switcher;
        this.elevated = elevated;

        // Creating the first control installs the WinForms SynchronizationContext.
        menu = new ContextMenuStrip();
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        icon = new NotifyIcon
        {
            Icon = AppIcon.Small(),
            Text = "Display Switcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };

        switcher.LayoutChanged += name => ui.Post(_ => icon.Text = $"Display Switcher: {name}", null);
        try
        {
            if (switcher.CurrentLayout() is { } current)
                icon.Text = $"Display Switcher: {current}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Initial layout detection failed: {ex.Message}");
        }
    }

    public void AttachApi(ApiHost host) => api = host;

    // All of these may be called before Application.Run; Post queues them for the message loop.

    public void ShowApiStartupError() => ui.Post(_ =>
    {
        if (api?.Error is { } error)
            Dialogs.Error("The HTTP API could not start",
                $"{error}\n\nHome Assistant can't reach Display Switcher until this is fixed. "
                + $"Another program may be using port {settings.Listen.Port}; pick another one in Settings.");
    }, null);

    public void ShowSetupIfNeeded() => ui.Post(state =>
    {
        try
        {
            if (!store.LoadLayouts().TryGet("default", out _, out _))
                OpenWizard();
        }
        catch (Exception ex)
        {
            Log.Error("Reading layouts for first-run check failed", ex);
        }
    }, null);

    // A second launch (Start menu, double-clicked exe) opens Settings in this instance.
    public void OnSecondInstance() => ui.Post(_ => OpenSettings(), null);

    // Called from the HTTP thread after an API-triggered switch.
    public void ReportApiSwitch(SwitchResult result) => ui.Post(_ => Report(result, fromTray: false), null);

    void BuildMenu()
    {
        menu.Items.Clear();

        LayoutsFile layouts;
        string? current = null;
        try
        {
            layouts = store.LoadLayouts();
            current = switcher.CurrentLayout();
        }
        catch (Exception ex)
        {
            Log.Error("Building tray menu failed", ex);
            layouts = new LayoutsFile();
            menu.Items.Add(new ToolStripMenuItem($"Config error: {ex.Message}") { Enabled = false });
        }

        menu.Items.Add(new ToolStripMenuItem($"Display Switcher{(elevated ? "  (admin)" : "")}") { Enabled = false });
        var apiLine = !settings.Listen.Enabled ? "HTTP API off"
            : api?.Running == true ? $"API on {api.Url}"
            : $"API not running: {api?.Error ?? "starting"}";
        menu.Items.Add(new ToolStripMenuItem(apiLine) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (layouts.Layouts.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("No layouts learned yet") { Enabled = false });
        foreach (var name in layouts.Layouts.Keys.Order())
        {
            var item = new ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, current, StringComparison.OrdinalIgnoreCase),
                Enabled = !switcher.Busy,
            };
            item.Click += (_, _) => RunSwitch(name);
            menu.Items.Add(item);
        }

        if (switcher.HasSaved(current))
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Restore saved windows for '{current}'", null, (_, _) => RunRestore(current));
        }

        menu.Items.Add(new ToolStripSeparator());
        var revert = new ToolStripMenuItem($"Auto revert ({settings.AutoRevert.Seconds} s)") { Checked = settings.AutoRevert.Enabled };
        revert.Click += (_, _) => ToggleAutoRevert();
        menu.Items.Add(revert);

        var learn = new ToolStripMenuItem("Learn current layout as");
        foreach (var name in layouts.Layouts.Keys.Order())
            learn.DropDownItems.Add(name, null, (_, _) => RunLearn(name));
        if (layouts.Layouts.Count > 0)
            learn.DropDownItems.Add(new ToolStripSeparator());
        learn.DropDownItems.Add("New layout...", null, (_, _) =>
        {
            if (Prompt("Learn layout", "Name for the current display layout:") is { Length: > 0 } name)
                RunLearn(name);
        });
        menu.Items.Add(learn);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Setup wizard...", null, (_, _) => OpenWizard());
        menu.Items.Add("Open config folder", null, (_, _) => ShellLauncher.TryOpen(store.Dir));
        menu.Items.Add("View log", null, (_, _) =>
        {
            if (File.Exists(store.LogPath))
                ShellLauncher.TryOpen(store.LogPath);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    void OpenSettings()
    {
        if (api is null)
            return;
        if (settingsForm is { IsDisposed: false })
        {
            settingsForm.Activate();
            return;
        }
        settingsForm = new SettingsForm(store, settings, api);
        settingsForm.FormClosed += (_, _) => settingsForm = null;
        settingsForm.Show();
    }

    void OpenWizard()
    {
        if (wizard is { IsDisposed: false })
        {
            wizard.Activate();
            return;
        }
        wizard = new SetupWizard(switcher, store, settings, OpenSettings);
        wizard.FormClosed += (_, _) => wizard = null;
        wizard.Show();
    }

    void ToggleAutoRevert()
    {
        settings.AutoRevert.Enabled = !settings.AutoRevert.Enabled;
        try
        {
            store.SaveSettings(settings);
            Log.Info($"Auto revert {(settings.AutoRevert.Enabled ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Dialogs.Error("Settings were not saved", ex.Message);
        }
    }

    // async void: menu click handler on the UI thread; every await resumes there.
    async void RunSwitch(string name)
    {
        try
        {
            string? revertTo = null;
            if (settings.AutoRevert.Enabled)
            {
                var (current, check) = await Task.Run(() => (switcher.CurrentLayout(), switcher.Validate(name)));
                if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
                    return;
                // Check before asking: "TV not connected" should be the answer, not a prompt.
                if (!check.Ok)
                {
                    ShowFailure(check, fromTray: true);
                    return;
                }
                int seconds = settings.AutoRevert.Seconds;
                if (current is null)
                {
                    if (!Dialogs.Confirm($"Switch to '{name}'?",
                            "The current display setup doesn't match a saved layout, so it can't be reverted automatically.",
                            "Switch without auto revert", TaskDialogIcon.Warning))
                        return;
                }
                else
                {
                    if (!Dialogs.Confirm($"Switch to '{name}'?",
                            $"Display Switcher will switch back to '{current}' after {seconds} seconds unless you click "
                            + "Keep layout.\n\nTurn off Auto revert in the tray menu to switch without this prompt.",
                            "Switch"))
                        return;
                    revertTo = current;
                }
            }

            var result = await Task.Run(() => switcher.Switch(name));
            Report(result, fromTray: true);
            if (revertTo is null || result.Status != SwitchStatus.Switched)
                return;

            if (Dialogs.KeepOrRevert(name, revertTo, settings.AutoRevert.Seconds))
            {
                Log.Info($"Kept '{name}'");
                return;
            }

            // Skip if something else (Home Assistant, Win+P) changed the layout during the countdown.
            var back = await Task.Run(() =>
                string.Equals(switcher.CurrentLayout(), name, StringComparison.OrdinalIgnoreCase) ? switcher.Switch(revertTo) : null);
            if (back is null)
            {
                Log.Info($"Auto revert to '{revertTo}' skipped: the layout changed during the countdown");
                return;
            }
            Log.Info($"Auto revert to '{revertTo}': {back.Status}");
            Report(back, fromTray: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Tray switch to '{name}' failed", ex);
            Dialogs.Error("Layout switch failed", ex.Message);
        }
    }

    async void RunRestore(string? layout)
    {
        try
        {
            Report(await Task.Run(() => switcher.RestoreSaved(layout)), fromTray: true);
        }
        catch (Exception ex)
        {
            Log.Error("Restore failed", ex);
            Dialogs.Error("Restore failed", ex.Message);
        }
    }

    async void RunLearn(string name)
    {
        try
        {
            var r = await Task.Run(() => switcher.Learn(name));
            if (!r.Ok)
            {
                Dialogs.Error($"Could not learn '{name}'", r.Message);
                return;
            }
            var lines = (r.Monitors ?? []).Select(m => m.ToString()).ToList();
            lines.Add($"Audio: {r.Audio?.Name ?? "none"}");
            lines.AddRange(r.Warnings ?? []);
            Notify(r.Message, string.Join("\n", lines), r.Warnings is { Count: > 0 } ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error($"Learn '{name}' failed", ex);
            Dialogs.Error($"Could not learn '{name}'", ex.Message);
        }
    }

    void Report(SwitchResult r, bool fromTray)
    {
        if (!r.Ok)
        {
            // A double-pressed HA button is not worth a dialog.
            if (r.Status == SwitchStatus.Busy)
            {
                if (fromTray)
                    Notify("Busy", r.Message, ToolTipIcon.Info);
                return;
            }
            if (fromTray || settings.Notifications.OnApiFailure)
                ShowFailure(r, fromTray);
            return;
        }
        if (r.Status != SwitchStatus.Switched)
            return;
        if (r.Warnings is { Count: > 0 } && fromTray)
            Notify(r.Message, string.Join("\n", r.Warnings), ToolTipIcon.Warning);
        else if (settings.Notifications.OnSuccess)
            Notify("Layout switched", r.Message, ToolTipIcon.Info);
    }

    static void ShowFailure(SwitchResult r, bool fromTray)
    {
        var (heading, hint) = r.Status switch
        {
            SwitchStatus.DisplayUnavailable => ($"Can't switch to '{r.Layout}'",
                "Nothing was changed. Check that every display in this layout is turned on, connected and set to the "
                + "right input, then try again. If cables, ports or graphics drivers changed, learn the layout again."),
            SwitchStatus.UnknownLayout => ("Unknown layout",
                "Learn it first: set up the displays, then tray icon > Learn current layout as."),
            SwitchStatus.InvalidRequest => ("Invalid request", ""),
            _ => ("Layout switch failed", "Details are in the log (tray icon > View log)."),
        };
        var source = fromTray ? "" : "\n\nRequested through the HTTP API (Home Assistant).";
        Dialogs.Error(heading, $"{r.Message}\n\n{hint}".TrimEnd() + source);
    }

    void Notify(string title, string text, ToolTipIcon kind) =>
        icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, kind);

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        settingsForm?.Close();
        wizard?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icon.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }

    static string? Prompt(string title, string label)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi,
            TopMost = true,
            Icon = AppIcon.Full,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };
        var box = new TextBox { Width = 280 };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var ok = new Button { Text = "Learn", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.AddRange([new Label { Text = label, AutoSize = true }, box, buttons]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
