using Microsoft.Win32;

namespace DisplaySwitcher.UI;

// First-run walkthrough for learning the "default" layout. Also reachable from the tray.
internal sealed class SetupWizard : DialogForm
{
    const int PageWidth = 500;
    const string DefaultLayout = "default";

    readonly LayoutSwitcher switcher;
    readonly ConfigStore store;
    readonly Settings settings;
    readonly Action openSettings;

    readonly Panel[] pages;
    readonly Label[] stepLabels;
    readonly Button back = new() { Text = "< Back", AutoSize = true, MinimumSize = new Size(80, 0) };
    readonly Button next = new() { Text = "Next >", AutoSize = true, MinimumSize = new Size(80, 0) };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0) };
    int index;

    // Displays page
    readonly Label rememberState = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 8) };

    // Audio page
    readonly Label audioNow = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };

    // Save page
    readonly ListView monitors = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Size = new Size(PageWidth, 110),
        Margin = new Padding(0, 0, 0, 8),
    };
    readonly Label saveAudio = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 8) };
    readonly Button saveButton = new() { Text = $"Save as \"{DefaultLayout}\"", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    readonly Label saveResult = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    string? lastSignature;
    bool saved;

    static readonly (string Title, int Width)[] Columns =
        [("Display", 150), ("Resolution", 100), ("Refresh", 80), ("Position", 100), ("Main", 50)];

    // Next-steps page
    readonly Label apiInfo = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 8) };

    public SetupWizard(LayoutSwitcher switcher, ConfigStore store, Settings settings, Action openSettings)
    {
        this.switcher = switcher;
        this.store = store;
        this.settings = settings;
        this.openSettings = openSettings;

        Text = "Display Switcher Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(760, 560);

        string[] steps = ["Welcome", "Displays", "Audio", "Save layout", "Next steps"];
        pages = [WelcomePage(), DisplaysPage(), AudioPage(), SavePage(), NextStepsPage()];

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 170,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(16, 20, 8, 8),
            BackColor = SystemColors.ControlLight,
        };
        stepLabels = steps.Select((s, i) => new Label { Text = $"{i + 1}.  {s}", AutoSize = true, Margin = new Padding(0, 0, 0, 12) }).ToArray();
        sidebar.Controls.AddRange(stepLabels);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(10, 10, 10, 6),
        };
        bottom.Controls.AddRange([cancel, next, back]);
        var rule = new Label { Dock = DockStyle.Bottom, Height = 1, BorderStyle = BorderStyle.Fixed3D };

        var host = new Panel { Dock = DockStyle.Fill };
        foreach (var page in pages)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            host.Controls.Add(page);
        }

        Controls.Add(host);
        Controls.Add(rule);
        Controls.Add(bottom);
        Controls.Add(sidebar);

        back.Click += (_, _) => ShowPage(index - 1);
        next.Click += (_, _) =>
        {
            if (index == pages.Length - 1)
                Close();
            else
                ShowPage(index + 1);
        };
        cancel.Click += (_, _) => Close();
        saveButton.Click += async (_, _) => await SaveDefault();
        refresh.Tick += (_, _) => RefreshCurrent();

        saved = store.LoadLayouts().TryGet(DefaultLayout, out _, out _);
        EndBuild();
        ShowPage(0);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Dispose();
        base.OnFormClosed(e);
    }

    void ShowPage(int i)
    {
        index = Math.Clamp(i, 0, pages.Length - 1);
        for (int p = 0; p < pages.Length; p++)
        {
            pages[p].Visible = p == index;
            stepLabels[p].Font = new Font(Font, p == index ? FontStyle.Bold : FontStyle.Regular);
            stepLabels[p].ForeColor = p == index ? SystemColors.ControlText : SystemColors.GrayText;
        }
        back.Enabled = index > 0;
        bool last = index == pages.Length - 1;
        next.Text = last ? "Finish" : "Next >";
        cancel.Visible = !last;
        // The layout has to exist before "next steps" make sense.
        next.Enabled = index != 3 || saved;
        AcceptButton = next;

        refresh.Enabled = index is 1 or 2 or 3;
        lastSignature = null;
        RefreshCurrent();
    }

    void RefreshCurrent()
    {
        try
        {
            switch (index)
            {
                case 1:
                    rememberState.Text = RememberWindowLocationsOff() switch
                    {
                        true => "Currently: off. Good.",
                        false => "Currently: ON. Turn it off before continuing.",
                        null => "Currently: unknown. Check the setting by hand.",
                    };
                    rememberState.ForeColor = RememberWindowLocationsOff() == true ? Color.ForestGreen : Color.Firebrick;
                    break;
                case 2:
                    audioNow.Text = $"Current default playback device: {AudioManager.GetDefault()?.Name ?? "none"}";
                    break;
                case 3:
                    RefreshSavePage();
                    break;
                case 4:
                    apiInfo.Text = settings.Listen.Enabled
                        ? $"The HTTP API listens on port {settings.Listen.Port}. POST {{\"layout\": \"tv\"}} to http://<this PC>:{settings.Listen.Port}/layout. "
                          + "Settings has a button that copies a ready-made Home Assistant rest_command."
                        : "The HTTP API is turned off. Turn it on in Settings to trigger switches from Home Assistant.";
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup wizard refresh: {ex.Message}");
        }
    }

    void RefreshSavePage()
    {
        var display = DisplayManager.Capture();
        var audio = AudioManager.GetDefault();
        var signature = DisplayManager.Signature(display.Monitors) + "|" + audio?.Id;
        if (signature == lastSignature)
            return;
        lastSignature = signature;

        monitors.BeginUpdate();
        // Column widths are in pixels and are not touched by form auto-scaling.
        for (int c = 0; c < Columns.Length; c++)
            monitors.Columns[c].Width = LogicalToDeviceUnits(Columns[c].Width);
        monitors.Items.Clear();
        foreach (var m in display.Monitors.OrderBy(m => m.Y).ThenBy(m => m.X))
        {
            monitors.Items.Add(new ListViewItem([
                m.Name.Length > 0 ? m.Name : m.GdiName,
                $"{m.Width} x {m.Height}",
                $"{m.RefreshHz:0.##} Hz",
                $"{m.X}, {m.Y}",
                m.Primary ? "Yes" : "",
            ]));
        }
        monitors.EndUpdate();
        saveAudio.Text = $"Default playback device: {audio?.Name ?? "none"}";
    }

    async Task SaveDefault()
    {
        saveButton.Enabled = false;
        next.Enabled = false;
        saveResult.ForeColor = SystemColors.ControlText;
        saveResult.Text = "Saving...";
        var r = await Task.Run(() => switcher.Learn(DefaultLayout));
        saveButton.Enabled = true;
        if (!r.Ok)
        {
            saveResult.ForeColor = Color.Firebrick;
            saveResult.Text = $"Not saved: {r.Message}";
            next.Enabled = saved;
            return;
        }
        saved = true;
        next.Enabled = true;
        var lines = new List<string> { $"Saved {r.Monitors?.Count ?? 0} display(s) and {r.Audio?.Name ?? "no audio device"} as \"{DefaultLayout}\"." };
        lines.AddRange(r.Warnings ?? []);
        saveResult.ForeColor = r.Warnings is { Count: > 0 } ? Color.DarkGoldenrod : Color.ForestGreen;
        saveResult.Text = string.Join("\n", lines);
    }

    // HKCU\Control Panel\Desktop\RestorePreviousStateRecalcBehavior is what the Settings
    // toggle "Remember window locations based on monitor connection" writes: 1 = off.
    // Absent means the Windows default, which is on.
    static bool? RememberWindowLocationsOff()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("RestorePreviousStateRecalcBehavior") is int v ? v == 1 : false;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- pages

    static Panel Page(params Control[] content)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(24, 20),
        };
        flow.Controls.AddRange(content);
        var page = new Panel { AutoScroll = true };
        page.Controls.Add(flow);
        return page;
    }

    Panel WelcomePage() => Page(
        Heading("Welcome to Display Switcher"),
        Para("Display Switcher switches between saved display layouts, for example your desk monitors and a TV, "
             + "from the tray menu or from Home Assistant. Each layout records which displays are on, their "
             + "resolution, refresh rate and arrangement, and the default audio device.", PageWidth),
        Para("When you leave your \"default\" layout, the position, size and state of every window and your "
             + "audio devices are saved. Coming back puts them where they were.", PageWidth),
        Para("This setup saves your everyday desk setup as the \"default\" layout. It takes a couple of minutes:", PageWidth),
        Bullets(PageWidth,
            "Set up your displays exactly as you use them at your desk.",
            "Pick the audio device you use at your desk.",
            "Save the layout."),
        Para("You can open this again any time from the tray icon > Setup wizard.", PageWidth));

    Panel DisplaysPage() => Page(
        Heading("Set up your desk displays"),
        Para("A layout replays the display configuration exactly as it is when you save it. Get it right first, "
             + "in Settings > System > Display:", PageWidth),
        Bullets(PageWidth,
            "Turn on every monitor you use at your desk. Turn off any display that is not part of this setup, "
            + "such as a TV: select it and choose \"Disconnect this display\". If the TV stays on, it becomes part of the default layout.",
            "Set each monitor's resolution, and its refresh rate under Advanced display. A 144 Hz monitor left at 60 Hz is saved at 60 Hz.",
            "Drag the monitors so the arrangement matches where they sit on your desk, and pick the main display.",
            "Scale (125%, 150%) and HDR are remembered by Windows per monitor rather than by the layout. Set them once and they stay.",
            "Turn off \"Remember window locations based on monitor connection\" under Display > Multiple displays. "
            + "Otherwise Windows moves windows at the same time Display Switcher restores them, and they end up in the wrong place."),
        rememberState,
        Inline(LinkButton("Open display settings", "ms-settings:display")));

    Panel AudioPage() => Page(
        Heading("Choose your desk audio device"),
        Para("The default playback device at the moment you save is part of the layout. Make the speakers or "
             + "headset you use at your desk the default now.", PageWidth),
        audioNow,
        Bullets(PageWidth,
            "When you switch away, Display Switcher saves your current default devices (playback and communications) "
            + "and restores them when you come back, so later changes at the desk are kept.",
            "TV and HDMI audio devices only appear while that display is on. Pick the TV's audio when you create the TV layout, not now.",
            "This page updates on its own when you change the default device."),
        Inline(LinkButton("Open sound settings", "ms-settings:sound"), LinkButton("Classic sound control panel", "mmsys.cpl")));

    Panel SavePage()
    {
        foreach (var (title, _) in Columns)
            monitors.Columns.Add(title);
        var page = Page(
            Heading("Save the default layout"),
            Para("This is what will be saved. The list updates when you change the displays. Check that every "
                 + "monitor is listed, the refresh rates are right, and nothing extra (like a TV) is on.", PageWidth),
            monitors,
            saveAudio,
            Inline(saveButton),
            saveResult);
        if (store.LoadLayouts().TryGet(DefaultLayout, out _, out _))
            saveResult.Text = $"A \"{DefaultLayout}\" layout already exists. Saving replaces it, or press Next to keep it.";
        return page;
    }

    Panel NextStepsPage()
    {
        var settingsButton = new Button { Text = "Open Settings", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
        settingsButton.Click += (_, _) => openSettings();
        return Page(
            Heading("Next: add a TV layout"),
            Para("Your desk setup is saved. To add a second layout, such as TV only:", PageWidth),
            Bullets(PageWidth,
                "Turn the TV on and switch it to the PC's input.",
                "In display settings, make the TV the only active display: select each desk monitor and choose "
                + "\"Disconnect this display\". Win+P > \"Second screen only\" also works with one other display.",
                "Set the TV's resolution and refresh rate (for example 3840 x 2160 at 120 Hz), and turn on the TV's game mode.",
                "Make the TV's audio the default playback device.",
                "Right-click the tray icon > Learn current layout as > New layout..., and name it \"tv\".",
                "Switch back with the tray icon > default. Your windows and audio come back."),
            Para("Layouts other than \"default\" minimize every window on arrival so you land on a clean desktop. "
                 + "Change this per layout in layouts.yaml.", PageWidth),
            Para("Auto revert is " + (settings.AutoRevert.Enabled ? "on" : "off") + ": tray switches ask first and come back after "
                 + $"{settings.AutoRevert.Seconds} seconds unless you click Keep layout, so a TV that shows no picture can't strand you. "
                 + "Turn it off in the tray menu once your layouts work.", PageWidth),
            Heading("Home Assistant"),
            apiInfo,
            Inline(settingsButton));
    }
}
