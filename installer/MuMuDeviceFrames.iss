; MuMu Device Frames installer (Inno Setup 6). Build from the repo root after `dotnet publish ... -o publish`:
;
;     ISCC.exe installer\MuMuDeviceFrames.iss
;
; Per-user, no admin rights, into %LOCALAPPDATA%\Programs\MuMuDeviceFrames (the folder install.ps1 uses, so the two
; never fight). Settings, frames and per-emulator choices live in %APPDATA%\MuMuDeviceFrames and survive upgrades.
;
; Silent: MuMuDeviceFrames-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES [/TASKS="startup,frames,androidtools,duo"]
;         [/DUO=1] [/ANDROIDTOOLS=1] [/NOLAUNCH=1]
; Uninstall silently: unins000.exe /VERYSILENT [/REMOVESETTINGS=1] [/REMOVEOVERLAYS=1]

#define AppName "MuMu Device Frames"
#define AppExe "MuMuDeviceFrames.exe"
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\publish\" + AppExe)
#endif

[Setup]
AppId={{CFA81413-3A35-4172-BCCE-DC374580CE69}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=JustSuperHuman
AppPublisherURL=https://github.com/JustSuperHuman/MuMuDeviceFrames
AppSupportURL=https://github.com/JustSuperHuman/MuMuDeviceFrames/issues
AppCopyright=Copyright (C) 2026 James P. Verde
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\MuMuDeviceFrames
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=MuMuDeviceFrames-Setup
SetupIconFile=..\assets\MuMuDeviceFrames.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no

[Messages]
WelcomeLabel2=This installs [name/ver]: real iPhone, Pixel and Galaxy frames around your MuMu Player emulators, the iPhone Duo with its five poses, and the Android side of the fit (resolution, density, safe areas).%n%nNo admin rights are needed.

[Tasks]
Name: "startup"; Description: "Start with Windows"
Name: "frames"; Description: "Download the device frame set now (about 35 MB, from device-frames-media)"
Name: "androidtools"; Description: "Install the Android display tools (safe areas, iOS-style status bar, iPhone Duo folding)"; Check: AndroidToolsMissing
Name: "duo"; Description: "Set up an iPhone Duo emulator (a new MuMu instance, a minute or two)"; Flags: unchecked; Check: MuMuInstalled

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Real device frames around MuMu Player windows"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MuMuDeviceFrames"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--prepare"; StatusMsg: "Drawing the iPhone Duo frames..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--download"; StatusMsg: "Downloading the device frame set (about 35 MB)..."; Tasks: frames; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\install-android-tools.ps1"""; StatusMsg: "Setting up the Android display tools (a console shows the progress)..."; Check: WantAndroidTools; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--duo-create"; StatusMsg: "Creating the iPhone Duo emulator (a minute or two)..."; Check: WantDuo; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: postinstall nowait; Check: ShouldLaunch

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  HostWindow = 'MuMuDeviceFramesHost'; { the tray app's hidden window }
  ExitRequest = $8001;                 { WM_APP + 1: quit cleanly, giving the emulator windows back }
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function MuMuInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayerGlobal')
    or RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayerGlobal')
    or FileExists(ExpandConstant('{commonpf64}\Netease\MuMuPlayer\nx_main\MuMuManager.exe'));
end;

{ Android SDK build-tools with aapt2 and apksigner under an SDK root. }
function HasBuildTools(Root: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if Root = '' then exit;
  if FindFirst(AddBackslash(Root) + 'build-tools\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..')
        and FileExists(AddBackslash(Root) + 'build-tools\' + Found.Name + '\aapt2.exe')
        and FileExists(AddBackslash(Root) + 'build-tools\' + Found.Name + '\lib\apksigner.jar') then
      begin
        Result := True;
        exit;
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

function JavaFound: Boolean;
begin
  Result := ((GetEnv('JAVA_HOME') <> '') and FileExists(AddBackslash(GetEnv('JAVA_HOME')) + 'bin\java.exe'))
    or DirExists(ExpandConstant('{localappdata}\Programs\MuMuDeviceFramesJava'))
    or (FileSearch('java.exe', GetEnv('PATH')) <> '');
end;

{ Same idea as the app's own check: the setup is offered only when something is missing (it reuses what is there). }
function AndroidToolsMissing: Boolean;
begin
  Result := not ((HasBuildTools(GetEnv('ANDROID_HOME')) or HasBuildTools(GetEnv('ANDROID_SDK_ROOT'))
    or HasBuildTools(ExpandConstant('{localappdata}\Android\Sdk'))) and JavaFound);
end;

function ShouldLaunch: Boolean;
begin
  Result := ExpandConstant('{param:NOLAUNCH|0}') <> '1';
end;

{ Asks the running tray app to quit (it restores the emulator windows on the way out) and waits for it. }
procedure CloseRunningApp;
var
  Wnd: HWND;
  I: Integer;
begin
  Wnd := FindWindowByWindowName(HostWindow);
  if Wnd = 0 then exit;
  PostMessage(Wnd, ExitRequest, 0, 0);
  for I := 1 to 60 do
  begin
    Sleep(200);
    if FindWindowByWindowName(HostWindow) = 0 then exit;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not MuMuInstalled then
    MsgBox('MuMu Player 12 was not found on this PC.' + #13#10#13#10 +
      'MuMu Device Frames frames MuMu Player emulators, so you will also want MuMu Player from https://www.mumuplayer.com/ . ' +
      'You can install MuMu Device Frames now and MuMu Player later.', mbInformation, MB_OK);
end;

{ The task, or /DUO=1 and /ANDROIDTOOLS=1 on the command line (also with /VERYSILENT and any /TASKS list). }
function WantDuo: Boolean;
begin
  Result := MuMuInstalled and (WizardIsTaskSelected('duo') or (ExpandConstant('{param:DUO|0}') = '1'));
end;

function WantAndroidTools: Boolean;
begin
  Result := AndroidToolsMissing and (WizardIsTaskSelected('androidtools') or (ExpandConstant('{param:ANDROIDTOOLS|0}') = '1'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp; { an upgrade replaces the exe; settings in %APPDATA% stay as they are }
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'MuMuDeviceFrames');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  RemoveOverlays, RemoveSettings: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseRunningApp;
    Exec(ExpandConstant('{app}\{#AppExe}'), '--restore', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RemoveOverlays := ExpandConstant('{param:REMOVEOVERLAYS|0}') = '1';
    if not UninstallSilent then
      RemoveOverlays := MsgBox('Also take the Android overlays this app installed (safe-area cutout, status bar, iPhone Duo postures) off the emulators that are running now?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if RemoveOverlays then
      Exec(ExpandConstant('{app}\{#AppExe}'), '--remove-overlays', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'MuMuDeviceFrames');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveSettings := ExpandConstant('{param:REMOVESETTINGS|0}') = '1';
    if not UninstallSilent then
      RemoveSettings := MsgBox('Also delete your settings and the downloaded frames (' + ExpandConstant('{userappdata}') + '\MuMuDeviceFrames)?' + #13#10#13#10 +
        'Choose No to keep them for a later install.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if RemoveSettings then
      DelTree(ExpandConstant('{userappdata}\MuMuDeviceFrames'), True, True, True);
  end;
end;
