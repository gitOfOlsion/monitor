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
        Instead, sets up a separate, truly isolated copy in its own folder
        with its own redirected profile (%USERPROFILE%), so it keeps its
        own config/log files completely separate from the main install.
        The admin's existing RustDesk + relay remain completely untouched.

    Always resolves the latest RustDesk release via the GitHub API rather
    than a hardcoded version number, since pinning to a fixed version
    silently breaks once that release is no longer the latest (assets can
    be removed or renamed across releases).
#>

param(
    [string]$ServerAddr = "",
    [string]$Key = "",
    [string]$PortablePath = "C:\PeripheralMonitorRustDesk"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "    [FAIL] $msg" -ForegroundColor Red }

# Validate - either pre-filled by the server-generated download, or passed manually.
if ([string]::IsNullOrWhiteSpace($ServerAddr) -or [string]::IsNullOrWhiteSpace($Key)) {
    Write-Fail "ServerAddr and Key are required."
    Write-Host "  If you downloaded this script from the dashboard, re-download it -" -ForegroundColor Gray
    Write-Host "  the dashboard pre-fills these values automatically." -ForegroundColor Gray
    Write-Host "  Or run manually:" -ForegroundColor Gray
    Write-Host "    .\configure-admin-rustdesk.ps1 -ServerAddr <ip> -Key <public_key>" -ForegroundColor Gray
    exit 1
}

function Get-LatestRustDeskAsset {
    # Queries GitHub's API for the latest release and returns the download URL
    # for the Windows x86_64 installer-style exe (e.g. rustdesk-1.4.6-x86_64.exe).
    # Never hardcode a version number - GitHub removes/renames old assets over time.
    $api = "https://api.github.com/repos/rustdesk/rustdesk/releases/latest"
    $headers = @{ "User-Agent" = "PeripheralMonitor-Setup" }
    $release = Invoke-RestMethod -Uri $api -Headers $headers -UseBasicParsing

    $asset = $release.assets | Where-Object {
        $_.name -match '^rustdesk-[\d\.]+-x86_64\.exe$'
    } | Select-Object -First 1

    if (-not $asset) {
        throw "Could not find a Windows x86_64 .exe asset in the latest RustDesk release ($($release.tag_name))"
    }
    return @{ Url = $asset.browser_download_url; Version = $release.tag_name; FileName = $asset.name }
}

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

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# -- Detect existing installation ----------------------------------------
Write-Step "Checking for an existing RustDesk installation..."

$mainExePath = "${env:ProgramFiles}\RustDesk\rustdesk.exe"
$mainInstalled = Test-Path $mainExePath

if ($mainInstalled) {
    Write-Warn "RustDesk is already installed at $mainExePath"
    Write-Host "    To avoid overwriting your existing relay configuration," -ForegroundColor Gray
    Write-Host "    a separate, isolated copy will be set up instead." -ForegroundColor Gray
} else {
    Write-Ok "No existing RustDesk installation found - clean install path"
}

# -- Resolve latest release (used by both cases below) -------------------
Write-Step "Checking latest RustDesk release..."
try {
    $latest = Get-LatestRustDeskAsset
    Write-Ok "Latest version: $($latest.Version) ($($latest.FileName))"
} catch {
    Write-Fail "Could not determine the latest RustDesk release: $_"
    Write-Host "    Check your internet connection, or download manually from:" -ForegroundColor Gray
    Write-Host "    https://github.com/rustdesk/rustdesk/releases" -ForegroundColor Gray
    exit 1
}

# ==========================================================================
# CASE 1: Not installed - install normally, configure directly
# ==========================================================================
if (-not $mainInstalled) {

    Write-Step "Installing RustDesk $($latest.Version)..."

    $rdInstaller = Join-Path $env:TEMP $latest.FileName

    Write-Host "    Downloading..." -ForegroundColor Gray
    Invoke-WebRequest -Uri $latest.Url -OutFile $rdInstaller -UseBasicParsing

    Write-Host "    Installing..." -ForegroundColor Gray
    Start-Process -FilePath $rdInstaller -ArgumentList '--silent-install' -Wait -NoNewWindow
    Remove-Item $rdInstaller -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    if (-not (Test-Path $mainExePath)) {
        Write-Fail "Installation failed - executable not found at $mainExePath"
        exit 1
    }
    Write-Ok "RustDesk installed"

    Write-Step "Configuring RustDesk to use this server's relay..."
    & $mainExePath --config "host=$ServerAddr,key=$Key" 2>$null
    Start-Sleep -Seconds 1
    Write-Ok "RustDesk is now configured to use: $ServerAddr"

    Write-Host ""
    Write-Host "======================================================" -ForegroundColor Green
    Write-Host "  Done! Remote Desktop links from the dashboard" -ForegroundColor Green
    Write-Host "  will now connect correctly using your RustDesk install." -ForegroundColor Green
    Write-Host "======================================================" -ForegroundColor Green
    exit 0
}

