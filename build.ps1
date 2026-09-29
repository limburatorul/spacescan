<#
    Builds SpaceScan and wraps it in an installer.

    Output: dist\SpaceScan-<version>-setup.exe

    The version lives in App.cs (AssemblyFileVersion) alone, so a release is bumped in one place.
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& "$root\make-icon.ps1" | Write-Host
cmd /c "`"$root\build.cmd`""
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo("$root\SpaceScan.exe").FileVersion
if (-not $version) { throw "no version on SpaceScan.exe" }
Write-Host "SpaceScan $version" -ForegroundColor Cyan
Write-Host ("  compiled: {0:N0} KB" -f ((Get-Item "$root\SpaceScan.exe").Length / 1KB)) -ForegroundColor DarkGray

if ($SkipInstaller) { return }

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found at $iscc" }
& $iscc /Q "/DAppVersion=$version" "$root\installer\SpaceScan.iss"
if ($LASTEXITCODE -ne 0) { throw "installer failed" }
$setup = "$root\dist\SpaceScan-$version-setup.exe"
Write-Host ("  installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor DarkGray
