; RadialShortcut setup: per-user install, no admin rights.
; Build: ISCC.exe /DAppVersion=0.8.0 /DSourceDir=<publish folder> installer\RadialShortcut.iss
; The in-app updater swaps the whole install folder, so the uninstaller lives beside it and
; uninstalling removes the folder as a whole rather than a list of files.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\rel\app"
#endif

[Setup]
AppId={{3FA68CEA-C5AD-4565-AF3D-60EC8FFB87C9}
AppName=RadialShortcut
AppVersion={#AppVersion}
AppVerName=RadialShortcut {#AppVersion}
AppPublisher=Arda Kaya
AppPublisherURL=https://github.com/arrdakaya/RadialShortcut
AppSupportURL=https://github.com/arrdakaya/RadialShortcut/issues
DefaultDirName={localappdata}\Programs\RadialShortcut
UninstallFilesDir={localappdata}\Programs\RadialShortcut Uninstall
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\rel
OutputBaseFilename=RadialShortcut-Setup
SetupIconFile=..\src\RadialShortcut.App\Assets\app.ico
UninstallDisplayIcon={app}\RadialShortcut.exe
UninstallDisplayName=RadialShortcut
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
AppMutex=Local\RadialShortcut.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
tr.StartWithWindows=Windows açılınca başlat
en.StartWithWindows=Start with Windows
tr.Launch=RadialShortcut'ı şimdi başlat
en.Launch=Start RadialShortcut now

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\RadialShortcut"; Filename: "{app}\RadialShortcut.exe"
Name: "{userdesktop}\RadialShortcut"; Filename: "{app}\RadialShortcut.exe"; Tasks: desktopicon

[Registry]
; The same value the app's own "Windows ile başlat" menu item writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RadialShortcut"; ValueData: """{app}\RadialShortcut.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RadialShortcut"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\RadialShortcut.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im RadialShortcut.exe /f"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.backup"
