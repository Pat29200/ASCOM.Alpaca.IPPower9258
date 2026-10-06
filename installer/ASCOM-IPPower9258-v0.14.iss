#define MyAppName "ASCOM Alpaca IP Power 9258/9258xx"
#define MyAppVersion "0.14"
#define MyAppPublisher "ASCOM.IPPower9258 Project"
#define MyAppExeName "ASCOM.Alpaca.IPPower9258.exe"
#define MySourceDir SourcePath + "..\bin\Release\net10.0\publish"

[Setup]
AppId={{A7B52E8D-9258-4A1C-8F31-925800000013}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

DefaultDirName={autopf}\ASCOM-IPPower9258
DisableProgramGroupPage=yes

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts
OutputBaseFilename=ASCOM-IPPower9258-Setup-v0.14

Compression=lzma2
SolidCompression=yes
WizardStyle=modern

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

CloseApplications=yes
RestartApplications=no

SetupLogging=yes

[Files]
; Publication Release v0.14.
; appsettings.Development.json est volontairement exclu.
Source: "{#MySourceDir}\*"; \
    DestDir: "{app}"; \
    Excludes: "appsettings.Development.json"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

Source: "IPPower9258-Arret-PC-600s.bat"; DestDir: "{sd}\ASCOM-IPPower9258-Scripts"; Flags: ignoreversion

[Dirs]
; Le fichier de configuration n'est PAS fourni par l'installateur.
; L'application le crée et le conserve dans ProgramData.
Name: "{commonappdata}\ASCOM-IPPower9258"

[Icons]
; Ouvre l'interface locale de configuration dans le navigateur par défaut.
Name: "{autoprograms}\Configurer IP Power 9258"; \
    Filename: "http://127.0.0.1:5000/setup"

Name: "{autodesktop}\Configurer IP Power 9258"; \
    Filename: "http://127.0.0.1:5000/setup"; \
    Tasks: desktopicon

[Tasks]
Name: "desktopicon"; \
    Description: "Créer un raccourci de configuration sur le Bureau"; \
    GroupDescription: "Raccourcis :"; \
    Flags: unchecked

[UninstallRun]
; Arrête le serveur s'il est encore actif.
Filename: "{sys}\taskkill.exe"; \
    Parameters: "/IM ""{#MyAppExeName}"" /F"; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "StopIPPower9258"

; Supprime la tâche de démarrage automatique.
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Delete /TN ""ASCOM Alpaca IP Power 9258"" /F"; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "DeleteScheduledTask"

; Supprime la règle pare-feu créée par l'installateur.
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""ASCOM Alpaca IP Power 9258"""; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "DeleteFirewallRule"

[UninstallDelete]
; IMPORTANT :
; aucun élément de C:\ProgramData\ASCOM-IPPower9258 n'est supprimé.
; La configuration et le mot de passe protégé DPAPI survivent donc
; à une désinstallation et à une future réinstallation.

[Code]
var
  AccountPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  AccountPage := CreateInputQueryPage(wpSelectDir,
    'Compte Windows de NINA',
    'Autoriser la lecture de la configuration pour la coupure differee',
    'Indiquez le compte qui execute NINA sur ce PC (par exemple PC-TELESCOPE\telescope). ' +
    'Ce compte pourra lire et dechiffrer les identifiants IP9258, mais cet installateur ne lui accorde pas leur modification.');
  AccountPage.Add('Compte Windows :', False);
  AccountPage.Values[0] := GetEnv('COMPUTERNAME') + '\' + GetUserNameString;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Account: String;
begin
  Result := True;
  if CurPageID = AccountPage.ID then begin
    Account := Trim(AccountPage.Values[0]);
    if (Account = '') or (Pos('"', Account) > 0) or (Pos(#13, Account) > 0) or (Pos(#10, Account) > 0) then begin
      MsgBox('Indiquez un compte Windows valide.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RunRequired(const ProgramName, Arguments: String);
var
  ExitCode: Integer;
begin
  Log('Configuration : ' + ProgramName + ' ' + Arguments);
  if not Exec(ExpandConstant('{sys}\') + ProgramName, Arguments, '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('Impossible de lancer ' + ProgramName);
  if ExitCode <> 0 then
    RaiseException(ProgramName + ' a echoue (code ' + IntToStr(ExitCode) +
      '). Consultez le journal installation. Le demarrage du serveur n''est pas confirme.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  { Les codes d'absence de tache/processus sont normaux en premiere installation. }
  Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/End /TN "ASCOM Alpaca IP Power 9258"', '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode);
  if not Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/IM "{#MyAppExeName}" /F', '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) then
    Result := 'Impossible de lancer l''arret du serveur avant la copie.'
  else if (ExitCode <> 0) and (ExitCode <> 128) then
    Result := 'Impossible d''arreter le serveur IPPower9258 (code ' + IntToStr(ExitCode) + ').';
end;

function XmlEscape(Value: String): String;
begin
  StringChangeEx(Value, '&', '&amp;', True);
  StringChangeEx(Value, '<', '&lt;', True);
  StringChangeEx(Value, '>', '&gt;', True);
  StringChangeEx(Value, '"', '&quot;', True);
  Result := Value;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  TaskXml, TaskPath, AppPath, ConfigPath, Account, PowerShellPath: String;
  TaskLines: TArrayOfString;
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  AppPath := ExpandConstant('{app}');
  ConfigPath := ExpandConstant('{commonappdata}\ASCOM-IPPower9258');
  Account := Trim(AccountPage.Values[0]);

  { Conserve les autres autorisations explicites preexistantes. }
  RunRequired('icacls.exe', '"' + ConfigPath +
    '" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"');
  RunRequired('icacls.exe', '"' + ConfigPath +
    '" /grant:r "' + Account + ':(OI)(CI)RX" /T');

  TaskPath := ExpandConstant('{tmp}\IPPower9258-task.xml');
  TaskXml := '<?xml version="1.0" encoding="UTF-16"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' +
    '<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>' +
    '<Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>' +
    '<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>' +
    '<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' +
    '<StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled>' +
    '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>' +
    '<Actions Context="Author"><Exec><Command>' + XmlEscape(AppPath + '\{#MyAppExeName}') +
    '</Command><WorkingDirectory>' + XmlEscape(AppPath) +
    '</WorkingDirectory></Exec></Actions></Task>';
  SetArrayLength(TaskLines, 1);
  TaskLines[0] := TaskXml;
  if not SaveStringsToUTF8File(TaskPath, TaskLines, False) then
    RaiseException('Impossible de creer la definition de la tache.');
  { schtasks accepte sur le PC cible le XML UTF-16LE avec BOM. }
  PowerShellPath := TaskPath;
  StringChangeEx(PowerShellPath, '''', '''''', True);
  RunRequired('WindowsPowerShell\v1.0\powershell.exe',
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference=''Stop''; $p=''' + PowerShellPath + '''; ' +
    '$x=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8); ' +
    '[IO.File]::WriteAllText($p,$x,[Text.Encoding]::Unicode)"');
  RunRequired('schtasks.exe', '/Create /TN "ASCOM Alpaca IP Power 9258" /XML "' + TaskPath + '" /F');

  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="ASCOM Alpaca IP Power 9258"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  RunRequired('netsh.exe', 'advfirewall firewall add rule name="ASCOM Alpaca IP Power 9258" ' +
    'dir=in action=allow program="' + AppPath + '\{#MyAppExeName}" enable=yes profile=private');
  RunRequired('schtasks.exe', '/Run /TN "ASCOM Alpaca IP Power 9258"');
end;
