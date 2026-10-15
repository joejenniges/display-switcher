
namespace DisplaySwitcher.UI;

internal static class AppIcon
{
    static Icon? full;

    public static Icon Full => full ??= Load();

    public static Icon Small() => new(Full, SystemInformation.SmallIconSize);

    static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}

// Base for code-built forms. Sizes in derived forms are written at 96 DPI; AutoScaleMode.Dpi
// rescales them for the monitor the form opens on and again when it is dragged to another.
// Derived constructors build controls and then call EndBuild().
internal abstract class DialogForm : Form
{
    protected DialogForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Full;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
    }

    protected void EndBuild()
    {
        ResumeLayout(false);
        PerformLayout();
    }

    // Opened from the tray, the form can land behind the active window.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }

    protected static Label Para(string text, int width) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 0, 0, 8),
    };

    protected static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 13F),
        Margin = new Padding(0, 0, 0, 10),
    };

    // Bulleted list with a hanging indent, so wrapped lines align with the text.
    protected static TableLayoutPanel Bullets(int width, params string[] items)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var item in items)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = "•", AutoSize = true, Margin = new Padding(4, 0, 6, 6) }, 0, row);
            table.Controls.Add(new Label { Text = item, AutoSize = true, MaximumSize = new Size(width - 20, 0), Margin = new Padding(0, 0, 0, 6) }, 1, row);
        }
        return table;
    }

    protected static Button LinkButton(string text, string target)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1) };
        b.Click += (_, _) => OpenShell(target);
        return b;
    }

    protected static FlowLayoutPanel Inline(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    // Through ShellLauncher: ms-settings: and file associations resolve via HKCU.
    public static void OpenShell(string target) => ShellLauncher.TryOpen(target);
}
