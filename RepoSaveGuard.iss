#ifndef AppVersion
  #error Pass the application version with Build-Installer.ps1.
#endif

[Setup]
AppId={{6E02B886-E8E8-4D06-9CAC-C7973BC00CAF}
AppName=REPO Save Guard
AppVersion={#AppVersion}
AppPublisher=alzen326
AppPublisherURL=https://github.com/alzen326/repo-save-guard
AppUpdatesURL=https://github.com/alzen326/repo-save-guard/releases
DefaultDirName={localappdata}\Programs\REPO Save Guard
DefaultGroupName=REPO Save Guard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
SetupIconFile=RepoSaveGuard.ico
UninstallDisplayIcon={app}\RepoSaveGuard.exe
VersionInfoVersion={#AppVersion}
CloseApplications=yes
CloseApplicationsFilter=RepoSaveGuard.exe
RestartApplications=no
WizardStyle=modern
OutputDir=dist
OutputBaseFilename=REPO-Save-Guard-Setup-{#AppVersion}
Compression=lzma2

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "RepoSaveGuard.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\REPO Save Guard"; Filename: "{app}\RepoSaveGuard.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\REPO Save Guard"; Filename: "{app}\RepoSaveGuard.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RepoSaveGuard.exe"; Description: "{cm:LaunchProgram,REPO Save Guard}"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser
