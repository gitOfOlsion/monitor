#Requires -RunAsAdministrator
param(
    [string]$InstallPath  = "C:\PeripheralMonitor",
    [string]$ServiceName  = "PeripheralMonitorAgent",
    [string]$ServiceUser  = "PeripheralMonitorSvc",
    [string]$DotNetVersion = "10.0"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "    [FAIL] $msg" -ForegroundColor Red }

function New-RandomPassword {
    param([int]$Length = 24)
    $chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*'
    $rng   = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $bytes = [byte[]]::new($Length)
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# ======================================================================
# STEP 0: Verify admin
# ======================================================================

Write-Host ""
Write-Host "======================================================" -ForegroundColor White
Write-Host "  Peripheral Monitor Agent - Windows Installer" -ForegroundColor White
Write-Host "======================================================" -ForegroundColor White

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "This script must be run as Administrator."
    exit 1
}
Write-Ok "Running as Administrator"

# ======================================================================
# STEP 1: Check/Install .NET Runtime
# ======================================================================

Write-Step "Checking .NET Runtime..."

$dotnetInstalled = $false
$requiredMajor   = [int]($DotNetVersion.Split('.')[0])

try {
    $runtimes = & dotnet --list-runtimes 2>&1
    foreach ($rt in $runtimes) {
        if ($rt -match 'Microsoft\.NETCore\.App\s+(\d+)\.') {
            if ([int]$Matches[1] -ge $requiredMajor) {
                Write-Ok ".NET $($Matches[1]).x runtime found"
                $dotnetInstalled = $true
                break
            }
        }
    }
} catch {
    Write-Warn ".NET CLI not found"
}

if (-not $dotnetInstalled) {
    Write-Warn ".NET $DotNetVersion runtime not found - installing..."

    $installerUrl  = 'https://dot.net/v1/dotnet-install.ps1'
    $installerPath = Join-Path $env:TEMP 'dotnet-install.ps1'

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

        & $installerPath -Channel $DotNetVersion -Runtime aspnetcore -InstallDir 'C:\Program Files\dotnet'
        & $installerPath -Channel $DotNetVersion -Runtime dotnet     -InstallDir 'C:\Program Files\dotnet'

        # Refresh PATH
        $dotnetDir = 'C:\Program Files\dotnet'
        $machinePath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
        if ($machinePath -notlike "*$dotnetDir*") {
            [Environment]::SetEnvironmentVariable('PATH', "$dotnetDir;$machinePath", 'Machine')
        }
        $env:PATH = "$dotnetDir;$env:PATH"

        Write-Ok ".NET $DotNetVersion runtime installed"
    } catch {
        Write-Fail "Failed to install .NET runtime: $_"
        Write-Host "    Download manually: https://dotnet.microsoft.com/download/dotnet/$DotNetVersion" -ForegroundColor Gray
        exit 1
    } finally {
        Remove-Item $installerPath -ErrorAction SilentlyContinue
    }
}

# ======================================================================
# STEP 2: Create service account
# ======================================================================

Write-Step "Configuring service account '$ServiceUser'..."

$password   = New-RandomPassword
$securePass = ConvertTo-SecureString $password -AsPlainText -Force

$userExists = Get-LocalUser -Name $ServiceUser -ErrorAction SilentlyContinue

if (-not $userExists) {
    New-LocalUser -Name $ServiceUser `
                  -Password $securePass `
                  -FullName 'Peripheral Monitor Service Account' `
                  -Description 'Runs the Peripheral Monitor Agent service' `
                  -PasswordNeverExpires `
                  -UserMayNotChangePassword `
                  -AccountNeverExpires | Out-Null
    Write-Ok "Service account '$ServiceUser' created"
} else {
    Write-Ok "Service account '$ServiceUser' already exists - resetting password"
    Set-LocalUser -Name $ServiceUser -Password $securePass
}

$script:svcPassword = $securePass

# Add service account to Performance Monitor Users group - this grants WMI read access
# to Win32_LogonSession and Win32_Process which are needed to detect the logged-in user.
try {
    Add-LocalGroupMember -Group "Performance Monitor Users" -Member $ServiceUser -ErrorAction SilentlyContinue
    Write-Ok "Added '$ServiceUser' to Performance Monitor Users (WMI access for user detection)"
} catch {
    Write-Warn "Could not add to Performance Monitor Users: $_"
}

