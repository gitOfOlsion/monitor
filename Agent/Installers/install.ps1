#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Peripheral Monitor Agent — Automated Windows Installer
.DESCRIPTION
    - Checks/installs .NET 10 Runtime
    - Creates a dedicated service account (PeripheralMonitorSvc)
    - Installs the agent to C:\PeripheralMonitor
    - Registers and starts as a Windows Service
    - Configures auto-start after boot
.NOTES
    Run as Administrator: Right-click PowerShell → "Run as Administrator"
    Or: Start-Process powershell -Verb RunAs -ArgumentList "-File install.ps1"
#>

param(
    [string]$InstallPath  = "C:\PeripheralMonitor",
    [string]$ServiceName  = "PeripheralMonitorAgent",
    [string]$ServiceUser  = "PeripheralMonitorSvc",
    [string]$DotNetVersion = "10.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ══════════════════════════════════════════════════════════════════════════
# HELPER FUNCTIONS
# ══════════════════════════════════════════════════════════════════════════

function Write-Step($msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    ✓ $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    ⚠ $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "    ✗ $msg" -ForegroundColor Red }

function Test-AdminPrivileges {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ══════════════════════════════════════════════════════════════════════════
# STEP 0: Verify admin
# ══════════════════════════════════════════════════════════════════════════

Write-Host "`n═══════════════════════════════════════════════════" -ForegroundColor White
Write-Host "  Peripheral Monitor Agent — Windows Installer" -ForegroundColor White
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor White

if (-not (Test-AdminPrivileges)) {
    Write-Fail "This script must be run as Administrator."
    Write-Host "    Right-click PowerShell → 'Run as Administrator'" -ForegroundColor Gray
    exit 1
}
Write-Ok "Running as Administrator"

# ══════════════════════════════════════════════════════════════════════════
# STEP 1: Check/Install .NET Runtime
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Checking .NET Runtime..."

$dotnetInstalled = $false
$requiredMajor   = [int]($DotNetVersion.Split('.')[0])

try {
    $runtimes = & dotnet --list-runtimes 2>&1
    $netCoreRuntimes = $runtimes | Where-Object { $_ -match "Microsoft\.NETCore\.App\s+(\d+)\.\d+" }

    foreach ($rt in $netCoreRuntimes) {
        if ($rt -match "Microsoft\.NETCore\.App\s+(\d+)\.") {
            $installedMajor = [int]$Matches[1]
            if ($installedMajor -ge $requiredMajor) {
                Write-Ok ".NET $installedMajor.x runtime found"
                $dotnetInstalled = $true
                break
            }
        }
    }
} catch {
    Write-Warn ".NET CLI not found"
}

if (-not $dotnetInstalled) {
    Write-Warn ".NET $DotNetVersion runtime not found — installing..."

    $installerUrl  = "https://dot.net/v1/dotnet-install.ps1"
    $installerPath = Join-Path $env:TEMP "dotnet-install.ps1"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

        # Install the ASP.NET Core runtime (includes .NET runtime)
        & $installerPath -Channel $DotNetVersion -Runtime aspnetcore -InstallDir "C:\Program Files\dotnet"
        & $installerPath -Channel $DotNetVersion -Runtime dotnet     -InstallDir "C:\Program Files\dotnet"

        # Refresh PATH
        $env:PATH = "C:\Program Files\dotnet;$env:PATH"
        [Environment]::SetEnvironmentVariable("PATH", "C:\Program Files\dotnet;$([Environment]::GetEnvironmentVariable('PATH', 'Machine'))", "Machine")

        Write-Ok ".NET $DotNetVersion runtime installed"
    } catch {
        Write-Fail "Failed to install .NET runtime: $_"
        Write-Host "    Download manually: https://dotnet.microsoft.com/download/dotnet/$DotNetVersion" -ForegroundColor Gray
        exit 1
    } finally {
        Remove-Item $installerPath -ErrorAction SilentlyContinue
    }
}

# ══════════════════════════════════════════════════════════════════════════
# STEP 2: Create service account
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Configuring service account '$ServiceUser'..."

$userExists = Get-LocalUser -Name $ServiceUser -ErrorAction SilentlyContinue

if (-not $userExists) {
    # Generate a strong random password
    Add-Type -AssemblyName System.Web
    $password = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    $securePass = ConvertTo-SecureString $password -AsPlainText -Force

    New-LocalUser -Name $ServiceUser `
                  -Password $securePass `
                  -FullName "Peripheral Monitor Service Account" `
                  -Description "Runs the Peripheral Monitor Agent service" `
                  -PasswordNeverExpires `
                  -UserMayNotChangePassword `
                  -AccountNeverExpires | Out-Null

    Write-Ok "Service account '$ServiceUser' created"

    # Store password securely for service registration
    $script:svcPassword = $securePass
} else {
    Write-Ok "Service account '$ServiceUser' already exists"
    # Reset password for service registration
    Add-Type -AssemblyName System.Web
    $password = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    $securePass = ConvertTo-SecureString $password -AsPlainText -Force
    Set-LocalUser -Name $ServiceUser -Password $securePass
    $script:svcPassword = $securePass
}

# Grant "Log on as a service" right using secedit
Write-Step "Granting 'Log on as a service' right..."
try {
    $tempCfg   = Join-Path $env:TEMP "pm-secedit.cfg"
    $tempDb    = Join-Path $env:TEMP "pm-secedit.sdb"
    secedit /export /cfg $tempCfg /quiet

    $content   = Get-Content $tempCfg
    $sidObj    = (New-Object System.Security.Principal.NTAccount($ServiceUser)).Translate([System.Security.Principal.SecurityIdentifier])
    $sid       = $sidObj.Value

    $newContent = $content -replace '(SeServiceLogonRight\s*=\s*)', "`$1*$sid,"
    if ($newContent -eq $content) {
        # Key not found — add it
        $newContent += "`r`nSeServiceLogonRight = *$sid"
    }
    $newContent | Set-Content $tempCfg
    secedit /configure /db $tempDb /cfg $tempCfg /quiet
    Remove-Item $tempCfg, $tempDb -ErrorAction SilentlyContinue

    Write-Ok "'Log on as a service' granted to $ServiceUser"
} catch {
    Write-Warn "Could not grant SeServiceLogonRight automatically: $_"
    Write-Host "    You may need to set this manually in Local Security Policy" -ForegroundColor Gray
}

# ══════════════════════════════════════════════════════════════════════════
# STEP 3: Install agent files
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Installing agent to $InstallPath..."

# Create directory
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

# Create logs and data subdirectories
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null

# Copy agent files (everything in same directory as this script, except the script itself)
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentFiles = Get-ChildItem $scriptDir -Exclude "install.ps1", "install.sh", "*.service", "*.rules"

foreach ($file in $agentFiles) {
    Copy-Item $file.FullName -Destination $InstallPath -Recurse -Force
}

Write-Ok "Agent files copied to $InstallPath"

# ══════════════════════════════════════════════════════════════════════════
# STEP 4: Set folder permissions (ACL)
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Setting folder permissions..."

$acl = Get-Acl $InstallPath

# Remove inheritance, copy existing rules
$acl.SetAccessRuleProtection($true, $true)

# Grant service account full control
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $ServiceUser,
    "FullControl",
    "ContainerInherit,ObjectInherit",
    "None",
    "Allow"
)
$acl.AddAccessRule($rule)
Set-Acl -Path $InstallPath -AclObject $acl

