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

; Per-user: no UAC prompt. Reading the MFT needs admin, but that is asked for per scan, not at install.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
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
Filename: "{app}\{#AppExe}"; Description: "Start SpaceScan"; Flags: nowait postinstall skipifsilent
