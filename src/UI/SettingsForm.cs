using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DisplaySwitcher.UI;

internal sealed class SettingsForm : DialogForm
{
    const int TextWidth = 430;

    readonly ConfigStore store;
    readonly Settings live;
    readonly ApiHost api;
    readonly bool startupInitial;

    readonly CheckBox apiEnabled = new() { Text = "Enable the HTTP API (Home Assistant, scripts)", AutoSize = true };
    readonly ComboBox host = new() { Width = 150 };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    readonly TextBox token = new() { Width = 250, UseSystemPasswordChar = true };
    readonly Label tokenWarning = new() { AutoSize = true, MaximumSize = new Size(TextWidth, 0), ForeColor = Color.Firebrick, Margin = new Padding(3, 0, 3, 8) };
    readonly Label apiStatus = new() { AutoSize = true, MaximumSize = new Size(TextWidth, 0) };

    readonly CheckBox autoRevert = new() { Text = "Auto revert tray switches after", AutoSize = true, Margin = new Padding(3, 5, 0, 3) };
    readonly NumericUpDown revertSeconds = new() { Minimum = 5, Maximum = 300, Width = 60 };
    readonly CheckBox startup = new() { Text = "Start when I sign in (as administrator, without a UAC prompt)", AutoSize = true };
    readonly Label startupNote = new() { AutoSize = true, MaximumSize = new Size(TextWidth, 0), ForeColor = SystemColors.GrayText };
    readonly CheckBox notifySuccess = new() { Text = "Show a notification after each successful switch", AutoSize = true };
    readonly CheckBox notifyApiFailure = new() { Text = "Show an error dialog when a Home Assistant / API switch fails", AutoSize = true };

    readonly NumericUpDown settleTimeout = Millis();
    readonly NumericUpDown settleDelay = Millis();
    readonly NumericUpDown audioTimeout = Millis();

    public SettingsForm(ConfigStore store, Settings live, ApiHost api)
    {
        this.store = store;
        this.live = live;
        this.api = api;

        Text = "Display Switcher Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12) };

        // ---- API
        var apiBox = Group("Home Assistant / HTTP API", out var g);
        Row(g, null, apiEnabled);
        Row(g, "Listen address:", Inline(host, new Label { Text = "0.0.0.0 = all network interfaces", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 6, 0, 0) }));
        Row(g, "Port:", port);
        var show = new Button { Text = "Show", AutoSize = true };
        show.Click += (_, _) =>
        {
            token.UseSystemPasswordChar = !token.UseSystemPasswordChar;
            show.Text = token.UseSystemPasswordChar ? "Show" : "Hide";
        };
        var generate = new Button { Text = "Generate", AutoSize = true };
        generate.Click += (_, _) =>
        {
            token.Text = Settings.NewToken();
            token.UseSystemPasswordChar = false;
            show.Text = "Hide";
        };
        Row(g, "Token:", Inline(token, show, generate));
        Row(g, null, Para("Requests must send \"Authorization: Bearer <token>\". The Home Assistant config below includes it.", TextWidth));
        Row(g, null, tokenWarning);
        var copyHa = new Button { Text = "Copy Home Assistant config", AutoSize = true };
        copyHa.Click += (_, _) =>
        {
            CopyWithoutHistory(HomeAssistantYaml());
            copyHa.Text = "Copied";
        };
        Row(g, null, Inline(copyHa));
        Row(g, null, apiStatus);

