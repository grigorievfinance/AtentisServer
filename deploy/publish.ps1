<#
.SYNOPSIS
  Собирает всё для установки на Windows Server 2019: службу, панель управления и (если найден Inno Setup) установщик.
  Результат: publish\service, publish\panel, publish\installer\AtentisServer-Setup-<версия>.exe
  На сервере .NET ставить не нужно: компоненты самодостаточные.
.EXAMPLE
  .\deploy\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Нужен .NET SDK 8.0 или новее (только на машине, где собирается установщик).
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден 'dotnet'. Установите .NET SDK 8.0: https://dotnet.microsoft.com/download/dotnet/8.0 (SDK, не Runtime), затем откройте НОВОЕ окно PowerShell."
}
$sdks = @(dotnet --list-sdks | ForEach-Object { [int]($_ -split '\.')[0] })
if (-not ($sdks | Where-Object { $_ -ge 8 })) {
    throw "Найден .NET SDK, но версии ниже 8.0 (установлены: $($sdks -join ', ')). Установите .NET SDK 8.0."
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "Версия: $version"

foreach ($item in @(@{ Name = 'service'; Project = 'src\AtentisServer\AtentisServer.csproj' },
                    @{ Name = 'panel';   Project = 'src\AtentisServer.Panel\AtentisServer.Panel.csproj' })) {
    $out = Join-Path $Output $item.Name
    Write-Host "Публикация $($item.Name) -> $out"
    dotnet publish (Join-Path $root $item.Project) -c Release -r win-x64 --self-contained true -o $out `
        -p:DebugType=none -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($($item.Name)) завершился с кодом $LASTEXITCODE" }
}

# Установщик (нужен Inno Setup 6: https://jrsoftware.org/isdl.php)
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
    if (-not $iscc -and (Test-Path $candidate)) { $iscc = $candidate }
}
if ($iscc) {
    & $iscc (Join-Path $root 'installer\AtentisServer.iss') "/DAppVersion=$version" "/DServiceDir=$(Join-Path $Output 'service')" "/DPanelDir=$(Join-Path $Output 'panel')"
    if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
    Write-Host "Готово: $(Join-Path $Output "installer\AtentisServer-Setup-$version.exe")"
} else {
    Write-Warning "Inno Setup не найден - установщик не собран. Службу и панель можно взять из $Output. Установите Inno Setup 6 и запустите скрипт снова."
}
