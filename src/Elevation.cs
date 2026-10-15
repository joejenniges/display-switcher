using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DisplaySwitcher;

internal static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Relaunches this exe through UAC with the same arguments. Returns false if the user
    // declined the prompt.
    public static bool Relaunch(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
