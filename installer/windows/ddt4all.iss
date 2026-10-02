; Inno Setup 6 script for DDT4All.NET.
; Compile (Windows):  iscc /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=..\..\artifacts\publish\win-x64 ddt4all.iss
; Wrapped by installer\build.ps1 / installer/windows/build-setup.sh.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\win-" + Arch
#endif
#ifndef OutDir
  #define OutDir "..\..\artifacts"
#endif
#define AppName "DDT4All"
#define AppExe  "Ddt4All.App.exe"

[Setup]
AppId={{6F1C2B7E-3D54-4B8A-9E5A-0D7D4A11C4E2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DDT4All.NET contributors
AppPublisherURL=https://github.com/cedricp/ddt4all
AppSupportURL=https://github.com/cedricp/ddt4all
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\assets\ddt4all.ico
#if FileExists("..\..\LICENSE")
LicenseFile=..\..\LICENSE
#endif
OutputDir={#OutDir}
OutputBaseFilename=DDT4All-{#AppVersion}-win-{#Arch}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; No file associations are registered.
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\ddt4all.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\ddt4all.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\ddt4all.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
