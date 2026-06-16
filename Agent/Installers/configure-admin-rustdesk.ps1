#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Configure RustDesk to connect through this Peripheral Monitor's
    self-hosted relay server. Run this ONCE on the admin's own machine
    before using "Remote Desktop" links from the dashboard.
.DESCRIPTION
    Branches on whether RustDesk is already installed:

    CASE 1 - RustDesk NOT installed:
        Installs it normally (default location) and configures it directly
        to use this server's relay. No conflict possible since nothing
        existed before.

    CASE 2 - RustDesk ALREADY installed (admin has their own setup/relay):
        Does NOT touch the existing installation or its configuration.
        Instead, deploys a separate PORTABLE copy of RustDesk into its own
        isolated folder with its own config directory, pointed at this
        server's relay. A dedicated shortcut launches this isolated copy.
        The admin's existing RustDesk + relay remain completely untouched.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ServerAddr,

    [string]$RustDeskMinVersion = "1.2.0",
    [string]$PortablePath = "C:\PeripheralMonitorRustDesk"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "    [FAIL] $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "======================================================" -ForegroundColor White
Write-Host "  RustDesk Client Configuration" -ForegroundColor White
Write-Host "  Target server: $ServerAddr" -ForegroundColor White
Write-Host "======================================================" -ForegroundColor White

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "This script must be run as Administrator."
    exit 1
}

# ── Detect existing installation ────────────────────────────────────────
Write-Step "Checking for an existing RustDesk installation..."

$mainExePath = "${env:ProgramFiles}\RustDesk\rustdesk.exe"
$mainInstalled = Test-Path $mainExePath

if ($mainInstalled) {
    Write-Warn "RustDesk is already installed at $mainExePath"
    Write-Host "    To avoid overwriting your existing relay configuration," -ForegroundColor Gray
    Write-Host "    a separate, isolated copy will be set up instead." -ForegroundColor Gray
} else {
    Write-Ok "No existing RustDesk installation found — clean install path"
}

# ══════════════════════════════════════════════════════════════════════════
# CASE 1: Not installed — install normally, configure directly
# ══════════════════════════════════════════════════════════════════════════
if (-not $mainInstalled) {

    Write-Step "Installing RustDesk..."

    $rdInstaller = Join-Path $env:TEMP 'rustdesk-installer.exe'
    $rdUrl = 'https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-1.2.3-x86_64.exe'

    Write-Host "    Downloading RustDesk..." -ForegroundColor Gray
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $rdUrl -OutFile $rdInstaller -UseBasicParsing

    Write-Host "    Installing..." -ForegroundColor Gray
    Start-Process -FilePath $rdInstaller -ArgumentList '--silent-install' -Wait -NoNewWindow
    Remove-Item $rdInstaller -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    if (-not (Test-Path $mainExePath)) {
        Write-Fail "Installation failed — executable not found at $mainExePath"
        exit 1
    }
    Write-Ok "RustDesk installed"

    Write-Step "Configuring RustDesk to use this server's relay..."
    & $mainExePath --config "host=$ServerAddr,key=" 2>$null
    Start-Sleep -Seconds 1
    Write-Ok "RustDesk is now configured to use: $ServerAddr"

    Write-Host ""
    Write-Host "======================================================" -ForegroundColor Green
    Write-Host "  Done! Remote Desktop links from the dashboard" -ForegroundColor Green
    Write-Host "  will now connect correctly using your RustDesk install." -ForegroundColor Green
    Write-Host "======================================================" -ForegroundColor Green
    exit 0
}

# ══════════════════════════════════════════════════════════════════════════
# CASE 2: Already installed — deploy an isolated portable copy instead.
# The existing installation and its config are never touched.
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Setting up an isolated RustDesk copy for this deployment..."

New-Item -ItemType Directory -Path $PortablePath -Force | Out-Null
$portableExe = Join-Path $PortablePath 'rustdesk.exe'

$needsDownload = $true
if (Test-Path $portableExe) {
    try {
        $verInfo = (Get-Item $portableExe).VersionInfo
        $installedVersion = [version]"$($verInfo.FileMajorPart).$($verInfo.FileMinorPart).$($verInfo.FileBuildPart)"
        if ($installedVersion -ge [version]$RustDeskMinVersion) {
            Write-Ok "Isolated copy already present ($installedVersion)"
            $needsDownload = $false
        }
    } catch { }
}

if ($needsDownload) {
    $portableZip = Join-Path $env:TEMP 'rustdesk-portable.zip'
    $rdUrl = 'https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-1.2.3-x86_64-portable.exe'

    Write-Host "    Downloading portable RustDesk..." -ForegroundColor Gray
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    try {
        # The "-portable.exe" build is a self-extracting single file — copy it directly, no install step.
        Invoke-WebRequest -Uri $rdUrl -OutFile $portableExe -UseBasicParsing
        Write-Ok "Portable RustDesk downloaded to $portableExe"
    } catch {
        Write-Fail "Failed to download portable RustDesk: $_"
        Write-Host "    Download manually from: https://github.com/rustdesk/rustdesk/releases" -ForegroundColor Gray
        exit 1
    }
}

# Isolated config directory — completely separate from the main install's config.
$isolatedConfigDir = Join-Path $PortablePath 'config'
New-Item -ItemType Directory -Path $isolatedConfigDir -Force | Out-Null

Write-Step "Configuring the isolated copy to use this server's relay..."
Write-Host "    (your existing RustDesk installation and its settings are NOT modified)" -ForegroundColor Gray

# RUSTDESK_CONFIG_DIR redirects where this process instance reads/writes its config,
# isolating it from the main installation's config under %APPDATA%\RustDesk.
$env:RUSTDESK_CONFIG_DIR = $isolatedConfigDir
& $portableExe --config "host=$ServerAddr,key=" 2>$null
Start-Sleep -Seconds 1

Write-Ok "Isolated RustDesk configured to use: $ServerAddr"

# Desktop shortcut that launches the isolated copy with the right env var set,
# so double-clicking it never touches the main installation.
$launcherPath = Join-Path $PortablePath 'Launch-PeripheralMonitor-RustDesk.bat'
@"
@echo off
set RUSTDESK_CONFIG_DIR=$isolatedConfigDir
start "" "$portableExe" %*
"@ | Set-Content -Path $launcherPath -Encoding ASCII

$desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Peripheral Monitor Remote Desktop.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($desktopShortcut)
$shortcut.TargetPath = $launcherPath
$shortcut.WorkingDirectory = $PortablePath
$shortcut.Description = "Isolated RustDesk for Peripheral Monitor (does not affect your main RustDesk setup)"
$shortcut.Save()

Write-Ok "Launcher created: $launcherPath"
Write-Ok "Desktop shortcut created for convenience"

Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Done! An isolated RustDesk copy is ready." -ForegroundColor Green
Write-Host "  Your existing RustDesk installation is untouched." -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  IMPORTANT: dashboard 'rustdesk://' links open whichever" -ForegroundColor Yellow
Write-Host "  RustDesk install is registered as the system's protocol" -ForegroundColor Yellow
Write-Host "  handler - which will be your EXISTING install, not this" -ForegroundColor Yellow
Write-Host "  isolated one. For sessions through THIS server, launch" -ForegroundColor Yellow
Write-Host "  '$launcherPath' manually and enter the agent's RustDesk ID" -ForegroundColor Yellow
Write-Host "  shown on the dashboard yourself." -ForegroundColor Yellow
Write-Host ""
