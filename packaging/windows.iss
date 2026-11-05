#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{283F8CBC-256B-4CA4-9A32-3A759EED9738}
AppName=FRITZ TV
AppVersion={#AppVersion}
AppPublisher=FRITZ TV contributors
AppPublisherURL=https://github.com/jan-guenter/fritz-tv-app
DefaultDirName={localappdata}\Programs\FRITZ TV
DefaultGroupName=FRITZ TV
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\packages
OutputBaseFilename=fritz-tv-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\FritzTV.Desktop\Assets\fritz-tv.ico
UninstallDisplayIcon={app}\FritzTV.Desktop.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"

[Icons]
Name: "{group}\FRITZ TV"; Filename: "{app}\FritzTV.Desktop.exe"
Name: "{group}\Uninstall FRITZ TV"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\FritzTV.Desktop.exe"; Description: "Launch FRITZ TV"; Flags: nowait postinstall skipifsilent
