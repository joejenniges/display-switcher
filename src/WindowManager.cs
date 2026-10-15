using System.Runtime.InteropServices;
using DisplaySwitcher.Native;
using static DisplaySwitcher.Native.WindowNative;

namespace DisplaySwitcher;

public sealed class WindowInfo
{
    public long Hwnd { get; set; }
    public uint Pid { get; set; }
    public string Process { get; set; } = "";
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "normal"; // normal | minimized | maximized
    public bool RestoreToMaximized { get; set; }
    // rcNormalPosition from GetWindowPlacement: workspace coordinates, physical pixels
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    // Snapped windows: on-screen rect in screen coordinates, which differs from the
    // placement rect (that one is the size the window returns to when un-snapped).
    public bool Snapped { get; set; }
    public int ActualLeft { get; set; }
    public int ActualTop { get; set; }
    public int ActualRight { get; set; }
    public int ActualBottom { get; set; }
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public bool Topmost { get; set; }
    public bool Cloaked { get; set; }
    public int ZOrder { get; set; } // 0 = top
}

public sealed class WindowSnapshot
{
    public DateTime TakenAt { get; set; }
    public long Foreground { get; set; }
    public List<WindowInfo> Windows { get; set; } = [];
}

internal static class WindowManager
{
    // Desktop, taskbars and shell flyouts (Start, search) are "visible" top-level windows too.
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    const int PlaceTimeoutMs = 2000;

    public static WindowSnapshot Capture()
    {
        uint self = (uint)Environment.ProcessId;
        var windows = new List<WindowInfo>();
        int z = 0;
        EnumWindows((h, _) =>
        {
            var info = Describe(h, self, z++);
            if (info is not null)
                windows.Add(info);
            return true;
        }, 0);
        return new WindowSnapshot { TakenAt = DateTime.Now, Foreground = GetForegroundWindow(), Windows = windows };
    }

    public static int MinimizeAll()
    {
        int count = 0;
        foreach (var w in Capture().Windows)
        {
            var h = (nint)w.Hwnd;
            // Cloaked windows live on another virtual desktop (or are suspended UWP frames);
            // minimizing them is pointless and can drag them onto the current desktop.
            if (w.Cloaked || w.State == "minimized" || IsHungAppWindow(h))
                continue;
            // Async so one unresponsive app can't stall the switch.
            if (ShowWindowAsync(h, (int)SW_SHOWMINNOACTIVE))
                count++;
        }
        return count;
    }

