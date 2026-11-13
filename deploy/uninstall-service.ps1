#Requires -RunAsAdministrator
<#
.SYNOPSIS  Удаляет службу AtentisServer. Данные в PostgreSQL и логи не трогает.
#>
[CmdletBinding()]
param([string]$InstallDir = 'C:\Program Files\AtentisServer', [switch]$RemoveFiles)
$ErrorActionPreference = 'Stop'
$ServiceName = 'AtentisServer'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:45') }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Служба удалена (вместе с её секретами в реестре)."
}
if ($RemoveFiles -and (Test-Path $InstallDir)) { Remove-Item $InstallDir -Recurse -Force }
Write-Host "Логи и локальная конфигурация сохранены в $env:ProgramData\AtentisServer"
