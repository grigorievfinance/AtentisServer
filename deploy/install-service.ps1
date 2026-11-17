#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Устанавливает AtentisServer как службу Windows (Windows Server 2019+): автозапуск, авто-перезапуск при сбое,
  запуск под виртуальной учётной записью с минимальными правами.
  ВСЕ настройки (строка подключения к PostgreSQL, адреса/логины/пароли Atentis) хранятся в одном файле
  %ProgramData%\AtentisServer\appsettings.local.json, который служба читает при каждом запуске.
  Файл создаётся из шаблона один раз и дальше никогда не перезаписывается; права на него - только SYSTEM, Administrators и служба.

.PARAMETER SourceDir   Папка с результатом publish.ps1.
.PARAMETER InstallDir  Куда копировать программу.
.PARAMETER DependsOn   Необязательно: имя службы PostgreSQL на этом же сервере (например postgresql-x64-16) - AtentisServer будет стартовать после неё.
.PARAMETER PromptSecrets Устаревший способ: дополнительно запросить пароли и сохранить их в переменных окружения службы (файл настроек всё равно имеет приоритет).

.EXAMPLE
  .\install-service.ps1 -SourceDir ..\publish\AtentisServer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDir,
    [string]$InstallDir = 'C:\Program Files\AtentisServer',
    [string]$DependsOn = '',
    [switch]$PromptSecrets
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'AtentisServer'
$DataDir = Join-Path $env:ProgramData 'AtentisServer'
$ServiceAccount = "NT SERVICE\$ServiceName"
$SettingsFile = Join-Path $DataDir 'appsettings.local.json'
$SettingsTemplate = Join-Path $PSScriptRoot 'appsettings.local.template.json'

if (-not (Test-Path (Join-Path $SourceDir 'AtentisServer.exe'))) {
    throw "В '$SourceDir' нет AtentisServer.exe. Сначала выполните deploy\publish.ps1."
}

# 1. Остановить и обновить существующую установку
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Служба уже установлена - обновляю файлы."
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $existing.WaitForStatus('Stopped', '00:00:45') }
}

# 2. Файлы программы (только чтение для службы)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

# 3. Каталог данных: логи и локальная конфигурация. Доступ только у SYSTEM, администраторов и самой службы.
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null
icacls $DataDir /inheritance:r | Out-Null
icacls $DataDir /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null   # SYSTEM и Administrators; права службе - ниже, после создания службы

# 4. Источник событий Windows (предупреждения и ошибки сервиса попадают в журнал Application)
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    New-EventLog -LogName Application -Source $ServiceName
}

# 5. Создать / обновить службу
$exe = Join-Path $InstallDir 'AtentisServer.exe'
if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Atentis Server (сбор данных)' `
        -Description 'Собирает сделки и позиции из Atentis и сохраняет их в PostgreSQL.' `
        -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName start= delayed-auto obj= $ServiceAccount | Out-Null          # отложенный автозапуск: сеть и БД успевают подняться
if ($DependsOn) { sc.exe config $ServiceName depend= $DependsOn | Out-Null }

# 6. Автоматический перезапуск при падении: через 5 с, 30 с, 60 с; счётчик сбрасывается через сутки.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null     # считать сбоем и выход с ненулевым кодом, а не только падение процесса

# 7. Права виртуальной учётной записи службы (она существует только после создания службы):
#    программа - только чтение и запуск, данные (логи) - изменение.
icacls $InstallDir /grant:r "${ServiceAccount}:(OI)(CI)RX" | Out-Null
icacls $DataDir /grant:r "${ServiceAccount}:(OI)(CI)M" | Out-Null

# 8. Файл настроек (создаётся один раз из шаблона, существующий НЕ перезаписывается)
$settingsCreated = $false
if (-not (Test-Path $SettingsFile)) {
    if (-not (Test-Path $SettingsTemplate)) { throw "Не найден шаблон настроек: $SettingsTemplate" }
    Copy-Item $SettingsTemplate $SettingsFile
    $settingsCreated = $true
}
# Права на файл: он может содержать пароли. SYSTEM и Administrators - полный доступ, служба - только чтение, остальным нет доступа.
icacls $SettingsFile /inheritance:r | Out-Null
icacls $SettingsFile /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${ServiceAccount}:R" | Out-Null

if ($PromptSecrets) {
    & (Join-Path $PSScriptRoot 'Set-AtentisSecrets.ps1')
}

$envValue = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -ErrorAction SilentlyContinue).Environment
if ($envValue) {
    Write-Warning "У службы заданы переменные окружения (от прежней установки). Значения из файла настроек имеют приоритет над ними."
}

if ($settingsCreated) {
    Write-Host ""
    Write-Host "Создан файл настроек: $SettingsFile" -ForegroundColor Yellow
    Write-Host "1) Откройте его (notepad `"$SettingsFile`" от имени администратора) и заполните:" -ForegroundColor Yellow
    Write-Host "   строку подключения PostgreSQL, Server/Port/Login/Password для слотов MICEX и FORTS." -ForegroundColor Yellow
    Write-Host "2) Запустите службу:  Start-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "Служба НЕ запущена, пока файл не заполнен. Изменения в файле применяются при перезапуске: Restart-Service $ServiceName" -ForegroundColor Yellow
    Get-Service $ServiceName | Format-Table Name, Status, StartType -AutoSize
    return
}

Start-Service $ServiceName
Start-Sleep -Seconds 5
Get-Service $ServiceName | Format-Table Name, Status, StartType -AutoSize
Write-Host "Файл настроек: $SettingsFile (применяется при перезапуске службы)"
Write-Host "Проверка: Invoke-RestMethod http://127.0.0.1:9464/health/ready ; логи: $DataDir\logs"
