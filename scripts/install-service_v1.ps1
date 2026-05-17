# ============================================================
# Peripheral Monitor Agent - Windows Service Installer
# Creates a dedicated local service account (PMAgentSvc)
# that retains WMI permissions across reboots
# Run as Administrator
# ============================================================

param(
    [string]$ServerUrl       = "http://localhost:5000",
    [string]$LogPath         = "C:\ProgramData\PeripheralMonitor\Logs",
    [string]$ServiceAccount  = "PMAgentSvc",
    [string]$ServicePassword = "PM@Agent2026!Secure!"   # Change this!
)

$serviceName = "PeripheralMonitorAgent"
$displayName = "Peripheral Monitor Agent"
$installDir  = "C:\Program Files\PeripheralMonitor\Agent"
$exePath     = "$installDir\PeripheralMonitor.Agent.exe"
$configPath  = "$installDir\appsettings.json"
$localConfig = "$installDir\appsettings.local.json"
$fullAccount = "$env:COMPUTERNAME\$ServiceAccount"

# ── Require Administrator ────────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Must be run as Administrator"
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host "  Peripheral Monitor Agent Installer" -ForegroundColor Yellow
Write-Host "  Service Account: $fullAccount" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""

# ── 1. Stop & remove existing service ───────────────────────────────────
Write-Host "[ 1/9 ] Checking existing service..." -ForegroundColor Yellow
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "        Old service removed" -ForegroundColor Green
} else {
    Write-Host "        No existing service found" -ForegroundColor Green
}

# ── 2. Create directories ────────────────────────────────────────────────
Write-Host "[ 2/9 ] Creating directories..." -ForegroundColor Yellow
New-Item -Path $installDir -ItemType Directory -Force | Out-Null
New-Item -Path $LogPath    -ItemType Directory -Force | Out-Null
Write-Host "        $installDir" -ForegroundColor Green
Write-Host "        $LogPath"    -ForegroundColor Green

# ── 3. Copy published files ──────────────────────────────────────────────
Write-Host "[ 3/9 ] Copying agent files..." -ForegroundColor Yellow
$source = "$PSScriptRoot\..\publish\agent\"
if (Test-Path $source) {
    Copy-Item -Path "$source\*" -Destination $installDir -Recurse -Force
    Write-Host "        Files copied" -ForegroundColor Green
} else {
    Write-Warning "Publish output not found at: $source"
    Write-Warning "Run: dotnet publish Agent/Agent.csproj -c Release -r win-x64 --self-contained true"
    exit 1
}

# ── 4. Write configuration ───────────────────────────────────────────────
Write-Host "[ 4/9 ] Writing configuration..." -ForegroundColor Yellow
@{
    SignalRUrl = "$ServerUrl/monitor"
    Logging    = @{
        LogLevel = @{
            Default   = "Information"
            Microsoft = "Warning"
        }
    }
} | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8

if (-not (Test-Path $localConfig)) {
    @{ JwtToken = "" } | ConvertTo-Json | Set-Content -Path $localConfig -Encoding UTF8
}
Write-Host "        Config written — SignalRUrl: $ServerUrl/monitor" -ForegroundColor Green

# ── 5. Create dedicated service account ─────────────────────────────────
Write-Host "[ 5/9 ] Creating dedicated service account: $ServiceAccount..." -ForegroundColor Yellow

$existingUser = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
if ($existingUser) {
    Write-Host "        Account already exists — updating password" -ForegroundColor Gray
    $securePass = ConvertTo-SecureString $ServicePassword -AsPlainText -Force
    Set-LocalUser -Name $ServiceAccount -Password $securePass
} else {
    $securePass = ConvertTo-SecureString $ServicePassword -AsPlainText -Force
    New-LocalUser `
        -Name                 $ServiceAccount `
        -Password             $securePass `
        -PasswordNeverExpires `
        -UserMayNotChangePassword `
        -Description          "Peripheral Monitor Agent Service Account" `
        -ErrorAction Stop
    Write-Host "        Account created: $fullAccount" -ForegroundColor Green
}

# Add to Administrators group — required for WMI hardware event access
$adminsGroup = (Get-LocalGroup | Where-Object { $_.SID -eq "S-1-5-32-544" }).Name
$isMember = Get-LocalGroupMember -Group $adminsGroup -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*$ServiceAccount*" }
if (-not $isMember) {
    Add-LocalGroupMember -Group $adminsGroup -Member $ServiceAccount
    Write-Host "        Added to Administrators group" -ForegroundColor Green
} else {
    Write-Host "        Already in Administrators group" -ForegroundColor Gray
}

# Grant "Log on as a service" right via secedit
Write-Host "        Granting 'Log on as a service' right..." -ForegroundColor Gray
$tempInf = "$env:TEMP\svc_right.inf"
$tempDb  = "$env:TEMP\svc_right.sdb"
$sid     = (Get-LocalUser -Name $ServiceAccount).SID.Value

@"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeServiceLogonRight = *$sid
"@ | Set-Content -Path $tempInf -Encoding Unicode

& secedit /configure /db $tempDb /cfg $tempInf /areas USER_RIGHTS /quiet
Remove-Item $tempInf, $tempDb -ErrorAction SilentlyContinue
Write-Host "        'Log on as a service' right granted" -ForegroundColor Green