# Grant "Log on as a service" right
Write-Step "Granting 'Log on as a service' right..."
try {
    $tempCfg = Join-Path $env:TEMP 'pm-secedit.cfg'
    $tempDb  = Join-Path $env:TEMP 'pm-secedit.sdb'
    secedit /export /cfg $tempCfg /quiet

    $content = Get-Content $tempCfg -Raw
    $sidObj  = (New-Object System.Security.Principal.NTAccount($ServiceUser)).Translate(
                [System.Security.Principal.SecurityIdentifier])
    $sid     = $sidObj.Value

    if ($content -match 'SeServiceLogonRight\s*=\s*(.+)') {
        $existing = $Matches[1]
        $content  = $content -replace ('SeServiceLogonRight\s*=\s*.+'), "SeServiceLogonRight = *$sid,$existing"
    } else {
        $content += "`r`n[Privilege Rights]`r`nSeServiceLogonRight = *$sid"
    }
    $content | Set-Content $tempCfg
    secedit /configure /db $tempDb /cfg $tempCfg /quiet
    Remove-Item $tempCfg, $tempDb -ErrorAction SilentlyContinue
    Write-Ok "'Log on as a service' granted to $ServiceUser"
} catch {
    Write-Warn "Could not grant SeServiceLogonRight automatically: $_"
    Write-Host "    You may need to set this in Local Security Policy manually" -ForegroundColor Gray
}

# ======================================================================
# STEP 3: Install agent files
# ======================================================================

Write-Step "Installing agent to $InstallPath..."

if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null

# Persistent data directory in ProgramData. This holds the durable event outbox
# and the permanent event history (outbox.jsonl / history.jsonl). It lives OUTSIDE
# the install path on purpose, so reinstalling the agent (which only touches
# $InstallPath) never deletes the historic event log. We create it if missing but
# never clear it.
$dataDir = Join-Path $env:ProgramData 'PeripheralMonitor'
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Write-Ok "Created persistent data dir $dataDir (survives reinstall)"
} else {
    $hist = Join-Path $dataDir 'history.jsonl'
    $histInfo = if (Test-Path $hist) { " (existing history preserved: $((Get-Item $hist).Length) bytes)" } else { "" }
    Write-Ok "Persistent data dir $dataDir already exists$histInfo"
}
# Grant SYSTEM (the service account) full control of the data dir so it can write.
try {
    $dacl = Get-Acl $dataDir
    $sysRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        'NT AUTHORITY\SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $dacl.AddAccessRule($sysRule)
    Set-Acl -Path $dataDir -AclObject $dacl
} catch { Write-Warn "Could not set ACL on data dir: $_" }

# ----------------------------------------------------------------------
# CRITICAL: Stop the service BEFORE copying files. A running service holds
# PeripheralMonitor.Agent.exe open, which makes Copy-Item silently skip the
# overwrite - leaving the OLD binary in place. This is the #1 cause of
# "I reinstalled but nothing changed" problems.
# ----------------------------------------------------------------------
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Step "Stopping existing service '$ServiceName' before copying files..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    # Wait up to 15s for it to fully stop (so the binary is released)
    $waited = 0
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and $waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
    }

    # Also kill any lingering agent process not managed by SCM, just in case
    Get-Process -Name 'PeripheralMonitor.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Write-Ok "Service stopped (binary released for overwrite)"
}

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentFiles = Get-ChildItem $scriptDir -Exclude 'install.ps1','install.bat','install.sh','*.service','*.rules'

foreach ($file in $agentFiles) {
    Copy-Item $file.FullName -Destination $InstallPath -Recurse -Force
}

# Verify the agent binary was actually overwritten (compare write times)
$exeCheck = Join-Path $InstallPath 'PeripheralMonitor.Agent.exe'
if (Test-Path $exeCheck) {
    $age = (Get-Date) - (Get-Item $exeCheck).LastWriteTime
    if ($age.TotalMinutes -gt 60) {
        Write-Warn "WARNING: Agent binary is $([int]$age.TotalMinutes) min old - the copy may not have overwritten a locked file. Check that the service was stopped."
    } else {
        Write-Ok "Agent files copied to $InstallPath (binary is fresh)"
    }
} else {
    Write-Fail "Agent binary missing after copy - installation cannot continue"
    exit 1
}

# ======================================================================
# STEP 4: Set folder permissions
# ======================================================================

Write-Step "Setting folder permissions..."

