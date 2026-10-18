; TopBar Extras - installs the companions built for TopBar:
;   * TopBar Settings      the settings window (PyInstaller build of topbar_settings.py)
;   * TopBarDisplayWatch   restarts TopBar when the monitor layout changes
;   * TopBarExtras.dll     the in-process engine (right-click Settings, themes,
;                          fonts, pills, live changes, the clock's calendar /
;                          stopwatch / timer / alarm panel, sharing usage with
;                          the other PCs on the network, Wake-on-LAN, what-is-
;                          using-it drop-downs) and the extra widgets (CPU
;                          graph, RAM in GB, GPU usage bar, network PCs)
;   * JetBrains Mono       bundled font (SIL OFL), used by the engine privately
;   * TopBar itself        only on a PC that doesn't have it yet: the copy this
;                          installer was built next to, so one run sets a new PC up
;
; Per-user install, so no admin prompt - except for the optional network
; setup (a firewall rule for the other PCs' broadcasts, and arming the network
; card for Wake-on-LAN), which runs lan-setup.ps1 behind one Windows prompt.
;
; Build: build.cmd in this folder (builds the payloads, then runs ISCC).

#define AppName "TopBar Extras"
#define AppVersion "1.5.1"
#define SettingsExe "TopBar Settings.exe"
#define WatchExe "TopBarDisplayWatch.exe"
#define TopBarDir GetEnv("APPDATA") + "\TopBar"
#define FirewallRule "TopBar Extras LAN"
#define LanPort "51736"

[Setup]
AppId={{FD187DC3-B573-4BD4-A120-BB9C1F4A4283}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=TopBarExtrasSetup
SetupIconFile=..\assets\topbar_settings.ico
UninstallDisplayIcon={app}\{#SettingsExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Setup stops the processes it needs to itself (see PrepareToInstall), so
; Restart Manager would only add a prompt.
CloseApplications=no

[Tasks]
Name: "topbar"; Description: "Install TopBar too - it isn't on this PC yet"; GroupDescription: "TopBar:"; Check: not HadTopBar
Name: "firewall"; Description: "Let the other PCs on this network show this one's usage (adds a Windows Firewall rule)"; GroupDescription: "Network (Windows asks for admin approval once):"
Name: "wake"; Description: "Let the other PCs wake this one up (Wake-on-LAN) - arms the wired network card, which reconnects for a moment. Waking from shut down may also need Wake-on-LAN on in the BIOS"; GroupDescription: "Network (Windows asks for admin approval once):"
Name: "displaywatch"; Description: "Restart TopBar automatically when a monitor turns off or on"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut for TopBar Settings"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\dist\{#SettingsExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\watch\{#WatchExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\lan-setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
; TopBar loads every DLL in its extensions folder at startup. It is left in
; place on uninstall: if config.json still names one of these widgets, removing
; the DLL would make TopBar fail to build its widgets.
Source: "..\extension\bin\TopBarExtras.dll"; DestDir: "{userappdata}\TopBar\extensions"; Flags: ignoreversion uninsneveruninstall
; The engine loads these privately from extensions\fonts; kept on uninstall
; for the same reason as the DLL.
Source: "..\assets\fonts\*"; DestDir: "{userappdata}\TopBar\extensions\fonts"; Flags: ignoreversion uninsneveruninstall
; TopBar itself, for a PC without it. Its config is written by Settings --init
; (the starter bar), never copied from this PC.
Source: "{#TopBarDir}\TopBar.exe"; DestDir: "{userappdata}\TopBar"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: topbar
Source: "{#TopBarDir}\TopBar.exe.config"; DestDir: "{userappdata}\TopBar"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: topbar
Source: "{#TopBarDir}\TopBar.exe.manifest"; DestDir: "{userappdata}\TopBar"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: topbar
Source: "{#TopBarDir}\*.dll"; DestDir: "{userappdata}\TopBar"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: topbar

[Icons]
Name: "{autoprograms}\TopBar Settings"; Filename: "{app}\{#SettingsExe}"
Name: "{autodesktop}\TopBar Settings"; Filename: "{app}\{#SettingsExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TopBarDisplayWatch"; ValueData: """{app}\{#WatchExe}"""; Flags: uninsdeletevalue; Tasks: displaywatch
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TopBar"; ValueData: "{userappdata}\TopBar\TopBar.exe"; Flags: uninsdeletevalue; Tasks: topbar

[Run]
Filename: "{app}\{#WatchExe}"; Flags: nowait; Tasks: displaywatch
Filename: "{app}\{#SettingsExe}"; Description: "Open TopBar Settings"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#WatchExe}"; Flags: runhidden; RunOnceId: "StopWatch"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""{#SettingsExe}"""; Flags: runhidden; RunOnceId: "StopSettings"

[Code]
var
  TopBarWasRunning: Boolean;
  HadTopBarAtStart: Boolean;

function TopBarExe: String;
begin
  Result := ExpandConstant('{userappdata}\TopBar\TopBar.exe');
end;

// Decided once, before anything is copied - checking as files land would
// change the answer halfway through.
function HadTopBar: Boolean;
begin
  Result := HadTopBarAtStart;
end;

// tasklist exits 0 even when nothing matches, so pipe it through find.
function IsRunning(const Image: String): Boolean;
var
  Rc: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C tasklist /NH /FI "IMAGENAME eq ' + Image + '" | find /I "' + Image + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 0);
end;

procedure Stop(const Image: String);
var
  Rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "' + Image + '"', '',
       SW_HIDE, ewWaitUntilTerminated, Rc);
end;

function FirewallRuleExists: Boolean;
var
  Rc: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\netsh.exe'),
                 'advfirewall firewall show rule name="{#FirewallRule}"', '',
                 SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 0);
