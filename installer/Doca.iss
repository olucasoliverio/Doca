; Doca installer (Inno Setup 6). Built by build-installer.ps1, which publishes a
; self-contained copy (the .NET runtime goes along, nothing to install first) into
; publish\installer and passes the version from Doca.csproj.
; Per-user install, no admin: same folder as install.ps1, so both update the same copy.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\installer"
#endif

[Setup]
; Fixed forever: Windows uses it to recognize updates of the same app.
AppId={{6F3C2A8E-5B1D-4E7A-9C42-D0CA0D0CA001}
AppName=Doca
AppVersion={#MyAppVersion}
AppVerName=Doca {#MyAppVersion}
AppPublisher=Lucas Oliverio
DefaultDirName={userpf}\Doca
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Doca-Setup-{#MyAppVersion}
SetupIconFile=..\assets\doca.ico
UninstallDisplayIcon={app}\Doca.exe
UninstallDisplayName=Doca
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Doca is closed by our own code below (--quit restores the taskbar); the
; Restart Manager would kill it instead and leave the taskbar hidden.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startup"; Description: "Iniciar o Doca com o Windows"; GroupDescription: "Opções:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Doca"; Filename: "{app}\Doca.exe"; WorkingDir: "{app}"; Comment: "Dock e barra de menu estilo macOS"

[Registry]
; Same value Doca's own "Iniciar com o Windows" setting uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Doca"; ValueData: """{app}\Doca.exe"""; Tasks: startup; Flags: uninsdeletevalue
; Unchecked on an update: drop a value left by an earlier install.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Doca"; ValueType: none; Tasks: not startup; Flags: deletevalue uninsdeletevalue

[Run]
Filename: "{app}\Doca.exe"; Description: "Abrir o Doca agora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings in %APPDATA%\Doca are kept on purpose (reinstalling brings them back).
Type: filesandordirs; Name: "{app}"

[Code]
function DocaRunning(): Boolean;
var
  Locator, Service, Procs: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Doca.exe''');
    Result := Procs.Count > 0;
  except
  end;
end;

{ Closes a running Doca the clean way: "--quit" makes it restore the Windows taskbar
  and the reserved screen space. Only if it hangs is it killed. }
procedure CloseDoca(Exe: String);
var
  Code, I: Integer;
begin
  if not DocaRunning() then Exit;
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 40 do
  begin
    if not DocaRunning() then Exit;
    Sleep(250);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Doca.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseDoca(ExpandConstant('{app}\Doca.exe'));
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    CloseDoca(ExpandConstant('{app}\Doca.exe'));
end;
