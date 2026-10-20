; Inno Setup Script for NPS Kiosk (KioskMax)
; Requires Inno Setup 6

#ifndef MyAppName
  #define MyAppName "KioskMax"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "Max Yuri"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#ifndef MyAppURL
  #define MyAppURL "https://github.com/maxyuri99/"
#endif

#define InstallDirName "{pf64}\\UpMais\\NpsKiosk"
#define SourceBundle "..\\publish\\NpsKiosk-Portable"

[Setup]
AppId={{7ED6C8D9-36F8-4B0B-9A62-1D30E0FE1A7B}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={#InstallDirName}
DefaultGroupName=KioskMax
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\\publish\\installer
OutputBaseFilename=KioskMaxSetup_{#MyAppVersion}
WizardStyle=modern
#ifexist "{#SourceBundle}\\icones\\app.ico"
SetupIconFile={#SourceBundle}\\icones\\app.ico
#endif

; Ensure icon appears in Programs and Features
UninstallDisplayIcon={app}\\icones\\app.ico

; Improve setup executable metadata
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription=KioskMax installer

[Languages]
Name: "br"; MessagesFile: "compiler:Languages\\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "Criar atalho na Área de Trabalho"; Flags: unchecked

[Files]
Source: "{#SourceBundle}\\Display\\*"; DestDir: "{app}\\Display"; Flags: recursesubdirs ignoreversion
Source: "{#SourceBundle}\\Tray\\*"; DestDir: "{app}\\Controller"; Flags: recursesubdirs ignoreversion
Source: "{#SourceBundle}\\Router\\*"; DestDir: "{app}\\Router"; Flags: recursesubdirs ignoreversion
Source: "{#SourceBundle}\\Watchdog\\*"; DestDir: "{app}\\Watchdog"; Flags: recursesubdirs ignoreversion
Source: "{#SourceBundle}\\icones\\*"; DestDir: "{app}\\icones"; Flags: recursesubdirs onlyifdoesntexist ignoreversion
Source: "{#SourceBundle}\\run-both*"; DestDir: "{app}"; Flags: ignoreversion

; Default config on first install
Source: "{#SourceBundle}\\config\\config.sample.json"; DestDir: "{commonappdata}\\UpMais\\NpsKiosk"; DestName: "config.json"; Flags: onlyifdoesntexist

[Dirs]
Name: "{commonappdata}\\UpMais"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\\UpMais\\NpsKiosk"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\\KioskMax Controller"; Filename: "{app}\\Controller\\KioskController.exe"; WorkingDir: "{app}\\Controller"; IconFilename: "{app}\\icones\\app.ico"
Name: "{commondesktop}\\KioskMax Controller"; Filename: "{app}\\Controller\\KioskController.exe"; WorkingDir: "{app}\\Controller"; Tasks: desktopicon; IconFilename: "{app}\\icones\\app.ico"

[Run]
Filename: "{app}\\Controller\\KioskController.exe"; Description: "Iniciar KioskMax Controller agora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function WebView2Installed(): Boolean;
var
  Value: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) then
  begin
    Result := Value <> '';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrCode: Integer;
begin
  if not WebView2Installed() then
  begin
    if MsgBox('O Microsoft WebView2 Runtime não foi detectado. Ele é necessário para o Display funcionar. Deseja abrir a página de instalação agora?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Url, ConfigPath, Conteudo: String;
  Ansi: AnsiString;
begin
  if CurStep <> ssPostInstall then Exit;
  Url := Trim(ExpandConstant('{param:GESTAOURL|}'));
  if Url = '' then Exit;
  ConfigPath := ExpandConstant('{commonappdata}\UpMais\NpsKiosk\config.json');
  if not LoadStringFromFile(ConfigPath, Ansi) then Exit;
  Conteudo := String(Ansi);
  if Pos('"gestaoApiUrl": ""', Conteudo) > 0 then
    StringChangeEx(Conteudo, '"gestaoApiUrl": ""', '"gestaoApiUrl": "' + Url + '"', True)
  else if Pos('"gestaoApiUrl"', Conteudo) = 0 then
    StringChangeEx(Conteudo, '{', '{' + #13#10 + '  "gestaoApiUrl": "' + Url + '",', False);
  SaveStringToFile(ConfigPath, AnsiString(Conteudo), False);
end;
