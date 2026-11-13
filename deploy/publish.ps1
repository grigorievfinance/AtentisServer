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

dotnet publish $project -c Release -r win-x64 --self-contained true -o $Output `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
Write-Host "Готово: $Output"
