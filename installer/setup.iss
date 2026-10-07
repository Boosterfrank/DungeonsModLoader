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
; On by default (the user can untick it). Silent installs never change an existing registration (see ShouldRegisterNxm).
Name: "nxmhandler"; Description: "Open Nexus Mods ""Mod Manager Download"" links (nxm://) with {#MyAppName}"; GroupDescription: "Nexus Mods:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same shape the app writes from Settings > "Handle nxm:// links". Removed on uninstall only when it still points here (see [Code]).
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: ""; ValueData: "URL:Nexus Mods Protocol"; Tasks: nxmhandler; Check: ShouldRegisterNxm
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: nxmhandler; Check: ShouldRegisterNxm
Root: HKCU; Subkey: "Software\Classes\nxm"; ValueType: string; ValueName: "Registered by"; ValueData: "{#MyAppName}"; Tasks: nxmhandler; Check: ShouldRegisterNxm
Root: HKCU; Subkey: "Software\Classes\nxm\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: nxmhandler; Check: ShouldRegisterNxm
Root: HKCU; Subkey: "Software\Classes\nxm\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: nxmhandler; Check: ShouldRegisterNxm

[Run]
; Also runs after a silent install, so the app's own self-update (/SILENT) brings the new version back up.
; Pass /NOLAUNCH=1 to suppress it (automated installs).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall; Check: ShouldLaunch

[Code]
const
  NxmCommandKey = 'Software\Classes\nxm\shell\open\command';

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

// [Registry] check for the nxm:// entries. Interactive installs follow the task tick box (on by default). Silent
// installs (automation, the app's own self-update) only write the registration when nothing handles the links yet
// or this install already does, so an update never takes them away from another program the user chose.
function ShouldRegisterNxm(): Boolean;
var
  Command: String;
begin
  if not WizardSilent then
  begin
    Result := True;
    Exit;
  end;

  Command := CurrentNxmCommand();
  Result := (Command = '') or NxmPointsToThisApp(Command);
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