# ==========================================================================
# CASE 2: Already installed - set up a truly isolated copy instead.
# The existing installation and its config are never touched.
#
# RustDesk is not a "true" portable program out of the box: even the
# installer-style exe self-extracts to %LOCALAPPDATA%\rustdesk and reads
# its config from %APPDATA%\rustdesk, both derived from %USERPROFILE%.
# To isolate it, we run the installer once into a temp profile, copy the
# resulting program files into our own folder, then launch it later with
# %USERPROFILE% redirected to that same folder - so its AppData/Roaming
# config lives inside $PortablePath, never touching the real profile.
# ==========================================================================

Write-Step "Setting up an isolated RustDesk copy for this deployment..."

New-Item -ItemType Directory -Path $PortablePath -Force | Out-Null
$isolatedExePath = Join-Path $PortablePath 'rustdesk.exe'

if (Test-Path $isolatedExePath) {
    Write-Ok "Isolated copy already present at $PortablePath"
} else {
    $rdInstaller = Join-Path $env:TEMP $latest.FileName
    Write-Host "    Downloading RustDesk $($latest.Version)..." -ForegroundColor Gray
    Invoke-WebRequest -Uri $latest.Url -OutFile $rdInstaller -UseBasicParsing

    Write-Host "    Extracting program files (no install, no relay contact yet)..." -ForegroundColor Gray
    # Running with --noinstall extracts the program image without registering
    # it as an installed application or touching the real user profile's config.
    Start-Process -FilePath $rdInstaller -ArgumentList '--noinstall' -Wait -NoNewWindow -ErrorAction SilentlyContinue

    $extractedDir = Join-Path $env:LOCALAPPDATA 'rustdesk'
    if (Test-Path (Join-Path $extractedDir 'rustdesk.exe')) {
        Copy-Item -Path (Join-Path $extractedDir '*') -Destination $PortablePath -Recurse -Force
        Write-Ok "Program files copied to isolated folder: $PortablePath"
    } else {
        Write-Fail "Could not locate extracted RustDesk program files at $extractedDir"
        Write-Host "    Falling back to a plain copy of the downloaded installer (will self-extract on first run)." -ForegroundColor Gray
        Copy-Item -Path $rdInstaller -Destination $isolatedExePath -Force
    }
    Remove-Item $rdInstaller -ErrorAction SilentlyContinue
}

# Redirect the profile this isolated copy uses for its config/log files,
# so it never reads or writes to the admin's real %APPDATA%\rustdesk.
$isolatedProfileDir = Join-Path $PortablePath 'profile'
$isolatedConfigDir  = Join-Path $isolatedProfileDir 'AppData\Roaming\RustDesk\config'
New-Item -ItemType Directory -Path $isolatedConfigDir -Force | Out-Null

Write-Step "Configuring the isolated copy to use this server's relay..."
Write-Host "    (your existing RustDesk installation and its settings are NOT modified)" -ForegroundColor Gray

# IMPORTANT: we do NOT call 'rustdesk.exe --config' here. That subcommand talks
# to an already-running RustDesk instance over local IPC and applies the change
# to WHICHEVER copy is currently alive (your main install, since its service is
# running) - completely ignoring our %USERPROFILE% redirect. Calling it would
# silently reconfigure your real RustDesk instead of the isolated one.
#
# Instead we write RustDesk2.toml directly into the isolated profile, which is
# the same file the application itself would produce - no live process involved.
$configToml = @"
rendezvous_server = '$ServerAddr'
nat_type = 1
serial = 0

