<#
.SYNOPSIS
  Проверка установщика на настоящем Windows (запускается в CI, можно и вручную на тестовой машине от администратора):
  тихая установка -> служба работает -> канал управления -> настройки применяются на лету -> пароли только в памяти ->
  авто-перезапуск после аварии -> удаление.
.PARAMETER InstallerPath  Путь к AtentisServer-Setup-*.exe
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'

$DataDir = Join-Path $env:ProgramData 'AtentisServer'
$InstallDir = Join-Path $env:ProgramFiles 'AtentisServer'
$SettingsFile = Join-Path $DataDir 'appsettings.local.json'
$InstallLog = Join-Path $env:TEMP 'atentis-install.log'
$AtentisSecret = 'ATENTIS-SECRET-77-xyz'
$DbSecret = 'DB-SECRET-123-abc'
$step = 0

function Step($text) { $script:step++; Write-Host "`n=== $($script:step). $text" -ForegroundColor Cyan }
function Check($condition, $message) {
    if (-not $condition) { throw "ПРОВАЛ: $message" }
    Write-Host "  ok: $message"
}

function Invoke-Control($request) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'AtentisServer.Control', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $json = $request | ConvertTo-Json -Depth 10 -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        return ($reader.ReadLine() | ConvertFrom-Json)
    } finally { $pipe.Dispose() }
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $ok = $false
        try { $ok = [bool](& $condition) } catch { }
        if ($ok) { return }
        Start-Sleep -Milliseconds 700
    }
    throw "ПРОВАЛ: не дождались: $what (за $seconds с)"
}

