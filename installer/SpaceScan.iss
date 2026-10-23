; Inno Setup script for SpaceScan. Build it through build.ps1, which compiles the app first and
; passes the version in.

#define AppName "SpaceScan"
#define AppExe "SpaceScan.exe"
#define AppPublisher "Protagonist Labs"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{5B7F3C21-9D4A-4E86-B0C7-2A9E6F1D8B53}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per machine since 1.4.0, in the studio's common folder (vault: Ecosistem-v3-foldere.md): installing and every update
; ask for UAC. The app itself still runs without admin; reading the MFT asks per scan. The ExePath key and the Explorer
; menu stay per user (HKCU), the app manages them; the code at the end moves the data and replaces the old per-user install.
PrivilegesRequired=admin
DefaultDirName={autopf}\ProtagonistLabs\{#AppName}
; the user's data, keys and old per-user install are handled on purpose, for the user who runs setup
UsedUserAreasWarning=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=SpaceScan-{#AppVersion}-setup
SetupIconFile=..\SpaceScan.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "shellmenu"; Description: "Add ""Scan with SpaceScan"" to the right-click menu for folders and drives"; GroupDescription: "Windows integration:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\SpaceScan.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SpaceScan.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Where we are installed, so File Labs (and anything else) can offer "Scan with SpaceScan".
; The app rewrites this on every start; the installer sets it before the first run.
Root: HKCU; Subkey: "Software\SpaceScan"; ValueType: string; ValueName: "ExePath"; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey

Root: HKCU; Subkey: "Software\Classes\Directory\shell\SpaceScan"; ValueType: string; ValueName: ""; ValueData: "Scan with SpaceScan"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\SpaceScan"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\SpaceScan\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey; Tasks: shellmenu

Root: HKCU; Subkey: "Software\Classes\Drive\shell\SpaceScan"; ValueType: string; ValueName: ""; ValueData: "Scan with SpaceScan"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\SpaceScan"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\SpaceScan\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey; Tasks: shellmenu

Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SpaceScan"; ValueType: string; ValueName: ""; ValueData: "Scan with SpaceScan"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SpaceScan"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SpaceScan\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%V"""; Flags: uninsdeletekey; Tasks: shellmenu

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start SpaceScan"; Flags: nowait postinstall skipifsilent runasoriginaluser
; an update from inside the app is silent: start the new version again, as the user, not elevated
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
const
  OldKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5B7F3C21-9D4A-4E86-B0C7-2A9E6F1D8B53}_is1';

function IsEmptyDir(Dir: String): Boolean;
var
  F: TFindRec;
begin
  Result := True;
  if FindFirst(Dir + '\*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then begin Result := False; Exit; end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

{ An update over the old per-user install keeps the user's choice about the Explorer menu: it was a task there too. }
procedure InitializeWizard();
var
  Tasks: String;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, OldKey, 'Inno Setup: Selected Tasks', Tasks) and (Pos('shellmenu', Tasks) = 0) then
    WizardSelectTasks('!shellmenu');
end;

{ The data folder, %APPDATA%\SpaceScan -> %APPDATA%\ProtagonistLabs\SpaceScan (exclusions, scan history): the same rule
  as the app's Ecosystem.MoveData, which runs at every start too. A rename, never a copy; a new folder that already
  holds something is never mixed with the old one; a rename that fails leaves both as they are (the app retries). }
procedure MoveData();
var
  Old, New: String;
begin
  Old := ExpandConstant('{userappdata}\SpaceScan');
  New := ExpandConstant('{userappdata}\ProtagonistLabs\SpaceScan');
  if not DirExists(Old) then Exit;
  if DirExists(New) then
  begin
    if not IsEmptyDir(New) then Exit;
    RemoveDir(New);
  end;
  ForceDirectories(ExpandConstant('{userappdata}\ProtagonistLabs'));
  if not RenameFile(Old, New) then
    Log('SpaceScan data not moved (the app will try again at its start): ' + Old);
end;

{ Up to 1.3.1 SpaceScan installed per user, in %LOCALAPPDATA%\Programs\SpaceScan, with the same AppId in HKCU. Its data
  is moved first (above); then its program folder, shortcuts and uninstall entry go. Its own uninstaller is not run:
  it would delete HKCU\Software\SpaceScan and the Explorer menu, which [Registry] has just pointed at the new exe. }
procedure RemoveOldInstall();
var
  Dir: String;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, OldKey, 'InstallLocation', Dir) then
  begin
    Dir := RemoveBackslashUnlessRoot(Dir);
    { only a folder that really is the old SpaceScan, never wherever a damaged key might point }
    if (CompareText(Dir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0) and FileExists(Dir + '\{#AppExe}') then
      DelTree(Dir, True, True, True);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, OldKey);
  end;
  DelTree(ExpandConstant('{userprograms}\{#AppName}'), True, True, True);
  DeleteFile(ExpandConstant('{userdesktop}\{#AppName}.lnk'));
end;

{ A running SpaceScan could write its history back into the old folder after the move. }
procedure StopApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  MoveData();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RemoveOldInstall();
end;