[options]
custom-rendezvous-server = '$ServerAddr'
relay-server = '$ServerAddr'
key = '$Key'
always-use-relay = 'Y'
"@
Set-Content -Path (Join-Path $isolatedConfigDir 'RustDesk2.toml') -Value $configToml -Encoding ASCII

Write-Ok "Isolated RustDesk configured to use: $ServerAddr"
Write-Host "    Config file written directly to: $isolatedConfigDir\RustDesk2.toml" -ForegroundColor Gray
Write-Host "    (no live RustDesk process was contacted)" -ForegroundColor Gray

# Launcher that always redirects %USERPROFILE% before starting the isolated copy,
# so double-clicking it never touches the main installation's profile/config.
$launcherPath = Join-Path $PortablePath 'Launch-PeripheralMonitor-RustDesk.bat'
$launcherContent = "@echo off`r`nset USERPROFILE=$isolatedProfileDir`r`nstart `"`" `"$isolatedExePath`" %*`r`n"
Set-Content -Path $launcherPath -Value $launcherContent -Encoding ASCII

$desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Peripheral Monitor Remote Desktop.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($desktopShortcut)
$shortcut.TargetPath = $launcherPath
$shortcut.WorkingDirectory = $PortablePath
$shortcut.Description = "Isolated RustDesk for Peripheral Monitor (does not affect your main RustDesk setup)"
$shortcut.Save()

Write-Ok "Launcher created: $launcherPath"
Write-Ok "Desktop shortcut created for convenience"

# -- Register a dedicated "rustdesk-pm://" protocol handler ---------------
# We deliberately do NOT register the "rustdesk://" scheme itself - that would
# fight with whatever your main install already owns, recreating the same
# conflict we're avoiding. Instead, the dashboard's "Try to connect" button
# uses a distinct "rustdesk-pm://{id}/{password}" link that only this isolated
# copy understands, so it always launches the correctly-configured instance.
#
# Registered under HKEY_CURRENT_USER (not HKLM), so no extra elevation beyond
# what this script already requires, and it only affects the current user.
Write-Step "Registering 'rustdesk-pm://' link handler for one-click connect..."

$connectHandlerPath = Join-Path $PortablePath 'Connect-PeripheralMonitor-RustDesk.bat'
$connectHandlerContent = @'
@echo off
setlocal enabledelayedexpansion
set "URI=%~1"
set "URI=%URI:rustdesk-pm://=%"
for /f "tokens=1,2 delims=|" %%a in ("%URI%") do (
    set "RDID=%%a"
    set "RDPASS=%%b"
)
set USERPROFILE={ISOLATED_PROFILE}
start "" "{ISOLATED_EXE}" --connect %RDID% --password "%RDPASS%"
'@
$connectHandlerContent = $connectHandlerContent.Replace('{ISOLATED_PROFILE}', $isolatedProfileDir).Replace('{ISOLATED_EXE}', $isolatedExePath)
Set-Content -Path $connectHandlerPath -Value $connectHandlerContent -Encoding ASCII

$protocolKey = "HKCU:\SOFTWARE\Classes\rustdesk-pm"
New-Item -Path $protocolKey -Force | Out-Null
Set-ItemProperty -Path $protocolKey -Name '(Default)' -Value 'URL:Peripheral Monitor RustDesk Connect'
Set-ItemProperty -Path $protocolKey -Name 'URL Protocol' -Value ''

$commandKey = "$protocolKey\shell\open\command"
New-Item -Path $commandKey -Force | Out-Null
Set-ItemProperty -Path $commandKey -Name '(Default)' -Value "`"$connectHandlerPath`" `"%1`""

Write-Ok "Protocol handler registered: rustdesk-pm://"
Write-Host "    Dashboard 'Try to connect' links will now launch the isolated copy directly." -ForegroundColor Gray

Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Done! An isolated RustDesk copy is ready." -ForegroundColor Green
Write-Host "  Your existing RustDesk installation is untouched." -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Dashboard 'Try to connect' links now work automatically -" -ForegroundColor Gray
Write-Host "  they launch this isolated copy directly with the right" -ForegroundColor Gray
Write-Host "  server, ID, and password, without touching your main" -ForegroundColor Gray
Write-Host "  RustDesk install or its relay configuration." -ForegroundColor Gray
Write-Host ""
