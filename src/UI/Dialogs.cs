using DisplaySwitcher.Native;

namespace DisplaySwitcher.UI;

// Native task dialogs: DPI-correct and system-styled without hand layout.
// WHY dialogs and not tray balloons for errors: Windows 11 drops balloon notifications
// silently under Do Not Disturb, game mode or per-app notification settings, which made a
// failed switch look like nothing happened.
internal static class Dialogs
{
    public const string Caption = "Display Switcher";

    public static void Error(string heading, string text, string? details = null)
    {
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Error,
            Buttons = { TaskDialogButton.OK },
        };
        if (!string.IsNullOrEmpty(details))
            page.Expander = new TaskDialogExpander(details) { CollapsedButtonText = "Details", ExpandedButtonText = "Hide details" };
        Show(page);
    }

    public static bool Confirm(string heading, string text, string okText, TaskDialogIcon? icon = null)
    {
        var ok = new TaskDialogButton(okText);
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = icon ?? TaskDialogIcon.Information,
            Buttons = { ok, TaskDialogButton.Cancel },
            DefaultButton = ok,
            AllowCancel = true,
        };
        return Show(page) == ok;
    }

    // Shown on the new layout after an auto-revert switch. Returns true to keep it. Times out
    // to revert, like Windows' own "Keep these display settings?" prompt, so a TV that shows
    // no picture still comes back on its own.
    public static bool KeepOrRevert(string layout, string previous, int seconds)
    {
        var keep = new TaskDialogButton("Keep layout");
        var revert = new TaskDialogButton($"Revert to '{previous}'");
        var progress = new TaskDialogProgressBar { Minimum = 0, Maximum = seconds, Value = seconds };
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = $"Keep the '{layout}' layout?",
            Text = Remaining(previous, seconds),
            Icon = TaskDialogIcon.Information,
            Buttons = { keep, revert },
            // Enter reverts, as in Windows' own display prompt: the safe choice when the
            // screen may not be showing anything.
            DefaultButton = revert,
            ProgressBar = progress,
        };

        int left = seconds;
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            left--;
            if (left <= 0)
            {
                timer.Stop();
                revert.PerformClick();
                return;
            }
            progress.Value = left;
            page.Text = Remaining(previous, left);
        };
        page.Created += (_, _) => timer.Start();
        var result = Show(page);
        timer.Stop();
        return result == keep;
    }

    static string Remaining(string previous, int seconds) =>
        $"Switching back to '{previous}' in {seconds} second{(seconds == 1 ? "" : "s")}.";

    static TaskDialogButton Show(TaskDialogPage page)
    {
        page.Created += (_, _) => BringToFront(page.Caption ?? Caption);
        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }

    // Tray apps have no foreground window to own the dialog, so it can open behind whatever
    // is active. Topmost keeps it visible even when foreground activation is refused.
    static void BringToFront(string caption)
    {
        var buffer = new char[256];
        WindowNative.EnumThreadWindows(WindowNative.GetCurrentThreadId(), (hwnd, _) =>
        {
            int n = WindowNative.GetClassName(hwnd, buffer, buffer.Length);
            if (new string(buffer, 0, n) != "#32770")
                return true;
            n = WindowNative.InternalGetWindowText(hwnd, buffer, buffer.Length);
            if (new string(buffer, 0, n) != caption)
                return true;
            WindowNative.SetWindowPos(hwnd, WindowNative.HWND_TOPMOST, 0, 0, 0, 0,
                WindowNative.SWP_NOMOVE | WindowNative.SWP_NOSIZE);
            WindowNative.SetForegroundWindow(hwnd);
            return true;
        }, 0);
    }
}