        // ---- behaviour
        var behaviourBox = Group("Behaviour", out var b);
        Row(b, null, Inline(autoRevert, revertSeconds, new Label { Text = "seconds", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        Row(b, null, Para("Before a switch from the tray menu, asks for confirmation and then switches back unless you click Keep layout. Home Assistant switches never revert.", TextWidth));
        Row(b, null, startup);
        Row(b, null, startupNote);
        Row(b, null, notifySuccess);
        Row(b, null, notifyApiFailure);

        // ---- timing
        var timingBox = Group("Timing (milliseconds)", out var t);
        Row(t, "Display settle timeout:", settleTimeout);
        Row(t, "Delay before moving windows:", settleDelay);
        Row(t, "Audio device wait:", audioTimeout);

        // ---- buttons
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
        var folder = new Button { Text = "Open config folder", AutoSize = true };
        folder.Click += (_, _) => OpenShell(store.Dir);
        save.Click += (_, _) => Save();
        var buttons = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(folder, 0, 0);
        buttons.Controls.Add(save, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);

        root.Controls.Add(apiBox);
        root.Controls.Add(behaviourBox);
        root.Controls.Add(timingBox);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        apiEnabled.CheckedChanged += (_, _) => host.Enabled = port.Enabled = token.Enabled = show.Enabled = generate.Enabled = apiEnabled.Checked;
        token.TextChanged += (_, _) => UpdateTokenWarning();
        host.TextChanged += (_, _) => UpdateTokenWarning();
        apiEnabled.CheckedChanged += (_, _) => UpdateTokenWarning();
        autoRevert.CheckedChanged += (_, _) => revertSeconds.Enabled = autoRevert.Checked;

        // ---- values
        var s = live.Clone();
        host.Items.AddRange(["0.0.0.0", "127.0.0.1", .. LocalAddresses()]);
        host.Text = s.Listen.Host;
        port.Value = Math.Clamp(s.Listen.Port, 1, 65535);
        token.Text = s.Token ?? "";
        apiEnabled.Checked = s.Listen.Enabled;
        autoRevert.Checked = s.AutoRevert.Enabled;
        revertSeconds.Value = Math.Clamp(s.AutoRevert.Seconds, 5, 300);
        notifySuccess.Checked = s.Notifications.OnSuccess;
        notifyApiFailure.Checked = s.Notifications.OnApiFailure;
        settleTimeout.Value = Math.Clamp(s.Switching.SettleTimeoutMs, 0, 120000);
        settleDelay.Value = Math.Clamp(s.Switching.SettleDelayMs, 0, 120000);
        audioTimeout.Value = Math.Clamp(s.Switching.AudioTimeoutMs, 0, 120000);

        var registered = StartupTask.RegisteredExe();
        startupInitial = string.Equals(registered, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        startup.Checked = startupInitial;
        startupNote.Text = registered switch
        {
            null => "Not registered.",
            _ when startupInitial => "Registered as the scheduled task \"DisplaySwitcher\".",
            _ => $"The \"DisplaySwitcher\" task starts a different copy: {registered}. Check the box to point it at this one.",
        };
        UpdateApiStatus();
        UpdateTokenWarning();

        EndBuild();
    }

    void UpdateTokenWarning()
    {
        var h = host.Text.Trim();
        bool lan = h != "127.0.0.1" && h != "::1" && !h.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        tokenWarning.Visible = apiEnabled.Checked && lan && string.IsNullOrWhiteSpace(token.Text);
        tokenWarning.Text = "No token: any device on your network can switch this PC's displays. Click Generate, then update Home Assistant.";
    }

    // The text contains the token: keep it out of Win+V history and cloud clipboard sync.
    static void CopyWithoutHistory(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true);
    }

    void UpdateApiStatus()
    {
        apiStatus.ForeColor = api.Error is null ? SystemColors.ControlText : Color.Firebrick;
        apiStatus.Text = api.Running ? $"Running on {api.Url}"
            : api.Error is { } e ? $"Not running: {e}"
            : "Not running (disabled).";
    }

    void Save()
    {
        var hostText = host.Text.Trim();
        if (!IPAddress.TryParse(hostText, out _) && !hostText.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Error("Invalid listen address", "Use an IPv4 or IPv6 address such as 0.0.0.0, 127.0.0.1 or this PC's LAN address.");
            return;
        }

        var next = live.Clone();
        next.Listen.Enabled = apiEnabled.Checked;
        next.Listen.Host = hostText;
        next.Listen.Port = (int)port.Value;
        next.Token = string.IsNullOrWhiteSpace(token.Text) ? null : token.Text.Trim();
        next.AutoRevert.Enabled = autoRevert.Checked;
        next.AutoRevert.Seconds = (int)revertSeconds.Value;
        next.Notifications.OnSuccess = notifySuccess.Checked;
        next.Notifications.OnApiFailure = notifyApiFailure.Checked;
        next.Switching.SettleTimeoutMs = (int)settleTimeout.Value;
        next.Switching.SettleDelayMs = (int)settleDelay.Value;
        next.Switching.AudioTimeoutMs = (int)audioTimeout.Value;

        bool apiChanged = next.Listen.Enabled != live.Listen.Enabled
            || next.Listen.Host != live.Listen.Host
            || next.Listen.Port != live.Listen.Port
            || next.Token != live.Token
            || (next.Listen.Enabled && !api.Running);

        live.CopyFrom(next);
        try
        {
            store.SaveSettings(live);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Dialogs.Error("Settings were not saved", ex.Message);
            return;
        }
        Log.Info("Settings saved");

        if (startup.Checked != startupInitial)
        {
            UseWaitCursor = true;
            bool ok = StartupTask.SetEnabled(startup.Checked, out var error);
            UseWaitCursor = false;
            if (!ok)
            {
                Dialogs.Error("Start at sign-in was not changed", error ?? "Unknown error");
                return;
            }
        }

        if (apiChanged && !api.Restart())
        {
            UpdateApiStatus();
            Dialogs.Error("Settings saved, but the HTTP API could not start",
                $"{api.Error}\n\nAnother program may be using port {live.Listen.Port}. Pick another port or listen address.");
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    string HomeAssistantYaml()
    {
        var address = LocalAddresses().FirstOrDefault() ?? "PC_IP";
        if (host.Text.Trim() is { } h && h != "0.0.0.0" && h != "127.0.0.1" && h.Length > 0)
            address = h;
        var auth = string.IsNullOrWhiteSpace(token.Text)
            ? ""
            : $"\n    headers:\n      Authorization: \"Bearer {token.Text.Trim()}\"";
        // $$$: the payload contains literal {{ }} for Home Assistant's template engine.
        return $$$"""
            rest_command:
              pc_layout:
                url: "http://{{{address}}}:{{{port.Value}}}/layout"
                method: POST
                timeout: 60
                content_type: "application/json"
                payload: '{"layout": "{{ layout }}"}'{{{auth}}}

            """;
    }

    // IPv4 addresses of interfaces that have a default gateway, i.e. real LAN adapters
    // rather than Hyper-V / WSL / VPN virtual switches.
    static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(gw => gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    static NumericUpDown Millis() => new() { Minimum = 0, Maximum = 120000, Increment = 500, Width = 80, ThousandsSeparator = true };

    static GroupBox Group(string text, out TableLayoutPanel grid)
    {
        grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var box = new GroupBox
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 10),
        };
        box.Controls.Add(grid);
        return box;
    }

    static void Row(TableLayoutPanel grid, string? label, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label is null)
        {
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
            return;
        }
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 8, 3) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }
}
