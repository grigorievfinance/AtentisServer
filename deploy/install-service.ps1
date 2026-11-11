#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Устанавливает AtentisServer как службу Windows (Windows Server 2019+): автозапуск, авто-перезапуск при сбое,
  запуск под виртуальной учётной записью с минимальными правами, секреты - в защищённом реестре службы.

.PARAMETER SourceDir   Папка с результатом publish.ps1.
.PARAMETER InstallDir  Куда копировать программу.
.PARAMETER DependsOn   Необязательно: имя службы PostgreSQL на этом же сервере (например postgresql-x64-16) - AtentisServer будет стартовать после неё.
.PARAMETER SkipSecrets Не запрашивать пароли (если они уже заданы).

.EXAMPLE
  .\install-service.ps1 -SourceDir ..\publish\AtentisServer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDir,
    [string]$InstallDir = 'C:\Program Files\AtentisServer',
    [string]$DependsOn = '',
    [switch]$SkipSecrets
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'AtentisServer'
$DataDir = Join-Path $env:ProgramData 'AtentisServer'
$ServiceAccount = "NT SERVICE\$ServiceName"

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

# 8. Секреты
if (-not $SkipSecrets) {
    & (Join-Path $PSScriptRoot 'Set-AtentisSecrets.ps1')
}

Start-Service $ServiceName
Start-Sleep -Seconds 5
Get-Service $ServiceName | Format-Table Name, Status, StartType -AutoSize
Write-Host "Проверка: Invoke-RestMethod http://127.0.0.1:9464/health/ready ; логи: $DataDir\logs"
