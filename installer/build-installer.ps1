<#
  Genera el instalador de MOVE · DICOM Migrator.

    .\installer\build-installer.ps1 -Version 1.7.1

  1. dotnet publish (Release, win-x64, autónomo) a installer\out\publish
  2. Compila installer\DicomMigrator.iss con Inno Setup 6.3+ (ISCC.exe)
  Resultado: installer\out\DicomMigrator-Setup-<Version>.exe
#>
param(
    [string]$Version = '1.7.1',
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root       = Split-Path -Parent $PSScriptRoot
$installer  = $PSScriptRoot
$publishDir = Join-Path $installer 'out\publish'

if (-not $Iscc) {
    $Iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'No se encuentra ISCC.exe (Inno Setup 6). Instálalo o pásalo con -Iscc.' }

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "==> Publicando v$Version en $publishDir" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\DicomMigrator.Web\DicomMigrator.Web.csproj') `
    -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

Write-Host "==> Compilando instalador" -ForegroundColor Cyan
& $Iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $installer 'DicomMigrator.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC falló ($LASTEXITCODE)" }

Write-Host "==> $(Join-Path $installer "out\DicomMigrator-Setup-$Version.exe")" -ForegroundColor Green
