; DungeonsModLoader installer (Inno Setup 6).
; Built by build.ps1, which publishes the app and passes the version and the publish folder:
;   ISCC.exe /DMyAppVersion=0.1.0 /DPublishDir=..\publish\win-x64 installer\setup.iss
; Per-user install (no admin rights), Start menu + optional desktop shortcut, optional nxm:// link handling.
; The uninstaller never touches the user's mods and only asks whether to remove the app data.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#define MyAppName "DungeonsModLoader"
#define MyAppPublisher "Boosterfrank"
#define MyAppURL "https://github.com/Boosterfrank/DungeonsModLoader"
#define MyAppExeName "DungeonsModLoader.exe"

[Setup]
AppId={{7D0B9E7A-6C0D-4F42-9A3B-2B7E1D5A1C33}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
SetupIconFile=..\src\DungeonsModLoader.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Default decided in code: on when nothing (or this app) handles nxm:// links, off when another mod manager does.
Name: "nxmhandler"; Description: "Open Nexus Mods ""Mod Manager Download"" links (nxm://) with {#MyAppName}"; GroupDescription: "Nexus Mods:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same shape the app writes from Settings > "Handle nxm:// links". Removed on uninstall only when it still points here (see [Code]).
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: ""; ValueData: "URL:Nexus Mods Protocol"; Tasks: nxmhandler
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: nxmhandler
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: "Registered by"; ValueData: "{#MyAppName}"; Tasks: nxmhandler
Root: HKCU; Subkey: "Software\Classes\nxm\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: nxmhandler
Root: HKCU; Subkey: "Software\Classes\nxm\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: nxmhandler

[Run]
; Also runs after a silent install, so the app's own self-update (/SILENT) brings the new version back up.
; Pass /NOLAUNCH=1 to suppress it (automated installs).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall; Check: ShouldLaunch

[Code]
const
  NxmCommandKey = 'Software\Classes\nxm\shell\open\command';

var
  TasksDefaulted: Boolean;

// The command currently registered for nxm:// links, or '' when none.
function CurrentNxmCommand(): String;
begin
  if not RegQueryStringValue(HKCU, NxmCommandKey, '', Result) then
    Result := '';
end;

// True when the registered command runs the copy being installed / uninstalled ({app}\DungeonsModLoader.exe).
// Another copy at another path (a development build, a portable copy) counts as "another program".
function NxmPointsToThisApp(const Command: String): Boolean;
begin
  Result := Pos(Lowercase(ExpandConstant('{app}\{#MyAppExeName}')), Lowercase(Command)) > 0;
end;

// [Run] check: launch after install unless /NOLAUNCH=1 was given.
function ShouldLaunch(): Boolean;
begin
  Result := ExpandConstant('{param:NOLAUNCH|0}') <> '1';
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Command: String;
begin
  // Silent installs (automation, the app's own self-update) never change the registration: an in-place update
  // keeps a registration that already points to {app}, and nothing else is touched.
  if WizardSilent then
    Exit;

  if (CurPageID = wpSelectTasks) and not TasksDefaulted then
  begin
    TasksDefaulted := True;
    Command := CurrentNxmCommand();
    // Nothing registered, or this install already: pre-select. Another program (e.g. Vortex): leave it unchecked so
    // an install never takes the links over silently; the app offers the switch itself when a download needs it.
    if (Command = '') or NxmPointsToThisApp(Command) then
      WizardSelectTasks('nxmhandler');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove the nxm:// registration only when it still points to this install.
    Command := CurrentNxmCommand();
    if (Command <> '') and NxmPointsToThisApp(Command) then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\nxm');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Mods in the game folder are never touched. App data (settings, profiles, cache, logs, backups) only on request.
    DataDir := ExpandConstant('{localappdata}\{#MyAppName}');
    if (not UninstallSilent) and DirExists(DataDir) then
      if MsgBox('Also remove the app data (settings, profiles, cache, logs and mod backups)?' #13#10#13#10
                + 'Your mods in the game folder stay as they are either way.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