# ── 6. Set filesystem permissions ────────────────────────────────────────
Write-Host "[ 6/9 ] Setting filesystem permissions..." -ForegroundColor Yellow
foreach ($path in @($installDir, $LogPath)) {
    & icacls $path /grant "${ServiceAccount}:(OI)(CI)F" /T /Q
    & icacls $path /grant "*S-1-5-18:(OI)(CI)F"        /T /Q   # SYSTEM
    & icacls $path /grant "*S-1-5-32-544:(OI)(CI)F"    /T /Q   # Administrators
    Write-Host "        Permissions set: $path" -ForegroundColor Green
}

# ── 7. Configure WMI root\cimv2 security ────────────────────────────────
Write-Host "[ 7/9 ] Configuring WMI root\cimv2 security..." -ForegroundColor Yellow

function Set-WmiNamespaceSecurity {
    param(
        [string]$Namespace,
        [string]$AccountSid,
        [string]$AccountName,
        [int]$AccessMask
    )
    try {
        $wmiSec    = Get-WmiObject -Namespace $Namespace -Class "__SystemSecurity"
        $binarySD  = $null
        $getResult = $wmiSec.GetSD([ref]$binarySD)

        if ($getResult.ReturnValue -ne 0) {
            throw "GetSD failed: $($getResult.ReturnValue)"
        }

        $converter = New-Object System.Management.ManagementClass("Win32_SecurityDescriptorHelper")
        $sdWin32   = $converter.BinarySDToWin32SD($binarySD)
        $sd        = $sdWin32.Descriptor

        # Remove existing ACE for this account to avoid duplicates
        $sd.DACL = @($sd.DACL | Where-Object { $_.Trustee.SidString -ne $AccountSid })

        # Create trustee for our service account
        $trustee           = ([WMIClass]"Win32_Trustee").CreateInstance()
        $trustee.SidString = $AccountSid
        $trustee.Name      = $AccountName

        # Create ACE with required access rights
        $ace            = ([WMIClass]"Win32_ACE").CreateInstance()
        $ace.AccessMask = $AccessMask
        $ace.AceFlags   = 0   # No inheritance needed for namespace ACE
        $ace.AceType    = 0   # ACCESS_ALLOWED
        $ace.Trustee    = $trustee

        $sd.DACL = @($sd.DACL) + @($ace)

        $newBinarySD = $converter.Win32SDToBinarySD($sd)
        $setResult   = $wmiSec.SetSD($newBinarySD.BinarySD)

        return $setResult.ReturnValue -eq 0
    }
    catch {
        Write-Warning "WMI security error on ${Namespace}: $_"
        return $false
    }
}

# Access mask bits:
# Execute Methods = 1
# Provider Write  = 2
# Enable Account  = 4
# Remote Enable   = 32
# Read Security   = 262144
$wmiAccessMask = 1 -bor 2 -bor 4 -bor 32 -bor 262144   # = 262183

# Get SID of the dedicated service account
$svcSid = (Get-LocalUser -Name $ServiceAccount).SID.Value

foreach ($ns in @("root", "root\cimv2")) {
    $ok = Set-WmiNamespaceSecurity -Namespace $ns -AccountSid  $svcSid -AccountName $ServiceAccount -AccessMask $wmiAccessMask
    if ($ok) {
        Write-Host "WMI $ns — OK" -ForegroundColor Green
    } else {
        Write-Warning "Failed on WMI namespace: $ns"
    }
}

# ── 8. Install the service ───────────────────────────────────────────────
Write-Host "[ 8/9 ] Installing Windows Service..." -ForegroundColor Yellow

& sc.exe create $serviceName `
    binPath=     "`"$exePath`"" `
    start=       auto `
    DisplayName= $displayName `
    obj=         $fullAccount `
    password=    $ServicePassword

if ($LASTEXITCODE -ne 0) {
    Write-Error "Service creation failed (exit code $LASTEXITCODE)"
    exit 1
}

& sc.exe description $serviceName "Monitors USB/HID peripheral devices and reports to Peripheral Monitor Server"
& sc.exe failure     $serviceName reset= 60 actions= restart/5000/restart/10000/restart/30000

Write-Host "        Service installed as: $fullAccount" -ForegroundColor Green

# ── 9. Start the service ─────────────────────────────────────────────────
Write-Host "[ 9/9 ] Starting service..." -ForegroundColor Yellow
Start-Service $serviceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4

$svc   = Get-Service $serviceName -ErrorAction SilentlyContinue
$color = if ($svc.Status -eq "Running") { "Green" } else { "Red" }
Write-Host "        Status: $($svc.Status)" -ForegroundColor $color

if ($svc.Status -ne "Running") {
    Write-Host ""
    Write-Host "Service failed to start. Checking event log..." -ForegroundColor Red
    Get-EventLog -LogName Application -Newest 10 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like "*$serviceName*" -or $_.Source -like "*$serviceName*" } |
        Format-List TimeGenerated, EntryType, Message
}

# ── Summary ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host "  Installation Complete" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host "  Service account : $fullAccount"
Write-Host "  In Admin group  : Yes (required for WMI hardware events)"
Write-Host "  WMI namespaces  : root + root\cimv2 (persists across reboots)"
Write-Host "  Service status  : $($svc.Status)"
Write-Host "  Install dir     : $installDir"
Write-Host "  Logs            : $LogPath"
Write-Host "  SignalR URL     : $ServerUrl/monitor"
Write-Host ""
Write-Host "  Manage: services.msc -> '$displayName'" -ForegroundColor Yellow
Write-Host ""
Write-Host "  To reinstall with a different server:" -ForegroundColor Gray
Write-Host "  .\install-service.ps1 -ServerUrl 'http://192.168.1.100:5000'" -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""