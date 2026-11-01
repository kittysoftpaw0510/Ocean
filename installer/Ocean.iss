; Ocean setup (Inno Setup 6.3+). Build with scripts\make-setup.ps1, which publishes a self-contained
; build into dist\setup\Ocean and compiles this script into dist\Ocean-Setup-<version>.exe.
;
; What the setup does
;   - installs Ocean.exe (tray app) and Ocean.Service.exe (+ bundled .NET runtime) to Program Files
;   - registers "OceanService" (automatic start, restarts on failure, signed-in users may start it)
;   - adds Windows Firewall rules for the service: private/domain networks, plus public networks (Windows marks
;     many home/office LANs Public). The service keeps the public rule in line with its "Allow connections on
;     Public networks" setting.
;   - Start menu shortcut, optional desktop shortcut, optional "open in tray at sign-in"
;   - upgrades in place over a running Ocean: closes the tray apps and the service by itself (no prompts), keeps
;     settings, and puts the old version back to work if the update is cancelled
;   - uninstall removes the service, firewall rules and shortcuts, and offers to remove settings/caches

#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\setup\Ocean"
#endif
#define ServiceName "OceanService"
#define ServiceDisplayName "Ocean LAN Video Sharing"
#define Assets "..\assets"

[Setup]
AppId={{9C2B6C1E-4C55-4E4B-9F0E-5E0C3F6A9A21}
AppName=Ocean
AppVersion={#AppVersion}
AppVerName=Ocean {#AppVersion}
AppPublisher=Ocean
AppComments=Share and stream videos between PCs on your local network.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Ocean Setup
DefaultDirName={autopf}\Ocean
DefaultGroupName=Ocean
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Ocean-Setup-{#AppVersion}
SetupIconFile={#Assets}\ocean.ico
UninstallDisplayIcon={app}\Ocean.exe
UninstallDisplayName=Ocean
WizardStyle=modern
WizardImageFile={#Assets}\wizard-large-100.bmp,{#Assets}\wizard-large-125.bmp,{#Assets}\wizard-large-150.bmp,{#Assets}\wizard-large-200.bmp
WizardSmallImageFile={#Assets}\wizard-small-100.bmp,{#Assets}\wizard-small-125.bmp,{#Assets}\wizard-small-150.bmp,{#Assets}\wizard-small-200.bmp
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
; compress in a separate (64-bit on 64-bit Windows) process: the bundled ffmpeg pushes the 32-bit compiler out of memory
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabel=Ocean is installed and already sharing in the background.%n%nInstall Ocean on the other PCs too; they find each other automatically. If they don't, check that no other firewall or antivirus program blocks Ocean.Service.exe.

[Tasks]
Name: "service"; Description: "Run Ocean as a Windows service (recommended: shares at startup, even when nobody is signed in)"
Name: "firewall"; Description: "Allow Ocean through Windows Firewall (also on networks Windows marks as Public; you can change this later in Ocean's settings)"
Name: "autostart"; Description: "Open Ocean in the system tray when I sign in"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Shared data folder: written by the service and, in portable mode, by the signed-in user.
Name: "{commonappdata}\Ocean"; Permissions: users-modify

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "install-service.ps1,uninstall-service.ps1"

[Icons]
Name: "{autoprograms}\Ocean"; Filename: "{app}\Ocean.exe"; Comment: "Share and watch videos on your local network"
Name: "{autodesktop}\Ocean"; Filename: "{app}\Ocean.exe"; Tasks: desktopicon; Comment: "Share and watch videos on your local network"

[Registry]
; Same value the app's "Start with Windows" setting toggles.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Ocean"; ValueData: """{app}\Ocean.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Ocean"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Ocean.exe"; Description: "Open Ocean now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\Ocean.Service.exe"""; Flags: runhidden; RunOnceId: "Firewall"

[Code]
const
  // Default service DACL + "Authenticated Users may start the service and query it" (not stop/change).
  ServiceSddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWRPLOCRRC;;;AU)';

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  Log(Format('%s %s -> %d', [FileName, Params, Result]));
end;

function Sc(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

function ServiceStopped: Boolean;
begin
  // find returns 0 when the text is present
  Result := Run(ExpandConstant('{cmd}'), '/C ""' + ExpandConstant('{sys}\sc.exe') + '" query {#ServiceName} | "' + ExpandConstant('{sys}\find.exe') + '" "STOPPED" >nul"') = 0;
end;

var
  RecoveryOff: Boolean;   // the service's restart-on-failure is switched off for the update
  SetupDone: Boolean;     // files are in place (otherwise the old version is put back to work at the end)

function OceanRunning: Boolean;
begin
  // find returns 0 when the text is present
  Result := Run(ExpandConstant('{cmd}'), '/C ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq Ocean.Service.exe" /NH | "' +
                ExpandConstant('{sys}\find.exe') + '" /I "Ocean.Service.exe" >nul"') = 0;
  if not Result then
    Result := Run(ExpandConstant('{cmd}'), '/C ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq Ocean.exe" /NH | "' +
                  ExpandConstant('{sys}\find.exe') + '" /I "Ocean.exe" >nul"') = 0;
end;

procedure KillOceanPrograms;
var
  Dir: String;
begin
  // Every program running from Ocean's folder, in every user's session: tray apps, a portable background service,
  // ffmpeg making a lighter copy (matched by its folder, so other programs' ffmpeg is left alone).
  Dir := ExpandConstant('{app}');
  Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith(''' +
      Dir + '\'', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force"');
  // the same by name, in case PowerShell is unavailable
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Ocean.exe');
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Ocean.Service.exe');
end;

{ Gets every Ocean program out of the way without asking anyone to close anything. The order matters:
  1. Windows' "restart the service when it fails" is switched off first, so stopping it by force can't bring it back
     in the middle of the update (it would lock the new files: "DeleteFile failed; code 5").
  2. The tray apps close next, so none of them starts the service again or keeps a connection open.
  3. The service is asked to stop (versions before 1.2.2 take up to 30 s), and whatever is still running after that
     is ended. }
procedure StopServiceAndApps;
var
  i: Integer;
begin
  if ServiceExists then
  begin
    if Sc('failure {#ServiceName} reset= 0 actions= ""') = 0 then RecoveryOff := True;
  end;
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Ocean.exe');
  if ServiceExists then
  begin
    Sc('stop {#ServiceName}');
    for i := 1 to 70 do
    begin
      if ServiceStopped then Break;
      Sleep(500);
    end;
  end;
  KillOceanPrograms;
  for i := 1 to 20 do
  begin
    if not OceanRunning then Break;
    Sleep(500);
    if i mod 4 = 0 then KillOceanPrograms;
  end;
  Sleep(300);
end;

{ The update was cancelled or failed: the old version goes back to work as it was. }
procedure DeinitializeSetup;
begin
  if SetupDone or not RecoveryOff or not ServiceExists then Exit;
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000');
  Sc('start {#ServiceName}');
end;

procedure InstallOrUpdateService;
var
  Bin: String;
begin
  Bin := 'binPath= "\"' + ExpandConstant('{app}\Ocean.Service.exe') + '\""';
  if ServiceExists then
    Sc('config {#ServiceName} ' + Bin + ' start= auto DisplayName= "{#ServiceDisplayName}"')
  else
    Sc('create {#ServiceName} ' + Bin + ' start= auto DisplayName= "{#ServiceDisplayName}"');
  Sc('description {#ServiceName} "Shares and streams videos with other Ocean PCs on the local network."');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000');
  Sc('failureflag {#ServiceName} 1'); // a deliberate exit with a failure code (restart from Settings) is restarted too
  Sc('sdset {#ServiceName} ' + ServiceSddl);
  if Sc('start {#ServiceName}') <> 0 then
    Log('Service did not start immediately; Ocean will start it on first launch.');
end;

procedure ConfigureFirewall;
var
  Netsh, Exe: String;
begin
  // Drop any older or Windows-created rule for our program (including "block" rules left by a cancelled firewall
  // prompt), then add ours. Runs before the service starts, because the service then keeps the public-network
  // rule in line with its settings (and removes it if the user turned that setting off).
  Netsh := ExpandConstant('{sys}\netsh.exe');
  Exe := ExpandConstant('{app}\Ocean.Service.exe');
  Run(Netsh, 'advfirewall firewall delete rule name=all program="' + Exe + '"');
  if WizardIsTaskSelected('firewall') then
  begin
    Run(Netsh, 'advfirewall firewall add rule name="Ocean" description="Ocean LAN video sharing (discovery + encrypted transfers)" dir=in action=allow program="' + Exe + '" enable=yes profile=private,domain');
    Run(Netsh, 'advfirewall firewall add rule name="Ocean (public networks)" description="Ocean LAN video sharing on networks Windows marks as Public (Ocean settings: Network)" dir=in action=allow program="' + Exe + '" enable=yes profile=public');
  end;
end;

procedure RemoveService;
begin
  if ServiceExists then
  begin
    StopServiceAndApps;
    Sc('delete {#ServiceName}');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WizardForm.PreparingLabel.Caption := 'Closing the running Ocean (it starts again when the update is done)...';
  WizardForm.PreparingLabel.Visible := True;
  WizardForm.Repaint;
  StopServiceAndApps;
  WizardForm.PreparingLabel.Visible := False;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SetupDone := True;
    WizardForm.StatusLabel.Caption := 'Configuring Windows Firewall...';
    ConfigureFirewall;
    WizardForm.StatusLabel.Caption := 'Starting the Ocean service...';
    if WizardIsTaskSelected('service') then
      InstallOrUpdateService
    else
      RemoveService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopServiceAndApps;
    if ServiceExists then Sc('delete {#ServiceName}');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      if MsgBox('Also remove Ocean''s settings, list of shares, node identity and caches from this PC?' + #13#10#13#10 +
                'Your videos and downloaded files are not touched. Choose No to keep them for a later reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{commonappdata}\Ocean'), True, True, True);
  end;
end;
