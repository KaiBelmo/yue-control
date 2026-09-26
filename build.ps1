<#
.SYNOPSIS
  Builds yuecontrol as a single .exe, installs it to %LOCALAPPDATA%\yuecontrol,
  registers it to start with Windows and launches it.

.PARAMETER NoAutostart
  Skip the "start with Windows" registration.

.PARAMETER NoRun
  Do not launch the app after installing.
#>
param(
    [switch]$NoAutostart,
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'

# Prefer the user-local SDK install, fall back to whatever "dotnet" is on PATH.
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$root = $PSScriptRoot
$publishDir = Join-Path $root 'publish'
$installDir = Join-Path $env:LOCALAPPDATA 'yuecontrol'
$exe = Join-Path $installDir 'yuecontrol.exe'

Write-Host "Publishing..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'yuecontrol.csproj') -c Release -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Installing to $installDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $installDir | Out-Null

# One-time migration from older names ("SpotifyHotkeys", then "Spotikey"): stop them, keep the newest
# config, drop their autostart entry and install folder.
foreach ($oldName in @('Spotikey', 'SpotifyHotkeys')) {
    $oldDir = Join-Path $env:LOCALAPPDATA $oldName
    $old = Get-Process $oldName -ErrorAction SilentlyContinue
    if ($old) { $old | Stop-Process -Force; $old | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }
    if ((Test-Path (Join-Path $oldDir 'config.json')) -and -not (Test-Path (Join-Path $installDir 'config.json'))) {
        Copy-Item (Join-Path $oldDir 'config.json') $installDir
    }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $oldName -ErrorAction SilentlyContinue
    if (Test-Path $oldDir) { Remove-Item $oldDir -Recurse -Force -ErrorAction SilentlyContinue }
}

$running = Get-Process yuecontrol -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# The old image can stay mapped for a moment after the process exits; retry the copy briefly.
$copied = $false
for ($i = 0; $i -lt 20 -and -not $copied; $i++) {
    try {
        Copy-Item (Join-Path $publishDir 'yuecontrol.exe') $exe -Force -ErrorAction Stop
        $copied = $true
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $copied) { throw "Could not overwrite $exe (still in use?)" }

# Chrome extension lives next to the exe so "Load unpacked" has a stable path.
$extDest = Join-Path $installDir 'extension'
if (Test-Path $extDest) { Remove-Item $extDest -Recurse -Force }
Copy-Item (Join-Path $root 'extension') $extDest -Recurse -Force

if (-not $NoAutostart) {
    Write-Host "Registering autostart (HKCU\...\Run)" -ForegroundColor Cyan
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'yuecontrol' -Value "`"$exe`""
}

if (-not $NoRun) {
    Write-Host "Starting $exe" -ForegroundColor Cyan
    Start-Process $exe
}

Write-Host "Done." -ForegroundColor Green