# Apply to subdirectories
Get-ChildItem $InstallPath -Recurse -Directory | ForEach-Object {
    Set-Acl -Path $_.FullName -AclObject $acl
}

Write-Ok "Permissions set — $ServiceUser has full control of $InstallPath"

# ══════════════════════════════════════════════════════════════════════════
# STEP 5: Register Windows Service
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Registering Windows Service '$ServiceName'..."

$exePath = Join-Path $InstallPath "PeripheralMonitor.Agent.exe"

if (-not (Test-Path $exePath)) {
    Write-Fail "Agent executable not found at $exePath"
    exit 1
}

# Stop and remove existing service if present
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service already exists — stopping and removing..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create the service running as the service account
$credential = New-Object System.Management.Automation.PSCredential(".\$ServiceUser", $script:svcPassword)

New-Service -Name $ServiceName `
            -BinaryPathName $exePath `
            -DisplayName "Peripheral Monitor Agent" `
            -Description "Monitors peripheral devices and reports to the central dashboard" `
            -StartupType Automatic `
            -Credential $credential | Out-Null

Write-Ok "Service '$ServiceName' registered"

# Configure service recovery: restart on failure
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Ok "Recovery policy: auto-restart on failure (5s, 10s, 30s)"

# ══════════════════════════════════════════════════════════════════════════
# STEP 6: Start the service
# ══════════════════════════════════════════════════════════════════════════

Write-Step "Starting service..."

try {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    $svc = Get-Service -Name $ServiceName
    if ($svc.Status -eq "Running") {
        Write-Ok "Service is running"
    } else {
        Write-Warn "Service status: $($svc.Status)"
        Write-Host "    Check logs at: $InstallPath\logs\" -ForegroundColor Gray
    }
} catch {
    Write-Fail "Failed to start service: $_"
    Write-Host "    Check logs at: $InstallPath\logs\" -ForegroundColor Gray
    Write-Host "    Or run manually: & '$exePath'" -ForegroundColor Gray
}

# ══════════════════════════════════════════════════════════════════════════
# DONE
# ══════════════════════════════════════════════════════════════════════════

Write-Host "`n═══════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Install path:  $InstallPath"
Write-Host "  Service name:  $ServiceName"
Write-Host "  Service user:  .\$ServiceUser"
Write-Host "  Logs:          $InstallPath\logs\"
Write-Host "  Config:        $InstallPath\appsettings.local.json"
Write-Host ""
Write-Host "  Manage:" -ForegroundColor Gray
Write-Host "    Stop:    Stop-Service $ServiceName"
Write-Host "    Start:   Start-Service $ServiceName"
Write-Host "    Status:  Get-Service $ServiceName"
Write-Host "    Remove:  sc.exe delete $ServiceName"
Write-Host ""
