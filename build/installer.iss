; Script generated for BlueStar Launcher
; Author: Corøna (BlueStar Developers)
; Repo: https://github.com/Coronitaa/BlueStar

#define MyAppName "BlueStar"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Corona (BlueStar Developers)"
#define MyAppURL "https://github.com/Coronitaa/BlueStar"
#define MyAppExeName "BlueStar.exe"

[Setup]
AppId={{6B5A1D15-18A7-44E2-9F4F-7E812C459D01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=BlueStar-Setup-v1.0.0-win-x64
SetupIconFile=..\src\BlueStar.App\Assets\bluestar.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Launcher Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion=1.0.0.0
VersionInfoCopyright=Copyright (C) 2026 Corøna
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\BlueStar_Certificate.cer"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "certutil.exe"; Parameters: "-addstore -f -user TrustedPublisher ""{tmp}\BlueStar_Certificate.cer"""; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
