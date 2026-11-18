; Установщик Atentis Server (Inno Setup 6).
; Сборка:  ISCC.exe installer\AtentisServer.iss /DAppVersion=4.1.0 /DServiceDir=..\publish\service /DPanelDir=..\publish\panel
; Тихая установка (для администраторов):
;   AtentisServer-Setup-4.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /OPERATORS="SERVER\ivanov,SERVER\petrov"
;
; Что делает установщик:
;   * копирует службу (папка service) и «Панель управления» (папка panel) - .NET ставить на сервер не нужно;
;   * создаёт группу Windows AtentisServerOperators и добавляет в неё указанных пользователей:
;     они смогут вводить логины/пароли Atentis и настройки базы без прав администратора;
;   * регистрирует службу AtentisServer (отложенный автозапуск, авто-перезапуск при сбое, виртуальная учётная запись)
;     и запускает её; настройки служба получает позже из «Панели управления»;
;   * закрывает доступ к каталогу данных C:\ProgramData\AtentisServer (там файл настроек с паролем БД и журналы).

#ifndef AppVersion
  #define AppVersion "4.1.0"
#endif
#ifndef ServiceDir
  #define ServiceDir "..\publish\service"
#endif
#ifndef PanelDir
  #define PanelDir "..\publish\panel"
#endif

#define ServiceName "AtentisServer"
#define OperatorsGroup "AtentisServerOperators"