function Dump-Diagnostics {
    Write-Host "`n----- ДИАГНОСТИКА -----" -ForegroundColor Yellow
    if (Test-Path $InstallLog) { Write-Host "--- журнал установщика (хвост):"; Get-Content $InstallLog -Tail 80 }
    $logs = Join-Path $DataDir 'logs'
    if (Test-Path $logs) { Get-ChildItem $logs -Filter *.log | ForEach-Object { Write-Host "--- $($_.Name) (хвост):"; Get-Content $_.FullName -Tail 60 } }
    try { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AtentisServer' } -MaxEvents 10 | Format-List TimeCreated, Message } catch { }
    sc.exe query AtentisServer
}

try {
    Step 'Тихая установка'
    $p = Start-Process -FilePath $InstallerPath -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$InstallLog")
    Check ($p.ExitCode -eq 0) "установщик завершился с кодом 0 (получен $($p.ExitCode))"
    Check (Test-Path (Join-Path $InstallDir 'service\AtentisServer.exe')) 'файлы службы установлены'
    Check (Test-Path (Join-Path $InstallDir 'panel\AtentisPanel.exe')) 'файлы панели установлены'

    Step 'Служба зарегистрирована и работает'
    $svc = Get-CimInstance Win32_Service -Filter "Name='AtentisServer'"
    Check ($null -ne $svc) 'служба AtentisServer существует'
    Check ($svc.StartMode -eq 'Auto') "автозапуск включён (режим: $($svc.StartMode))"
    Check ($svc.StartName -eq 'NT AUTHORITY\NETWORK SERVICE' -or $svc.StartName -like 'NT SERVICE\*') "служба работает не под администратором (учётная запись: $($svc.StartName))"
    Wait-Until { (Get-Service AtentisServer).Status -eq 'Running' } 60 'состояние службы Running'
    $failure = (sc.exe qfailure AtentisServer) -join "`n"
    Check ($failure -match 'RESTART') 'настроен авто-перезапуск при сбое'

    Step 'Права на каталог данных'
    $acl = (icacls $DataDir) -join "`n"
    Check ($acl -notmatch 'BUILTIN\\Users' -and $acl -notmatch 'Everyone' -and $acl -notmatch 'Authenticated Users') 'каталог данных закрыт от обычных пользователей'
    Check ($acl -match 'AtentisServer') 'служба имеет доступ к каталогу данных'

    Step 'Служба отвечает без настроек (первый запуск)'
    Wait-Until { (Invoke-Control @{ cmd = 'status' }).ok } 60 'ответ на команду status'
    $status = (Invoke-Control @{ cmd = 'status' }).status
    Check ($status.slots.Count -eq 2) 'два рынка в списке'
    Check (($status.slots | Where-Object { $_.state -ne 'NotConfigured' }).Count -eq 0) 'оба рынка «не настроены»'
    Check (-not $status.databaseConfigured) 'база данных «не настроена»'
    $health = Invoke-WebRequest -Uri 'http://127.0.0.1:9464/health/live' -UseBasicParsing
    Check ($health.StatusCode -eq 200) 'health-check /health/live отвечает 200'

    Step 'Настройки применяются на лету, без перезапуска службы'
    $pidBefore = (Get-Process AtentisServer).Id
    $apply = Invoke-Control @{
        cmd = 'apply'
        settings = @{
            database = @{ host = '127.0.0.1'; port = 5432; database = 'atentis'; username = 'atentis_app'; password = $DbSecret; sslMode = 'Prefer' }
            slots = @(
                @{ name = 'MICEX'; market = 'SPOT'; enabled = $true; server = '127.0.0.1'; port = 1; login = 'trader1'; publicKeyFile = '' },
                @{ name = 'FORTS'; market = 'FUT'; enabled = $false; server = ''; port = 0; login = ''; publicKeyFile = '' })
        }
        passwords = @{ MICEX = $AtentisSecret }
    }
    Check $apply.ok "apply выполнен: $($apply.error)"
    Wait-Until { (Invoke-Control @{ cmd = 'status' }).status.slots[0].hasPassword } 20 'служба приняла пароль'
    Check ((Get-Process AtentisServer).Id -eq $pidBefore) 'процесс службы НЕ перезапускался'
    $status = (Invoke-Control @{ cmd = 'status' }).status
    Check ($status.slots[0].state -in @('Connecting', 'Reconnecting')) "рынок MICEX пытается подключиться (состояние: $($status.slots[0].state))"
    Check ($status.slots[1].state -eq 'Disabled') 'рынок FORTS выключен'
    Check ($status.databaseConfigured) 'база данных настроена'

    Step 'Пароль Atentis нигде не сохранён на диск'
    Check (Test-Path $SettingsFile) 'файл настроек создан'
    $settingsText = Get-Content $SettingsFile -Raw
    Check ($settingsText -match 'trader1' -and $settingsText -match '127\.0\.0\.1') 'адрес и логин сохранены в файле настроек'
    Check ($settingsText -notmatch [regex]::Escape($AtentisSecret)) 'пароля Atentis в файле настроек нет'
    $leak = Get-ChildItem $DataDir -Recurse -File | Select-String -SimpleMatch -Pattern $AtentisSecret, $DbSecret -List | Where-Object { $_.Path -ne $SettingsFile -and $_.Path -notlike '*.bak' }
    Check ($null -eq $leak) 'паролей нет в журналах и прочих файлах'
    $fileAcl = (icacls $SettingsFile) -join "`n"
    Check ($fileAcl -notmatch 'BUILTIN\\Users' -and $fileAcl -notmatch 'Everyone') 'файл настроек недоступен обычным пользователям'

    Step 'Любой пользователь сервера (не администратор) может управлять службой'
    $testUser = 'atentis_user1'
    # Не длиннее 14 символов: иначе net user задаёт интерактивный вопрос, а в CI отвечать некому.
    $testPass = 'Aa1!' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $UserSecret = 'USER-TYPED-SECRET-55'
    $work = Join-Path $env:SystemDrive 'atentis-ci'
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    icacls $work /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
    net user $testUser $testPass /add | Out-Null
    Check ($LASTEXITCODE -eq 0) 'тестовый пользователь без прав администратора создан'
    try {
        $userScript = @'
$ErrorActionPreference = 'Stop'
$r = @{}
$r.isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
function Ctl($request) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'AtentisServer.Control', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 10 -Compress) + "`n")
        $pipe.Write($bytes, 0, $bytes.Length); $pipe.Flush()
        return ((New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)).ReadLine() | ConvertFrom-Json)
    } finally { $pipe.Dispose() }
}
try {
    $r.statusOk = (Ctl @{ cmd = 'status' }).ok
    $apply = Ctl @{ cmd = 'apply'; passwords = @{ MICEX = '__SECRET__' } }
    $r.applyOk = $apply.ok
    $r.applyError = $apply.error
} catch { $r.pipeError = $_.Exception.Message }
try { Get-Content '__SETTINGS__' -Raw | Out-Null; $r.canReadSettings = $true } catch { $r.canReadSettings = $false }
try { $null = Get-ChildItem '__LOGS__' -ErrorAction Stop; $r.canReadLogs = $true } catch { $r.canReadLogs = $false }
$r | ConvertTo-Json | Set-Content -Path '__OUT__' -Encoding UTF8
'@
        $outFile = Join-Path $work 'result.json'
        $userScript = $userScript.Replace('__SECRET__', $UserSecret).Replace('__SETTINGS__', $SettingsFile).Replace('__LOGS__', (Join-Path $DataDir 'logs')).Replace('__OUT__', $outFile)
        $scriptFile = Join-Path $work 'as-user.ps1'
        Set-Content -Path $scriptFile -Value $userScript -Encoding UTF8
        $cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$testUser", (ConvertTo-SecureString $testPass -AsPlainText -Force))
        $proc = Start-Process -FilePath 'powershell.exe' -Credential $cred -WorkingDirectory $work -LoadUserProfile -Wait -PassThru `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptFile)
        Check (Test-Path $outFile) "сценарий обычного пользователя завершился (код $($proc.ExitCode))"
        $res = Get-Content $outFile -Raw | ConvertFrom-Json
        Check (-not $res.isAdmin) 'проверка выполнялась БЕЗ прав администратора'
        Check ($res.statusOk) "обычный пользователь получил статус службы ($($res.pipeError))"
        Check ($res.applyOk) "обычный пользователь ввёл пароль Atentis ($($res.applyError))"
        Check (-not $res.canReadSettings) 'но файл настроек (пароль БД) ему недоступен'
        Check ($res.canReadLogs) 'журналы работы ему доступны для чтения'
        Wait-Until { (Invoke-Control @{ cmd = 'status' }).status.slots[0].hasPassword } 20 'служба приняла пароль от обычного пользователя'
        $leak2 = Get-ChildItem $DataDir -Recurse -File | Select-String -SimpleMatch -Pattern $UserSecret -List
        Check ($null -eq $leak2) 'пароль, введённый обычным пользователем, нигде не сохранён'
    } finally {
        net user $testUser /delete 2>&1 | Out-Null
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    Step 'Авария: процесс убит - служба поднимается сама, настройки на месте, пароль забыт'
    Stop-Process -Name AtentisServer -Force
    Wait-Until { (Get-Service AtentisServer).Status -eq 'Running' -and (Get-Process AtentisServer -ErrorAction SilentlyContinue).Id -ne $pidBefore } 120 'автоматический перезапуск службы'
    Wait-Until { (Invoke-Control @{ cmd = 'status' }).ok } 60 'канал управления после перезапуска'
    $status = (Invoke-Control @{ cmd = 'status' }).status
    Check (-not $status.slots[0].hasPassword) 'после аварии пароль в памяти не сохранился'
    Check ($status.slots[0].state -eq 'WaitingForPassword') "рынок ждёт ввода пароля (состояние: $($status.slots[0].state))"
    $settings = (Invoke-Control @{ cmd = 'get_settings' }).settings
    Check ($settings.slots[0].server -eq '127.0.0.1' -and $settings.slots[0].login -eq 'trader1') 'адрес и логин восстановлены из файла настроек'
    Check ($settings.database.host -eq '127.0.0.1' -and $settings.database.hasPassword) 'настройки базы данных восстановлены, пароль БД наружу не отдаётся'
    Check ([string]::IsNullOrEmpty($settings.database.password)) 'пароль БД в ответе пустой'

    Step 'Удаление'
    $uninstaller = Join-Path $InstallDir 'unins000.exe'
    Check (Test-Path $uninstaller) 'деинсталлятор на месте'
    $u = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Check ($u.ExitCode -eq 0) "удаление завершилось с кодом 0 (получен $($u.ExitCode))"
    Wait-Until { -not (Get-Service AtentisServer -ErrorAction SilentlyContinue) } 30 'служба удалена'
    Check (-not (Test-Path (Join-Path $InstallDir 'service\AtentisServer.exe'))) 'файлы программы удалены'
    Check (Test-Path $SettingsFile) 'настройки и журналы сохранены (удаляются только вручную)'

    Write-Host "`nВСЕ ПРОВЕРКИ УСТАНОВЩИКА ПРОЙДЕНЫ" -ForegroundColor Green
    # Последняя нативная команда (net localgroup для уже удалённой группы) оставила ненулевой код - он не должен ронять шаг CI.
    $global:LASTEXITCODE = 0
    exit 0
}
catch {
    Write-Host "`n$_" -ForegroundColor Red
    Dump-Diagnostics
    exit 1
}
