; Hushcue installer (Inno Setup 6). Build it with installer\build.ps1, which passes the defines below:
;   iscc /DAppVersion=1.2.3 /DFileVersion=1.2.3 /DSourceDir=<tools\publish.ps1 output> /DOutputDir=<dir> hushcue.iss
;
; Installs for the current user by default (no admin prompt; %LOCALAPPDATA%\Programs\Hushcue), or for all users when
; the user picks that in the first dialog. Adds a Start menu shortcut, an uninstaller, and optionally
; "Generate subtitles with Hushcue" on the Explorer right-click menu of video and audio files.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Hushcue"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Hushcue"
#define AppExe "Hushcue.exe"
#define MenuVerb "Hushcue.Generate"

; The file types MediaFiles.Extensions lists (src\Hushcue.Core\Media\MediaFiles.cs; a test keeps the two in step)
#dim Ext[18] {".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus"}

[Setup]
AppId={{3E05726D-7DFC-4489-980E-8748E6FBE6DF}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jatinjit Singh
AppPublisherURL=https://github.com/iStorry/hushcue
AppSupportURL=https://github.com/iStorry/hushcue/issues
AppUpdatesURL=https://github.com/iStorry/hushcue/releases
AppComments=Accurate subtitles from any video, generated privately on your PC.
; the app holds this mutex while it runs (Services/SingleInstance.cs): setup asks to close it first
AppMutex=Hushcue
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; per-user by default; the first page lets the user switch to an all-users install (that one asks for admin)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, the oldest the app targets (TargetPlatformMinVersion)
MinVersion=10.0.17763
SetupIconFile=..\src\Hushcue.App\Assets\Hushcue.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
LicenseFile=..\LICENSE
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ContextMenu=Add "Generate subtitles with Hushcue" to the right-click menu of video and audio files, and "Edit subtitles with Hushcue" to .srt files
ContextMenuVerb=Generate subtitles with Hushcue
EditMenuVerb=Edit subtitles with Hushcue
RemoveData=Also remove the speech models and GPU libraries Hushcue downloaded (%1), and its settings?

[Tasks]
Name: "contextmenu"; Description: "{cm:ContextMenu}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; an update replaces the whole runtime folder, so no stale DLL from an older version is left behind
Type: filesandordirs; Name: "{app}\runtime"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Explorer verb under SystemFileAssociations, which adds to whatever app opens the type instead of replacing it.
; HKA is HKCU for a per-user install and HKLM for an all-users one.
#sub ContextMenuEntries
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}\shell"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}\shell\{#MenuVerb}"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenuVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}\shell\{#MenuVerb}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}\shell\{#MenuVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext[I]}\shell\{#MenuVerb}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
#endsub
#define I
#for {I = 0; I < DimOf(Ext); I++} ContextMenuEntries
; "Edit subtitles with Hushcue" on .srt files: opens them in the editor with the video next to them.
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.srt"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.srt\shell"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.srt\shell\Hushcue.Edit"; ValueType: string; ValueName: ""; ValueData: "{cm:EditMenuVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.srt\shell\Hushcue.Edit"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.srt\shell\Hushcue.Edit\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Models (about 1.6 GB each), the GPU libraries (about 0.9 GB) and settings live in %LOCALAPPDATA%\Hushcue, outside
// the install folder. Offer to remove them on an interactive uninstall; a silent uninstall always keeps them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    Data := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(Data) then
      if MsgBox(FmtMessage(CustomMessage('RemoveData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
