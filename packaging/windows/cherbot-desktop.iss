; Optional Inno Setup installer for the Windows tray app.
; Build after publishing: iscc /DSourceDir=..\..\artifacts\cherbot-desktop-win-x64 /DAppVersion=0.3.0 cherbot-desktop.iss
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\cherbot-desktop-win-x64"
#endif
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif

[Setup]
AppId={{3B6F2C1E-7A1D-4E39-9C8A-5D2E1F0A7B44}
AppName=CherBot
AppVersion={#AppVersion}
AppPublisher=CherBot contributors
DefaultDirName={localappdata}\Programs\CherBot
DefaultGroupName=CherBot
PrivilegesRequired=lowest
OutputBaseFilename=CherBot-Desktop-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\CherBot"; Filename: "{app}\CherBot.Desktop.exe"
Name: "{userdesktop}\CherBot"; Filename: "{app}\CherBot.Desktop.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\CherBot.Desktop.exe"; Description: "Start CherBot"; Flags: nowait postinstall skipifsilent
