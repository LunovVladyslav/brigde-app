; GearBoard Bridge — InnoSetup Installer Script
; Requires: Inno Setup 6.x  (https://jrsoftware.org/isdl.php)
;
; Build:
;   iscc installer\setup.iss
;
; Output:
;   installer\Output\GearBoardBridge-Setup-1.0.0.exe

#define AppName       "GearBoard Bridge"
#define AppVersion    "1.0.0"
#define AppPublisher  "GearBoard"
#define AppURL        "https://github.com/LunovVladyslav/brigde-app"
#define AppExeName    "GearBoardBridge.exe"
#define AppGUID       "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"

; Path to the published single-file exe (relative to this .iss file)
#define PublishDir    "..\src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{#AppGUID}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; Require Windows 10 1903+ (build 18362)
MinVersion=10.0.18362
; Request admin rights (needed for firewall rule)
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=GearBoardBridge-Setup-{#AppVersion}
SetupIconFile=
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; Don't restart by default
RestartIfNeededByRun=no
; Show "Launch GearBoard Bridge" checkbox on finish page
[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupentry"; Description: "Start GearBoard Bridge automatically with Windows"; GroupDescription: "Windows startup:"; Flags: unchecked

[Files]
; Main application — single self-contained exe
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
; Desktop (optional, unchecked by default)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Windows startup entry (only created if user selects the task)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "GearBoardBridge"; \
  ValueData: """{app}\{#AppExeName}"" --minimized"; \
  Flags: uninsdeletevalue; Tasks: startupentry

[Code]
// ── Firewall rule helpers ────────────────────────────────────────────────────

procedure AddFirewallRule();
var
  ResultCode: Integer;
begin
  // Add inbound UDP rule for WiFi MIDI discovery (port 5004)
  Exec('netsh.exe',
       'advfirewall firewall add rule ' +
       'name="GearBoard Bridge WiFi MIDI" ' +
       'dir=in action=allow protocol=UDP localport=5004 ' +
       'profile=private,domain ' +
       'description="Allows GearBoard Android app to discover and connect via WiFi MIDI"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveFirewallRule();
var
  ResultCode: Integer;
begin
  Exec('netsh.exe',
       'advfirewall firewall delete rule name="GearBoard Bridge WiFi MIDI"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// ── loopMIDI check ───────────────────────────────────────────────────────────

function IsLoopMidiInstalled(): Boolean;
var
  DllPath: String;
begin
  DllPath := ExpandConstant('{sys}\teVirtualMIDI64.dll');
  Result   := FileExists(DllPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    AddFirewallRule();

    // Warn if loopMIDI is not installed
    if not IsLoopMidiInstalled() then
    begin
      MsgBox(
        'GearBoard Bridge requires loopMIDI to create virtual MIDI ports.' + #13#10 +
        #13#10 +
        'Please download and install loopMIDI from:' + #13#10 +
        'https://www.tobias-erichsen.de/software/loopmidi.html' + #13#10 +
        #13#10 +
        'After installing loopMIDI, GearBoard Bridge will work automatically.',
        mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFirewallRule();
end;
