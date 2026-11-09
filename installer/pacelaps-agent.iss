; The PaceLaps Agent installer (agent polish plan 3.6): per user, no administrator rights, a Start menu entry, an
; optional desktop icon, Start with Windows offered at the end, and an uninstall entry that asks whether to keep the
; settings, pairing and logs. Built by scripts/publish.ps1 -Installer with Inno Setup 6 (ISCC.exe), from the same
; single exe and texts as the zip:
;   ISCC /DAppVersion=0.13.0 /DSourceDir=<staging folder> /DOutputDir=<artifacts> installer\pacelaps-agent.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{5C2F0E8B-7D1A-4E3C-9A61-2B7E4F9D3C10}
AppName=PaceLaps Agent
AppVersion={#AppVersion}
AppPublisher=Christopher Monje, PaceLaps
AppPublisherURL=https://pacelaps.com
AppSupportURL=https://pacelaps.com/docs/race-control/agent
AppUpdatesURL=https://pacelaps.com/download
DefaultDirName={localappdata}\Programs\PaceLaps Agent
DefaultGroupName=PaceLaps Agent
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=pacelaps-agent-{#AppVersion}-setup
SetupIconFile=..\src\PaceLaps.Agent.App\Assets\pacelaps-agent.ico
UninstallDisplayIcon={app}\pacelaps-agent.exe
UninstallDisplayName=PaceLaps Agent
LicenseFile={#SourceDir}\LICENSE.txt
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; Close a running agent before files are replaced (its single-instance lock, Hosting/Tray/SingleInstance).
AppMutex=PaceLaps.Agent
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put an icon on the desktop"; Flags: unchecked
Name: "autostart"; Description: "Start with Windows (it starts asleep)"; Flags: unchecked

[Files]
Source: "{#SourceDir}\pacelaps-agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\dotnet-runtime-notices.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*-OFL.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PaceLaps Agent"; Filename: "{app}\pacelaps-agent.exe"
Name: "{userdesktop}\PaceLaps Agent"; Filename: "{app}\pacelaps-agent.exe"; Tasks: desktopicon

[Registry]
; The same value the agent's own Start with Windows setting writes (RunKeyAutoStart); removed on uninstall either way.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PaceLaps Agent"; ValueData: """{app}\pacelaps-agent.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PaceLaps Agent"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\pacelaps-agent.exe"; Description: "Start PaceLaps Agent"; Flags: nowait postinstall skipifsilent

[Code]
// Uninstall: the settings, the pairing and the logs live in %LOCALAPPDATA%\PaceLaps\Agent, apart from the program.
// They stay unless the operator says to remove them (an installed and an unzipped agent share them).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\PaceLaps\Agent');
    if DirExists(Data) and not UninstallSilent() then
      if MsgBox('Also remove the agent''s settings, pairing and logs?' + #13#10 + #13#10 +
                'Keep them if you install the agent again later. Revoke the agent in race control either way.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