end;

// lan-setup.ps1 with admin rights, behind one Windows prompt: -Firewall lets
// UDP {#LanPort} in from this network only, -Wake arms the wired card for
// Wake-on-LAN. Saying no just skips it - Settings > Network can ask again.
procedure NetworkSetup(const Switches: String);
var
  Rc: Integer;
begin
  ShellExec('runas', 'powershell.exe',
            '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'
            + ExpandConstant('{app}\lan-setup.ps1') + '"' + Switches,
            '', SW_HIDE, ewWaitUntilTerminated, Rc);
end;

function InitializeSetup: Boolean;
begin
  HadTopBarAtStart := FileExists(TopBarExe);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Anything holding a file about to be replaced has to go first. TopBar keeps
  // TopBarExtras.dll loaded, so it's stopped here and brought back afterwards.
  Stop('{#WatchExe}');
  Stop('{#SettingsExe}');
  TopBarWasRunning := IsRunning('TopBar.exe');
  if TopBarWasRunning then
    Stop('TopBar.exe');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Rc: Integer;
  Switches: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  Switches := '';
  if WizardIsTaskSelected('firewall') and not FirewallRuleExists then
    Switches := Switches + ' -Firewall';
  if WizardIsTaskSelected('wake') then
    Switches := Switches + ' -Wake';
  if Switches <> '' then
    NetworkSetup(Switches);
  // Put the engine first in config.json *before* TopBar comes back, so the
  // right-click Settings entry and live changes exist from its first start.
  // A TopBar that was only just installed gets the starter bar here too.
  if FileExists(TopBarExe) then
    Exec(ExpandConstant('{app}\{#SettingsExe}'), '--init', ExpandConstant('{app}'),
         SW_HIDE, ewWaitUntilTerminated, Rc);
  if (TopBarWasRunning or WizardIsTaskSelected('topbar')) and FileExists(TopBarExe) then
    Exec(TopBarExe, '', ExtractFileDir(TopBarExe), SW_SHOWNORMAL, ewNoWait, Rc);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Rc: Integer;
begin
  // The firewall rule, if there is one (taking it out needs admin too).
  if (CurUninstallStep = usUninstall) and FirewallRuleExists then
    ShellExec('runas', ExpandConstant('{sys}\netsh.exe'),
              'advfirewall firewall delete rule name="{#FirewallRule}"', '',
              SW_HIDE, ewWaitUntilTerminated, Rc);
end;