[Setup]
AppId={{B7E2A4C1-6D3F-4E58-9A10-5C2F8E7D1A34}
AppName=Atentis Server
AppVersion={#AppVersion}
AppVerName=Atentis Server {#AppVersion}
AppPublisher=Atentis Server
DefaultDirName={autopf}\AtentisServer
DefaultGroupName=Atentis Server
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\publish\installer
OutputBaseFilename=AtentisServer-Setup-{#AppVersion}
SetupIconFile=..\src\AtentisServer.Panel\Assets\app.ico
UninstallDisplayIcon={app}\panel\AtentisPanel.exe
UninstallDisplayName=Atentis Server
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=6.3
ShowLanguageDialog=no
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Atentis Server

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык «Панель управления» на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "panelautostart"; Description: "Показывать панель управления при входе пользователя в Windows (чтобы после перезагрузки сервера сразу ввести пароли)"; GroupDescription: "Ярлыки:"

[Files]
Source: "{#ServiceDir}\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PanelDir}\*"; DestDir: "{app}\panel"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Панель управления Atentis Server"; Filename: "{app}\panel\AtentisPanel.exe"; WorkingDir: "{app}\panel"
Name: "{group}\Журналы работы"; Filename: "{commonappdata}\AtentisServer\logs"
Name: "{group}\Удалить Atentis Server"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Atentis Server"; Filename: "{app}\panel\AtentisPanel.exe"; WorkingDir: "{app}\panel"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AtentisServerPanel"; ValueData: """{app}\panel\AtentisPanel.exe"""; Flags: uninsdeletevalue; Tasks: panelautostart

[Run]
Filename: "{app}\panel\AtentisPanel.exe"; Description: "Открыть панель управления"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  OperatorsPage: TInputQueryWizardPage;
  OperatorWarnings: String;

procedure InitializeWizard;
var
  DefaultUser: String;
begin
  OperatorsPage := CreateInputQueryPage(wpSelectDir,
    'Кто будет работать с программой',
    'Пользователи Windows, которые смогут вводить логины и пароли',
    'Укажите, кто будет открывать «Панель управления», вводить логины и пароли Atentis и настройки базы данных. ' +
    'Права администратора этим пользователям не нужны.' + #13#10#13#10 +
    'Можно указать нескольких через запятую, например: SERVER\ivanov, SERVER\petrov. ' +
    'Администраторы сервера имеют доступ всегда. Позже добавить пользователя можно командой: ' +
    'net localgroup {#OperatorsGroup} ИМЯ /add (пользователю затем нужно выйти из Windows и войти снова).');
  OperatorsPage.Add('Пользователи (через запятую):', False);

  DefaultUser := ExpandConstant('{param:OPERATORS|}');
  if DefaultUser = '' then
    DefaultUser := GetEnv('USERDOMAIN') + '\' + GetEnv('USERNAME');
  OperatorsPage.Values[0] := DefaultUser;
end;

function RunHidden(const FileName, Params: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('Запуск: %s %s -> %s, код %d', [FileName, Params, IntToStr(Ord(Result)), ResultCode]));
end;

function Sc(const Params: String): Integer;
var
  Code: Integer;
begin
  if not RunHidden(ExpandConstant('{sys}\sc.exe'), Params, Code) then Code := -1;
  Result := Code;
end;

function ServiceInstalled: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

function ServiceIs(const State: String): Boolean;
var
  Code: Integer;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/C sc query {#ServiceName} | find "' + State + '" >nul', Code) and (Code = 0);
end;

procedure StopService;
var
  I: Integer;
begin
  if not ServiceInstalled then Exit;
  Sc('stop {#ServiceName}');
  for I := 1 to 40 do
  begin
    if ServiceIs('STOPPED') then Exit;
    Sleep(1000);
  end;
  Log('Служба не остановилась за 40 секунд');
end;

procedure KillPanel;
var
  Code: Integer;
begin
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AtentisPanel.exe', Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceInstalled and ServiceIs('RUNNING') and (not WizardSilent) then
  begin
    if MsgBox('Служба Atentis Server сейчас работает. Для обновления её нужно остановить.' + #13#10#13#10 +
              'Пароли Atentis хранятся только в памяти службы, поэтому после обновления их нужно будет ввести снова в панели управления. ' +
              'Сбор данных на время обновления прервётся (после запуска будут собраны сделки текущей сессии).' + #13#10#13#10 +
              'Продолжить?', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := 'Обновление отменено.';
      Exit;
    end;
  end;
  StopService;
  KillPanel;
end;

procedure AddOperators;
var
  Rest, Item: String;
  P, Code: Integer;
begin
  { Группа операторов }
  RunHidden(ExpandConstant('{sys}\net.exe'), 'localgroup {#OperatorsGroup} /add /comment:"Пользователи панели управления Atentis Server"', Code);
  { 2 = группа уже существует - это нормально }

  Rest := OperatorsPage.Values[0];
  while Rest <> '' do
  begin
    P := Pos(',', Rest);
    if (Pos(';', Rest) > 0) and ((P = 0) or (Pos(';', Rest) < P)) then P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    Item := Trim(Item);
    if Item <> '' then
    begin
      if not RunHidden(ExpandConstant('{sys}\net.exe'), 'localgroup {#OperatorsGroup} "' + Item + '" /add', Code) then Code := -1;
      { 0 = добавлен, 2 = уже в группе }
      if (Code <> 0) and (Code <> 2) then
        OperatorWarnings := OperatorWarnings + #13#10 + '  - ' + Item;
    end;
  end;
end;

procedure ConfigureService;
var
  AppService, Account, DataDir: String;
  Code: Integer;
begin
  AppService := ExpandConstant('{app}\service\AtentisServer.exe');
  Account := 'NT SERVICE\{#ServiceName}';
  DataDir := ExpandConstant('{commonappdata}\AtentisServer');

  { Источник событий Windows: предупреждения и ошибки службы видны в журнале «Приложение» }
  RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "if (-not [System.Diagnostics.EventLog]::SourceExists(''AtentisServer'')) { New-EventLog -LogName Application -Source AtentisServer }"', Code);

  if not ServiceInstalled then
    Sc('create {#ServiceName} binPath= "\"' + AppService + '\"" start= delayed-auto obj= "' + Account + '" DisplayName= "Atentis Server (сбор данных)"')
  else
    Sc('config {#ServiceName} binPath= "\"' + AppService + '\"" start= delayed-auto obj= "' + Account + '"');

  Sc('description {#ServiceName} "Собирает сделки и позиции из Atentis и сохраняет их в PostgreSQL. Управление: Панель управления Atentis Server."');
  { Авто-перезапуск при сбое: через 5 с, 30 с, 60 с; счётчик сбрасывается через сутки }
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/30000/restart/60000');
  Sc('failureflag {#ServiceName} 1');

  { Каталог данных: настройки (пароль БД) и журналы. Доступ: SYSTEM, администраторы, служба; операторам - только чтение журналов }
  ForceDirectories(DataDir + '\logs');
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F', Code);
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '" /grant "' + Account + ':(OI)(CI)M"', Code);
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '\logs" /grant "{#OperatorsGroup}:(OI)(CI)RX"', Code);
  { Программа: служба может только читать и запускать }
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /grant "' + Account + ':(OI)(CI)RX"', Code);
end;

procedure StartService;
var
  I: Integer;
begin
  Sc('start {#ServiceName}');
  for I := 1 to 30 do
  begin
    if ServiceIs('RUNNING') then Exit;
    Sleep(1000);
  end;
  Log('Служба не перешла в состояние RUNNING за 30 секунд');
  if not WizardSilent then
    MsgBox('Служба установлена, но не запустилась сразу. Откройте журнал: ' + ExpandConstant('{commonappdata}\AtentisServer\logs') +
           ' или журнал Windows (Приложение, источник AtentisServer).', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    AddOperators;        { группа должна существовать ДО запуска службы: из неё строится доступ к каналу }
    ConfigureService;
    StartService;
    if (OperatorWarnings <> '') and (not WizardSilent) then
      MsgBox('Не удалось добавить в группу {#OperatorsGroup} следующих пользователей (проверьте написание имени):' + OperatorWarnings + #13#10#13#10 +
             'Добавить позже: net localgroup {#OperatorsGroup} ИМЯ /add', mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillPanel;
    StopService;
    Sc('delete {#ServiceName}');
    RunHidden(ExpandConstant('{sys}\net.exe'), 'localgroup {#OperatorsGroup} /delete', Code);
  end;
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    MsgBox('Программа удалена. Настройки и журналы сохранены в ' + ExpandConstant('{commonappdata}\AtentisServer') +
           ', собранные данные - в PostgreSQL. Их можно удалить вручную.', mbInformation, MB_OK);
end;