$acl  = Get-Acl $InstallPath
$acl.SetAccessRuleProtection($true, $true)
# Service runs as LocalSystem, so grant SYSTEM full control (for logs/data writes).
# Also grant Administrators so the install/uninstall can manage the folder.
$ruleSystem = New-Object System.Security.AccessControl.FileSystemAccessRule(
    'NT AUTHORITY\SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$ruleAdmin = New-Object System.Security.AccessControl.FileSystemAccessRule(
    'BUILTIN\Administrators', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($ruleSystem)
$acl.AddAccessRule($ruleAdmin)
Set-Acl -Path $InstallPath -AclObject $acl

Get-ChildItem $InstallPath -Recurse -Directory | ForEach-Object {
    Set-Acl -Path $_.FullName -AclObject $acl
}
Write-Ok "Permissions set - SYSTEM has full control of $InstallPath"

# ======================================================================
# STEP 5: Register Windows Service
# ======================================================================

Write-Step "Registering Windows Service '$ServiceName'..."

$exePath = Join-Path $InstallPath 'PeripheralMonitor.Agent.exe'

if (-not (Test-Path $exePath)) {
    Write-Fail "Agent executable not found at $exePath"
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service already exists - stopping and removing..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Run as LocalSystem. The dedicated low-privilege account cannot query other
# users' session info via the WTS API (WTSQuerySessionInformation returns
# access-denied), so the logged-in user can never be detected. LocalSystem has
# the SeTcbPrivilege and session-query rights needed. A monitoring agent that
# already needs broad WMI and RDP-tunnel access is appropriately run as SYSTEM.
New-Service -Name $ServiceName `
            -BinaryPathName $exePath `
            -DisplayName 'Peripheral Monitor Agent' `
            -Description 'Monitors peripheral devices and reports to the central dashboard' `
            -StartupType Automatic | Out-Null

# New-Service defaults to LocalSystem when no -Credential is supplied.
# Explicitly set it to be certain (handles the case of an pre-existing service def).
& sc.exe config $ServiceName obj= "LocalSystem" | Out-Null

Write-Ok "Service '$ServiceName' registered (running as LocalSystem)"

# Configure recovery: restart on failure
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Ok "Recovery policy: auto-restart on failure (5s, 10s, 30s)"

# ======================================================================
# STEP 6: Enable Remote Desktop (RDP) for Guacamole web-based access
# ======================================================================

Write-Step "Enabling Remote Desktop (RDP)..."

try {
    # Enable RDP via registry
    Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' `
        -Name "fDenyTSConnections" -Value 0 -Type DWord
    Write-Ok "Remote Desktop enabled"

    # Allow RDP through Windows Firewall
    Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction SilentlyContinue
    # If the above fails (older Windows), use netsh
    netsh advfirewall firewall set rule group="remote desktop" new enable=Yes 2>$null
    Write-Ok "Firewall rule enabled for Remote Desktop"

    # Set Network Level Authentication to OPTIONAL (not required).
    # NLA pre-authenticates before the RDP session starts, which is incompatible
    # with our SignalR-tunneled RDP - the timing-sensitive NLA handshake fails
    # when proxied through our relay. Value=0 means NLA is supported but not
    # enforced. Connections are still TLS-encrypted and require valid credentials.
    Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' `
        -Name "UserAuthentication" -Value 0 -Type DWord -ErrorAction SilentlyContinue
    Write-Ok "Remote Desktop configured (NLA optional, TLS encryption enabled)"

    Write-Host "    RDP is now enabled. Guacamole will connect using the credentials" -ForegroundColor Gray
    Write-Host "    configured per institution in the dashboard." -ForegroundColor Gray
} catch {
    Write-Warn "RDP setup failed (non-fatal): $_"
    Write-Host "    You can enable Remote Desktop manually via System Properties > Remote." -ForegroundColor Gray
}

# ======================================================================
# STEP 7: Start the agent service
# ======================================================================

Write-Step "Starting service..."

try {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    $svc = Get-Service -Name $ServiceName
    if ($svc.Status -eq 'Running') {
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

# ======================================================================
# DONE
# ======================================================================

Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Install path:  $InstallPath"
Write-Host "  Service name:  $ServiceName"
Write-Host "  Service user:  .\$ServiceUser"
Write-Host "  Logs:          $InstallPath\logs\"
Write-Host "  Config:        $InstallPath\appsettings.local.json"
Write-Host "  Remote desktop: RDP enabled for web-based remote access via Guacamole"
Write-Host ""
Write-Host "  Manage:" -ForegroundColor Gray
Write-Host "    Stop:    Stop-Service $ServiceName"
Write-Host "    Start:   Start-Service $ServiceName"
Write-Host "    Status:  Get-Service $ServiceName"
Write-Host "    Remove:  sc.exe delete $ServiceName"
Write-Host ""