    public static (int Restored, int Skipped) Restore(WindowSnapshot snapshot)
    {
        int skipped = 0;
        var placed = new List<(WindowInfo Info, nint Hwnd)>();

        foreach (var w in snapshot.Windows)
        {
            var h = (nint)w.Hwnd;
            if (!StillSameWindow(h, w))
            {
                skipped++;
                continue;
            }
            if (IsHungAppWindow(h))
            {
                Log.Warn($"Skipping hung window '{w.Title}' ({w.Process})");
                skipped++;
                continue;
            }
            // WHY the timeout: SetWindowPlacement is a synchronous cross-process call. An app
            // that hangs mid-restore (Sublime Text did, right after a display change) blocks it
            // forever, which wedged the whole switch. IsHungAppWindow above only catches apps
            // that were already hung. A timed-out call is abandoned on its pool thread.
            var task = Task.Run(() => (Ok: Place(h, w), Error: Marshal.GetLastWin32Error()));
            if (!task.Wait(PlaceTimeoutMs))
            {
                Log.Warn($"Timed out placing '{w.Title}' ({w.Process}); app not responding");
                skipped++;
            }
            else if (task.Result.Ok)
            {
                placed.Add((w, h));
            }
            else
            {
                // Usually an elevated window while we are not elevated (UIPI).
                Log.Warn($"SetWindowPlacement failed for '{w.Title}' ({w.Process}): {task.Result.Error}");
                skipped++;
            }
        }

        var visible = placed.Where(p => p.Info.State != "minimized").OrderBy(p => p.Info.ZOrder).ToList();
        var foreground = visible.FirstOrDefault(p => p.Info.Hwnd == snapshot.Foreground);

        // WHY the verify loop: restoring from minimized is animated, and activations from it
        // land after SetWindowPlacement returns, re-raising windows after we restacked them
        // (seen in testing). Wait, restack, read the real order back, repeat until it holds.
        // Restacking also makes synchronous calls into other apps, so it gets the same
        // abandon-on-hang treatment as placement.
        var restack = Task.Run(() =>
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Thread.Sleep(400);
                Restack(visible);
                if (foreground.Hwnd != 0)
                    ForceForeground(foreground.Hwnd);
                Thread.Sleep(150);
                if (StackMatches(visible))
                    return;
                Log.Warn($"Stacking order not settled after attempt {attempt}");
            }
        });
        if (!restack.Wait(PlaceTimeoutMs * 4))
            Log.Warn("Restacking timed out; a window is not responding");

        return (placed.Count, skipped);
    }

    // Chains each window directly below the one that was above it.
    // WHY not HWND_TOP per window: from a background process those calls were silently
    // ignored (tested), whereas inserting below a specific window is honoured.
    static void Restack(List<(WindowInfo Info, nint Hwnd)> topToBottom)
    {
        for (int i = 1; i < topToBottom.Count; i++)
        {
            var (info, h) = topToBottom[i];
            if (!SetWindowPos(h, topToBottom[i - 1].Hwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
                Log.Warn($"Restack failed for '{info.Title}': {Marshal.GetLastWin32Error()}");
        }
    }

    static bool StackMatches(List<(WindowInfo Info, nint Hwnd)> topToBottom)
    {
        var position = new Dictionary<nint, int>();
        int z = 0;
        EnumWindows((h, _) =>
        {
            position[h] = z++;
            return true;
        }, 0);
        int last = -1;
        foreach (var (_, h) in topToBottom)
        {
            if (!position.TryGetValue(h, out int p) || p < last)
                return false;
            last = p;
        }
        return true;
    }

    static bool Place(nint h, WindowInfo w)
    {
        var wp = new WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>(),
            ptMinPosition = new POINT { X = w.MinX, Y = w.MinY },
            ptMaxPosition = new POINT { X = w.MaxX, Y = w.MaxY },
            rcNormalPosition = new RECT { Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Bottom },
        };

        if (w.State == "minimized")
        {
            // WPF_RESTORETOMAXIMIZED is only honoured with SW_SHOWMINIMIZED, which activates;
            // otherwise stay minimized without activation and just fix the restore rect.
            wp.flags = w.RestoreToMaximized ? WPF_RESTORETOMAXIMIZED : 0;
            wp.showCmd = w.RestoreToMaximized ? SW_SHOWMINIMIZED : SW_SHOWMINNOACTIVE;
            return SetWindowPlacement(h, ref wp);
        }

        // Maximized windows go through SW_SHOWNORMAL first: with SW_SHOWNOACTIVATE a minimized
        // window that was maximized comes back maximized on whatever monitor Windows parked it
        // on, and maximizing an already-maximized window doesn't move it. Normal windows use
        // SW_SHOWNOACTIVATE so restoring doesn't churn activation (and z-order).
        wp.showCmd = w.State == "maximized" ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE;
        bool ok = SetWindowPlacement(h, ref wp);
        // WHY twice: crossing between monitors with different scale factors makes the app
        // rescale on WM_DPICHANGED after the first move, leaving the size off. The second
        // call reapplies the exact rect at the new DPI.
        SetWindowPlacement(h, ref wp);

        if (w.State == "maximized")
        {
            wp.showCmd = SW_SHOWMAXIMIZED;
            ok &= SetWindowPlacement(h, ref wp);
        }
        else if (w.Snapped)
        {
            // Snap group membership can't be recreated through a public API, so put the
            // window at its snapped geometry instead. Twice for the same DPI reason as above.
            int width = w.ActualRight - w.ActualLeft, height = w.ActualBottom - w.ActualTop;
            for (int i = 0; i < 2; i++)
                ok &= SetWindowPos(h, 0, w.ActualLeft, w.ActualTop, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        return ok;
    }

    // Handles are recycled; a matching pid and class is enough to trust it's the same window.
    static bool StillSameWindow(nint h, WindowInfo w)
    {
        if (!IsWindow(h))
            return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == w.Pid && ClassName(h) == w.Class;
    }

    static void ForceForeground(nint h)
    {
        // SetForegroundWindow from a background process is normally refused; attaching to the
        // current foreground thread's input queue is the documented-enough way around it.
        var current = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(current, out _);
        uint self = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached)
                AttachThreadInput(self, fgThread, false);
        }
    }

    static WindowInfo? Describe(nint h, uint selfPid, int z)
    {
        if (!IsWindowVisible(h))
            return null;

        long ex = GetWindowLongPtr(h, GWL_EXSTYLE);
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0 || GetWindow(h, GW_OWNER) != 0))
            return null;

        var cls = ClassName(h);
        if (ShellClasses.Contains(cls))
            return null;

        var title = Title(h);
        if (title.Length == 0)
            return null;

        GetWindowThreadProcessId(h, out uint pid);
        if (pid == selfPid)
            return null;

        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(h, ref wp))
            return null;

        GetWindowRect(h, out var actual);
        var state = IsIconic(h) ? "minimized" : IsZoomed(h) ? "maximized" : "normal";

        return new WindowInfo
        {
            Hwnd = h,
            State = state,
            Snapped = state == "normal" && IsWindowArranged(h),
            ActualLeft = actual.Left,
            ActualTop = actual.Top,
            ActualRight = actual.Right,
            ActualBottom = actual.Bottom,
            Pid = pid,
            Process = ProcessPath(pid),
            Class = cls,
            Title = title,
            RestoreToMaximized = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0,
            Left = wp.rcNormalPosition.Left,
            Top = wp.rcNormalPosition.Top,
            Right = wp.rcNormalPosition.Right,
            Bottom = wp.rcNormalPosition.Bottom,
            MinX = wp.ptMinPosition.X,
            MinY = wp.ptMinPosition.Y,
            MaxX = wp.ptMaxPosition.X,
            MaxY = wp.ptMaxPosition.Y,
            Topmost = (ex & WS_EX_TOPMOST) != 0,
            Cloaked = IsCloaked(h),
            ZOrder = z,
        };
    }

    static bool IsCloaked(nint h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    static string Title(nint h)
    {
        var buf = new char[512];
        int n = InternalGetWindowText(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    static string ClassName(nint h)
    {
        var buf = new char[256];
        int n = GetClassName(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    static string ProcessPath(uint pid)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return "";
        try
        {
            var buf = new char[1024];
            int size = buf.Length;
            return QueryFullProcessImageName(process, 0, buf, ref size) ? new string(buf, 0, size) : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
