; Inno Setup script for Discord Quest Launcher
; Compiled by GitHub Actions (or locally via ISCC.exe)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0.0"
#endif

#ifndef OutputBaseName
  #define OutputBaseName "discord-quest-launcher-" + AppVersion + "-windows-x64"
#endif

#define AppName "Discord Quest Launcher"
#define AppPublisher "projectGloria"
#define AppURL "https://github.com/projectGloria/FakeGameLauncher"
#define AppExeName "GameLauncher.exe"

[Setup]
AppId={{8E5F2A7B-4C91-4E3D-9B1A-6F82C3D4E501}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installers
OutputBaseFilename={#OutputBaseName}
SetupIconFile=..\assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\GameLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
