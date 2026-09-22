; Crescendo - Inno Setup script
; Output: dist\Crescendo-Setup.exe  (built by build\release.ps1, which passes /DMyAppVersion)
;
; Differences from a plain app installer, all because the engine lives inside
; the Windows audio process rather than in this program:
;   * after install it runs "Crescendo.exe --update-engine", which swaps in a
;     new engine DLL only if one was already installed (a fresh install never
;     touches the audio system until the user presses Install engine);
;   * uninstall runs "Crescendo.exe --uninstall" first, which removes the engine,
;     restores the driver's own effects and the Windows audio policy, and
;     deletes the startup task - so no boost is ever left behind.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "Crescendo"
#define MyAppPublisher "Ferhad"
#define MyAppExeName "Crescendo.exe"
#define Root SourcePath + "\.."

[Setup]
AppId={{E5C2A9D4-6B1F-4F7A-9C3E-2D8B7A41F6C0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/ferhad24/Crescendo
AppUpdatesURL=https://github.com/ferhad24/Crescendo/releases
VersionInfoVersion={#MyAppVersion}
; The engine path (Program Files\Crescendo\Engine) is fixed, so is the app path.
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#Root}\dist
OutputBaseFilename=Crescendo-Setup
SetupIconFile={#Root}\assets\crescendo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Crescendo itself requires administrator rights, and so does its engine.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Close a running copy so its files can be replaced during an update.
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#Root}\artifacts\release\Crescendo.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\artifacts\release\CrescendoApo.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: optional launch at the end.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--update-engine"; Description: "Start Crescendo"; Flags: nowait postinstall skipifsilent
; Silent update (from the in-app updater): refresh the engine and reopen.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--update-engine"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Must finish before files are removed: it still needs the exe to run.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveEngine"

[UninstallDelete]
; The engine copy the app placed next to itself; unregistered by --uninstall above.
Type: filesandordirs; Name: "{app}\Engine"
