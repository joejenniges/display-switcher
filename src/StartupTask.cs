using System.Diagnostics;
using System.Security;
using System.Xml.Linq;

namespace DisplaySwitcher;

// The "start at logon" scheduled task. A Run-key or Startup-folder entry can't start the app
// elevated without a UAC prompt at every logon; a task with RunLevel Highest can.
internal static class StartupTask
{
    public const string Name = "DisplaySwitcher";

    // Path of the exe the task starts, or null when no task is registered.
    public static string? RegisteredExe()
    {
        var (code, output) = Schtasks("/Query", "/TN", Name, "/XML");
        if (code != 0)
            return null;
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return XDocument.Parse(output).Descendants(ns + "Command").FirstOrDefault()?.Value.Trim('"');
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not parse scheduled task XML: {ex.Message}");
            return "";
        }
    }

    public static bool IsRegisteredForThisExe() =>
        string.Equals(RegisteredExe(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    // Registering a Highest task needs admin. When not elevated, re-run this exe elevated with
    // --register-startup / --unregister-startup and wait for it.
    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        if (Elevation.IsElevated())
            return enabled ? Register(out error) : Unregister(out error);

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = enabled ? "--register-startup" : "--unregister-startup",
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode == 0)
                return true;
            error = $"Task registration failed (exit code {p.ExitCode}); see the log";
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error = "Administrator approval was declined";
            return false;
        }
    }

    public static bool Register(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath!;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        // Task XML instead of schtasks flags: the flags can't turn off the 3-day execution limit
        // or the "only on AC power" conditions, and can't set a logon delay.
        // Priority 5: the task default (7) runs the process at below-normal priority.
        var xml = $"""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Display Switcher at logon.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(exe)}"</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        // WHY the COM API and not "schtasks /Create /XML <file>": this runs elevated, and a
        // file in %TEMP% can be swapped by an unelevated process between writing it and
        // schtasks reading it, registering an arbitrary elevated task. RegisterTask takes the
        // XML as a string, so there is nothing to swap.
        object? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            dynamic svc = service;
            svc.Connect();
            dynamic folder = svc.GetFolder("\\");
            folder.RegisterTask(Name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            Log.Info($"Registered startup task for {user}: {exe}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"Registering the startup task failed: {ex}");
            return false;
        }
        finally
        {
            if (service is not null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
    }

    const int TASK_CREATE_OR_UPDATE = 6;
    const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    // Starts the registered task now (elevated, no UAC prompt).
    public static bool Run()
    {
        var (code, output) = Schtasks("/Run", "/TN", Name);
        if (code != 0)
            Log.Warn($"schtasks /Run failed ({code}): {output.Trim()}");
        return code == 0;
    }

    public static bool Unregister(out string? error)
    {
        error = null;
        if (RegisteredExe() is null)
            return true;
        var (code, output) = Schtasks("/Delete", "/TN", Name, "/F");
        if (code == 0)
        {
            Log.Info("Removed startup task");
            return true;
        }
        error = output.Trim();
        Log.Warn($"schtasks /Delete failed ({code}): {error}");
        return false;
    }

    static (int Code, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
