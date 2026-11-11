#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Интерактивно запрашивает пароли и строку подключения к PostgreSQL и сохраняет их как переменные окружения
  ТОЛЬКО для службы AtentisServer (HKLM\SYSTEM\CurrentControlSet\Services\AtentisServer\Environment).
  Этот раздел реестра доступен только администраторам и SYSTEM. Пароли не попадают ни в файлы, ни в историю команд, ни в лог.

.NOTES
  Повторный запуск заменяет значения. Для смены пароля: запустить скрипт, затем Restart-Service AtentisServer.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'AtentisServer',
    [int]$SlotCount = 2
)
$ErrorActionPreference = 'Stop'

function Read-Plain([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$values = [ordered]@{}

Write-Host "PostgreSQL. Пример: Host=db.example.local;Port=5432;Database=atentis;Username=atentis_app;SSL Mode=VerifyFull;Password=..."
$cs = Read-Plain 'Строка подключения ConnectionStrings:Postgres'
if ([string]::IsNullOrWhiteSpace($cs)) { throw 'Пустая строка подключения.' }
$values['ConnectionStrings__Postgres'] = $cs

for ($i = 0; $i -lt $SlotCount; $i++) {
    $name = if ($i -eq 0) { 'MICEX (фондовый)' } else { 'FORTS (срочный)' }
    $pass = Read-Plain "Пароль Atentis для слота $i - $name (пусто = не менять/слот выключен)"
    if ($pass) { $values["Collector__Slots__${i}__Password"] = $pass }
}

$path = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (-not (Test-Path $path)) { throw "Служба $ServiceName не найдена. Сначала install-service.ps1." }

# Сохраняем уже заданные, не связанные с вводом переменные
$current = @{}
$existing = (Get-ItemProperty -Path $path -Name Environment -ErrorAction SilentlyContinue).Environment
foreach ($line in @($existing)) { if ($line -match '^([^=]+)=(.*)$') { $current[$Matches[1]] = $Matches[2] } }
foreach ($k in $values.Keys) { $current[$k] = $values[$k] }

$lines = $current.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
Set-ItemProperty -Path $path -Name Environment -Type MultiString -Value @($lines)

Write-Host "Сохранено переменных: $($values.Count). Перезапустите службу: Restart-Service $ServiceName"
