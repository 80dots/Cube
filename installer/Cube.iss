; Inno Setup 스크립트. tools/build-release.ps1 이 /DAppVersion=... /DSourceDir=... /DOutputDir=... 로 호출한다.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\windows"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{7D2C1C8E-9C7A-4E26-9B59-6A0B4F7C1D11}
AppName=Cube
AppVersion={#AppVersion}
AppVerName=Cube {#AppVersion}
AppPublisher=80dots
AppPublisherURL=https://github.com/80dots/Cube
DefaultDirName={autopf}\Cube
DefaultGroupName=Cube
UninstallDisplayIcon={app}\Cube.exe
SetupIconFile=..\icon.ico
OutputDir={#OutputDir}
OutputBaseFilename=Cube-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Cube"; Filename: "{app}\Cube.exe"
Name: "{group}\Uninstall Cube"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Cube"; Filename: "{app}\Cube.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Cube.exe"; Description: "{cm:LaunchProgram,Cube}"; Flags: nowait postinstall skipifsilent
