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

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentFiles = Get-ChildItem $scriptDir -Exclude 'install.ps1','install.bat','install.sh','*.service','*.rules'

foreach ($file in $agentFiles) {
    Copy-Item $file.FullName -Destination $InstallPath -Recurse -Force
}
Write-Ok "Agent files copied to $InstallPath"

# ======================================================================
# STEP 4: Set folder permissions
# ======================================================================

Write-Step "Setting folder permissions..."

$acl  = Get-Acl $InstallPath
$acl.SetAccessRuleProtection($true, $true)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $ServiceUser, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -Path $InstallPath -AclObject $acl

Get-ChildItem $InstallPath -Recurse -Directory | ForEach-Object {
    Set-Acl -Path $_.FullName -AclObject $acl
}
Write-Ok "Permissions set - $ServiceUser has full control of $InstallPath"

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

$credential = New-Object System.Management.Automation.PSCredential(
    ".\$ServiceUser", $script:svcPassword)

New-Service -Name $ServiceName `
            -BinaryPathName $exePath `
            -DisplayName 'Peripheral Monitor Agent' `
            -Description 'Monitors peripheral devices and reports to the central dashboard' `
            -StartupType Automatic `
            -Credential $credential | Out-Null

Write-Ok "Service '$ServiceName' registered"

# Configure recovery: restart on failure
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Ok "Recovery policy: auto-restart on failure (5s, 10s, 30s)"

# ======================================================================
# STEP 6: Install and configure RustDesk (remote desktop)
# ======================================================================
# Checklist (same pattern as the .NET runtime check above):
#   1. Is RustDesk installed at all?            -> if not, download + silent-install
#   2. Is the installed version recent enough?  -> if not, upgrade (reinstall over it)
#   3. Is it already pointed at OUR server?      -> if not, reconfigure host + password
#      (always re-apply config even if already installed, in case it was
#       previously configured for a different relay/server)
# ======================================================================

Write-Step "Checking RustDesk (remote desktop)..."

$RustDeskMinVersion = [version]"1.2.0"

try {
    $rdConfig = Join-Path $InstallPath 'appsettings.local.json'
    $rdServerAddr = ''
    $rdPassword   = ''
    if (Test-Path $rdConfig) {
        $cfg = Get-Content $rdConfig -Raw | ConvertFrom-Json
        if ($cfg.RustDesk) {
            $rdServerAddr = $cfg.RustDesk.ServerAddr
            $rdPassword   = $cfg.RustDesk.Password
        }
    }

    if ([string]::IsNullOrWhiteSpace($rdServerAddr)) {
        Write-Warn "No RustDesk server configured in appsettings.local.json - skipping remote desktop setup"
    } else {
        $rdExePath = "${env:ProgramFiles}\RustDesk\rustdesk.exe"
        $rdInstalled = $false
        $rdNeedsUpgrade = $false

        if (Test-Path $rdExePath) {
            $rdInstalled = $true
            try {
                $verInfo = (Get-Item $rdExePath).VersionInfo
                $installedVersion = [version]"$($verInfo.FileMajorPart).$($verInfo.FileMinorPart).$($verInfo.FileBuildPart)"
                if ($installedVersion -lt $RustDeskMinVersion) {
                    Write-Warn "RustDesk $installedVersion is older than required $RustDeskMinVersion - will upgrade"
                    $rdNeedsUpgrade = $true
                } else {
                    Write-Ok "RustDesk $installedVersion already installed (meets minimum $RustDeskMinVersion)"
                }
            } catch {
                Write-Warn "Could not determine installed RustDesk version - will reinstall to be safe"
                $rdNeedsUpgrade = $true
            }
        } else {
            Write-Warn "RustDesk not found - will install"
        }

        if (-not $rdInstalled -or $rdNeedsUpgrade) {
            $rdInstaller = Join-Path $env:TEMP 'rustdesk-installer.exe'

            Write-Host "    Resolving latest RustDesk release..." -ForegroundColor Gray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            try {
                $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/rustdesk/rustdesk/releases/latest' `
                    -Headers @{ "User-Agent" = "PeripheralMonitor-Agent" } -UseBasicParsing
                $asset = $release.assets | Where-Object { $_.name -match '^rustdesk-[\d\.]+-x86_64\.exe$' } | Select-Object -First 1
                if (-not $asset) { throw "No matching x86_64 .exe asset found in release $($release.tag_name)" }
                $rdUrl = $asset.browser_download_url
                Write-Host "    Found: $($asset.name)" -ForegroundColor Gray
            } catch {
                Write-Warn "Could not resolve latest release via GitHub API ($_) - using generic 'latest' redirect URL as fallback"
                $rdUrl = 'https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-x86_64.exe'
            }

            Write-Host "    Downloading RustDesk..." -ForegroundColor Gray
            Invoke-WebRequest -Uri $rdUrl -OutFile $rdInstaller -UseBasicParsing

            Write-Host "    Installing RustDesk silently..." -ForegroundColor Gray
            Start-Process -FilePath $rdInstaller -ArgumentList '--silent-install' -Wait -NoNewWindow
            Remove-Item $rdInstaller -ErrorAction SilentlyContinue

            Start-Sleep -Seconds 3
            if (Test-Path $rdExePath) { Write-Ok "RustDesk installed/upgraded" }
            else { Write-Warn "RustDesk installer ran but executable not found at expected path" }
        }

        # Always (re)apply server + password configuration, even if RustDesk was already
        # installed - it may have been previously configured for a different relay.
        if (Test-Path $rdExePath) {
            Write-Host "    Applying server + password configuration..." -ForegroundColor Gray

            $currentConfigPath = "$env:ProgramData\RustDesk\config\RustDesk2.toml"
            $alreadyCorrect = $false
            if (Test-Path $currentConfigPath) {
                $existing = Get-Content $currentConfigPath -Raw -ErrorAction SilentlyContinue
                if ($existing -and $existing -match [regex]::Escape($rdServerAddr)) {
                    $alreadyCorrect = $true
                }
            }

            if ($alreadyCorrect) {
                Write-Ok "RustDesk already configured for this server ($rdServerAddr) - skipping reconfiguration"
            } else {
                # Configure rendezvous/relay server (key left empty - using default unencrypted handshake)
                & $rdExePath --config "host=$rdServerAddr,key=" 2>$null
                Start-Sleep -Seconds 1

                # Set fixed permanent password
                & $rdExePath --password $rdPassword 2>$null
                Start-Sleep -Seconds 1

                Write-Ok "RustDesk reconfigured for this server (was previously pointed elsewhere, or freshly installed)"
            }

            Write-Host "    Server: $rdServerAddr" -ForegroundColor Gray
            Write-Host "    The agent reports this machine's RustDesk ID to the dashboard on its next heartbeat." -ForegroundColor Gray
        }
    }
} catch {
    Write-Warn "RustDesk setup failed (non-fatal): $_"
    Write-Host "    Device monitoring will continue to work normally." -ForegroundColor Gray
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
Write-Host "  Remote desktop: RustDesk configured (ID visible in dashboard after first heartbeat)"
Write-Host ""
Write-Host "  Manage:" -ForegroundColor Gray
Write-Host "    Stop:    Stop-Service $ServiceName"
Write-Host "    Start:   Start-Service $ServiceName"
Write-Host "    Status:  Get-Service $ServiceName"
Write-Host "    Remove:  sc.exe delete $ServiceName"
Write-Host ""
