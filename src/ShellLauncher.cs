using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DisplaySwitcher;

// Opens files, URIs and programs WITHOUT this process's elevation.
//
// WHY: the app usually runs elevated (logon task). Launch targets come from layouts.yaml in
// %APPDATA%, and ShellExecute resolves file/protocol associations through HKCU; a
// non-elevated process can change both. Starting them from the elevated process would hand
// any such process silent admin rights (a UAC bypass). Instead the request is passed to
// Explorer's desktop shell, which runs it with the user's normal token.
// There is deliberately no fallback to an elevated launch.
internal static class ShellLauncher
{
    public static void Open(string target, string? args = null, string? workingDir = null)
    {
        if (!Elevation.IsElevated())
        {
            Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true, WorkingDirectory = workingDir ?? "" })?.Dispose();
            return;
        }

        // Shell objects want an STA; callers include thread-pool and Kestrel threads.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                ShellExecuteViaDesktop(target, args ?? "", workingDir ?? "");
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Explorer did not respond");
        if (error is not null)
            throw new InvalidOperationException($"Could not start '{target}' through Explorer: {error.Message}", error);
    }

    // Opens a target and logs instead of throwing; for menu items and buttons.
    public static void TryOpen(string target)
    {
        try
        {
            Open(target);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    static void ShellExecuteViaDesktop(string file, string args, string dir)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
        object? shellWindows = null, desktop = null, browser = null, view = null, background = null;
        try
        {
            shellWindows = Activator.CreateInstance(shellWindowsType)!;
            object loc = 0; // CSIDL_DESKTOP
            object? root = null;
            desktop = ((dynamic)shellWindows).FindWindowSW(ref loc, ref root, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH)
                ?? throw new InvalidOperationException("Explorer desktop not found (is Explorer running?)");

            var sid = SID_STopLevelBrowser;
            var iid = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IOleServiceProvider)desktop).QueryService(ref sid, ref iid, out browser));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browser).QueryActiveShellView(out var shellView));
            view = shellView;
            var idispatch = IID_IDispatch;
            Marshal.ThrowExceptionForHR(shellView.GetItemObject(SVGIO_BACKGROUND, ref idispatch, out background));

            dynamic folderView = background;
            dynamic shell = folderView.Application;
            try
            {
                shell.ShellExecute(file, args, dir, "", SW_SHOWNORMAL);
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        finally
        {
            foreach (var o in new[] { background, view, browser, desktop, shellWindows })
            {
                if (o is not null && Marshal.IsComObject(o))
                    Marshal.ReleaseComObject(o);
            }
        }
    }

    const int SWC_DESKTOP = 8;
    const int SWFO_NEEDDISPATCH = 1;
    const uint SVGIO_BACKGROUND = 0;
    const int SW_SHOWNORMAL = 1;
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    // Only QueryActiveShellView is called; the other slots keep the vtable order.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig]
        int QueryActiveShellView(out IShellView view);
    }

    // Only GetItemObject is called; the other slots keep the vtable order.
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [PreserveSig]
        int GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }
}
