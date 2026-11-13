<#
.SYNOPSIS
  Собирает самодостаточный релиз для Windows Server 2019 (.NET Runtime на сервере устанавливать не нужно).
.EXAMPLE
  .\deploy\publish.ps1            # результат: .\publish\AtentisServer
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish\AtentisServer')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\AtentisServer\AtentisServer.csproj'

# Нужен .NET SDK 8.0 или новее (на сервере SDK не нужен - релиз самодостаточный).
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден 'dotnet'. Установите .NET SDK 8.0: https://dotnet.microsoft.com/download/dotnet/8.0 (SDK, не Runtime), затем откройте НОВОЕ окно PowerShell."
}
$sdks = @(dotnet --list-sdks | ForEach-Object { [int]($_ -split '\.')[0] })
if (-not ($sdks | Where-Object { $_ -ge 8 })) {
    throw "Найден .NET SDK, но версии ниже 8.0 (установлены: $($sdks -join ', ')). Установите .NET SDK 8.0."
}

dotnet publish $project -c Release -r win-x64 --self-contained true -o $Output `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
Write-Host "Готово: $Output"
