; VolumeX - Inno Setup script
; Output: dist\VolumeX-Setup.exe  (built by build\release.ps1, which passes /DMyAppVersion)
;
; Differences from a plain app installer, all because the engine lives inside
; the Windows audio process rather than in this program:
;   * after install it runs "VolumeX.exe --update-engine", which swaps in a new
;     engine DLL only if one was already installed (a fresh install never
;     touches the audio system until the user presses Install engine);
;   * uninstall runs "VolumeX.exe --uninstall" first, which removes the engine,
;     restores the driver's own effects and the Windows audio policy, and
;     deletes the startup task - so no boost is ever left behind.
;
; Upgrading from Crescendo (the name up to 1.1.2): the AppId is unchanged, so
; this replaces that installation and its uninstall entry rather than sitting
; beside it. The old program files and shortcuts are removed below; the old
; engine folder is removed by the app once Windows audio has moved off it.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "VolumeX"
#define MyAppPublisher "Ferhad"
#define MyAppExeName "VolumeX.exe"
#define Root SourcePath + "\.."

[Setup]
; Same AppId as Crescendo: an upgrade, not a second program.
AppId={{E5C2A9D4-6B1F-4F7A-9C3E-2D8B7A41F6C0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/ferhad24/VolumeX
AppUpdatesURL=https://github.com/ferhad24/VolumeX/releases
VersionInfoVersion={#MyAppVersion}
; The engine path (Program Files\VolumeX\Engine) is fixed, so is the app path.
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
; Otherwise an upgrade would reuse Program Files\Crescendo from the old install.
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#Root}\dist
OutputBaseFilename=VolumeX-Setup
SetupIconFile={#Root}\assets\crescendo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; VolumeX itself requires administrator rights, and so does its engine.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Close a running copy - under either name - so its files can be replaced.
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName},Crescendo.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[InstallDelete]
; Crescendo 1.1.x leftovers. Its Engine folder is deliberately not listed:
; audiodg may still have that DLL loaded; the app removes it after the restart.
Type: files; Name: "{autopf}\Crescendo\Crescendo.exe"
Type: files; Name: "{autopf}\Crescendo\CrescendoApo.dll"
Type: files; Name: "{autopf}\Crescendo\unins000.exe"
Type: files; Name: "{autopf}\Crescendo\unins000.dat"
Type: files; Name: "{autoprograms}\Crescendo.lnk"
Type: files; Name: "{autodesktop}\Crescendo.lnk"

[Files]
Source: "{#Root}\artifacts\release\VolumeX.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\artifacts\release\CrescendoApo.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: optional launch at the end.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--update-engine"; Description: "Start VolumeX"; Flags: nowait postinstall skipifsilent
; Silent update (from the in-app updater): refresh the engine and reopen.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--update-engine"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Must finish before files are removed: it still needs the exe to run.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveEngine"

[UninstallDelete]
; The engine copy the app placed next to itself; unregistered by --uninstall above.
Type: filesandordirs; Name: "{app}\Engine"
