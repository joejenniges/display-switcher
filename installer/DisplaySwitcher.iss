; Inno Setup 6 script. Build with ..\build-installer.ps1, which publishes the app first.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Display Switcher"
#define AppExe "DisplaySwitcher.exe"
#define TaskName "DisplaySwitcher"
#define FirewallRule "Display Switcher"

[Setup]
; AppId identifies the product for upgrades and uninstall. Never change it.
AppId={{4A703E54-E6AF-4D7D-BB28-5AB950874284}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Joe Jenniges
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Per-machine: the logon task runs elevated and the firewall rule needs admin anyway.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004+: IsWindowArranged and per-monitor V2 DPI behaviour.
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=DisplaySwitcher-Setup-{#AppVersion}
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running instance is stopped in PrepareToInstall; Restart Manager can't close a tray app.
CloseApplications=no

[Tasks]
Name: startup; Description: "Start {#AppName} when I sign in (as administrator, without a UAC prompt)"
Name: firewall; Description: "Allow Home Assistant to reach the HTTP API (Windows Firewall, private networks only)"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP profile=private,domain enable=yes"; Flags: runhidden; Tasks: firewall
; The app writes the task itself, so the installer and the Settings checkbox produce the same task.
Filename: "{app}\{#AppExe}"; Parameters: "--register-startup"; Flags: runhidden waituntilterminated; Tasks: startup
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; Tasks: not startup
; Launch through the task so the first run is already elevated.
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN {#TaskName}"; Description: "Launch {#AppName}"; Flags: postinstall runhidden nowait; Tasks: startup
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: postinstall nowait runasoriginaluser; Tasks: not startup

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN {#TaskName} /F"; Flags: runhidden; RunOnceId: "DeleteTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Upgrade: stop the running copy so its files can be replaced. Saved window snapshots
  // live in %APPDATA% and survive this.
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN {#TaskName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  Dir := ExpandConstant('{userappdata}\display-switcher');
  if DirExists(Dir) and not UninstallSilent() then
    if MsgBox('Also delete your saved layouts, settings and log?' + #13#10 + Dir, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Dir, True, True, True);
end;
