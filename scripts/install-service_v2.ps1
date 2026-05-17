# ============================================================
# Peripheral Monitor Agent - Windows Service Installer
# PowerShell 5.1 - Run as Administrator
# ============================================================
param(
    [string]$ServerUrl       = "http://localhost:5000",
    [string]$LogPath         = "C:\ProgramData\PeripheralMonitor\Logs",
    [string]$ServiceAccount  = "PMAgentSvc",
    [string]$ServicePassword = "PM@Agent2026!Secure!"
)

$serviceName = "PeripheralMonitorAgent"
$displayName = "Peripheral Monitor Agent"
$installDir  = "C:\Program Files\PeripheralMonitor\Agent"
$exePath     = "$installDir\PeripheralMonitor.Agent.exe"
$configPath  = "$installDir\appsettings.json"
$localConfig = "$installDir\appsettings.local.json"
$fullAccount = "$env:COMPUTERNAME\$ServiceAccount"

# Require Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Must be run as Administrator"
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Peripheral Monitor Agent Installer" -ForegroundColor Cyan
Write-Host "  Service Account: $fullAccount" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ── STEP 1: Stop and remove existing service ─────────────────────────────
Write-Host "[ 1/9 ] Checking existing service..." -ForegroundColor Yellow
$existingSvc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "        Old service removed" -ForegroundColor Green
}
else {
    Write-Host "        No existing service found" -ForegroundColor Green
}

# ── STEP 2: Create directories ───────────────────────────────────────────
Write-Host "[ 2/9 ] Creating directories..." -ForegroundColor Yellow
New-Item -Path $installDir -ItemType Directory -Force | Out-Null
New-Item -Path $LogPath    -ItemType Directory -Force | Out-Null
Write-Host "        $installDir" -ForegroundColor Green
Write-Host "        $LogPath"    -ForegroundColor Green

# ── STEP 3: Copy published files ─────────────────────────────────────────
Write-Host "[ 3/9 ] Copying agent files..." -ForegroundColor Yellow
$source = "$PSScriptRoot\..\publish\agent\"
if (Test-Path $source) {
    Copy-Item -Path "$source\*" -Destination $installDir -Recurse -Force
    Write-Host "        Files copied" -ForegroundColor Green
}
else {
    Write-Warning "Publish output not found at: $source"
    Write-Warning "Run: dotnet publish Agent/Agent.csproj -c Release -r win-x64 --self-contained true"
    exit 1
}

# ── STEP 4: Write configuration files ────────────────────────────────────
Write-Host "[ 4/9 ] Writing configuration..." -ForegroundColor Yellow

$config = @{
    SignalRUrl = "$ServerUrl/monitor"
    Logging    = @{
        LogLevel = @{
            Default   = "Information"
            Microsoft = "Warning"
        }
    }
}
$config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8

if (-not (Test-Path $localConfig)) {
    '{"JwtToken":""}' | Set-Content -Path $localConfig -Encoding UTF8
}

Write-Host "        appsettings.json written" -ForegroundColor Green
Write-Host "        SignalRUrl: $ServerUrl/monitor" -ForegroundColor Gray

# ── STEP 5: Create dedicated service account ──────────────────────────────
Write-Host "[ 5/9 ] Creating service account: $ServiceAccount..." -ForegroundColor Yellow

# Check if user exists using ADSI (faster, no net.exe hang)
$computer  = [ADSI]"WinNT://$env:COMPUTERNAME"
$userADSI  = $computer.Children | Where-Object { $_.SchemaClassName -eq "User" -and $_.Name -eq $ServiceAccount }

if ($userADSI) {
    Write-Host "        Account exists - updating password" -ForegroundColor Gray
    $userADSI.SetPassword($ServicePassword)
    $userADSI.SetInfo()
}
else {
    $newUser = $computer.Create("User", $ServiceAccount)
    $newUser.SetPassword($ServicePassword)
    $newUser.Put("Description", "Peripheral Monitor Agent Service Account")
    $newUser.Put("UserFlags", 64 + 65536)  # ADS_UF_PASSWD_CANT_CHANGE + ADS_UF_DONT_EXPIRE_PASSWD
    $newUser.SetInfo()
    Write-Host "        Account created: $fullAccount" -ForegroundColor Green
}

# Add to Administrators using ADSI (language independent via SID S-1-5-32-544)
$adminGroupName = ([System.Security.Principal.SecurityIdentifier]"S-1-5-32-544").Translate([System.Security.Principal.NTAccount]).Value.Split("\")[1]
Write-Host "        Administrators group name: $adminGroupName" -ForegroundColor Gray

$adminGroup = [ADSI]"WinNT://$env:COMPUTERNAME/$adminGroupName,group"
$members    = @($adminGroup.Invoke("Members")) | ForEach-Object { $_.GetType().InvokeMember("Name", "GetProperty", $null, $_, $null) }

if ($members -notcontains $ServiceAccount) {
    $adminGroup.Invoke("Add", "WinNT://$env:COMPUTERNAME/$ServiceAccount")
    Write-Host "        Added to Administrators group" -ForegroundColor Green
}
else {
    Write-Host "        Already in Administrators group" -ForegroundColor Gray
}

# Get SID using .NET
$ntAccount = New-Object System.Security.Principal.NTAccount("$env:COMPUTERNAME\$ServiceAccount")
$svcSid    = $ntAccount.Translate([System.Security.Principal.SecurityIdentifier]).Value
Write-Host "        SID: $svcSid" -ForegroundColor Gray

# Grant Log on as a service
$tempInf = "$env:TEMP\pmagt_svc.inf"
$tempDb  = "$env:TEMP\pmagt_svc.sdb"

$infContent = "[Unicode]`r`nUnicode=yes`r`n[Version]`r`nsignature=`"`$CHICAGO`$`"`r`nRevision=1`r`n[Privilege Rights]`r`nSeServiceLogonRight = *$svcSid`r`n"
[System.IO.File]::WriteAllText($tempInf, $infContent, [System.Text.Encoding]::Unicode)

& secedit /configure /db $tempDb /cfg $tempInf /areas USER_RIGHTS /quiet | Out-Null
Remove-Item $tempInf -ErrorAction SilentlyContinue
Remove-Item $tempDb  -ErrorAction SilentlyContinue
Write-Host "        Log on as a service right granted" -ForegroundColor Green

# ── STEP 6: Set filesystem permissions ───────────────────────────────────
Write-Host "[ 6/9 ] Setting filesystem permissions..." -ForegroundColor Yellow

$permPaths = @($installDir, $LogPath)
foreach ($path in $permPaths) {
    & icacls $path /grant "${ServiceAccount}:(OI)(CI)F" /T /Q 2>&1 | Out-Null
    & icacls $path /grant "*S-1-5-18:(OI)(CI)F"        /T /Q 2>&1 | Out-Null
    & icacls $path /grant "*S-1-5-32-544:(OI)(CI)F"    /T /Q 2>&1 | Out-Null
    Write-Host "        Permissions set: $path" -ForegroundColor Green
}

# ── STEP 7: Configure WMI root\cimv2 security ────────────────────────────
Write-Host "[ 7/9 ] Configuring WMI security..." -ForegroundColor Yellow

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
        $result    = $wmiSec.GetSD([ref]$binarySD)

        if ($result.ReturnValue -ne 0) {
            throw "GetSD returned: $($result.ReturnValue)"
        }

        $converter = New-Object System.Management.ManagementClass("Win32_SecurityDescriptorHelper")
        $sdWin32   = $converter.BinarySDToWin32SD($binarySD)
        $sd        = $sdWin32.Descriptor

        # Remove existing ACE for this account
        $newDacl = @()
        foreach ($ace in $sd.DACL) {
            if ($ace.Trustee.SidString -ne $AccountSid) {
                $newDacl += $ace
            }
        }

        # Create trustee
        $trustee           = ([WMIClass]"Win32_Trustee").CreateInstance()
        $trustee.SidString = $AccountSid
        $trustee.Name      = $AccountName

        # Create ACE
        $newAce            = ([WMIClass]"Win32_ACE").CreateInstance()
        $newAce.AccessMask = $AccessMask
        $newAce.AceFlags   = 0
        $newAce.AceType    = 0
        $newAce.Trustee    = $trustee

        $newDacl   += $newAce
        $sd.DACL    = $newDacl

        $newBinarySD = $converter.Win32SDToBinarySD($sd)
        $setResult   = $wmiSec.SetSD($newBinarySD.BinarySD)

        return ($setResult.ReturnValue -eq 0)
    }
    catch {
        Write-Warning "WMI error on ${Namespace}: $_"
        return $false
    }
}

# Access mask: Execute Methods=1, Provider Write=2, Enable=4, Remote Enable=32, Read Security=262144
$wmiMask = 1 -bor 2 -bor 4 -bor 32 -bor 262144

$namespaces = @("root", "root\cimv2")
foreach ($ns in $namespaces) {
    $ok = Set-WmiNamespaceSecurity -Namespace $ns -AccountSid $svcSid -AccountName $ServiceAccount -AccessMask $wmiMask
    if ($ok) {
        Write-Host "        WMI $ns - permissions set" -ForegroundColor Green
    }
    else {
        Write-Warning "Failed to set WMI permissions on: $ns"
    }
}

# ── STEP 8: Install the Windows Service ──────────────────────────────────
Write-Host "[ 8/9 ] Installing Windows Service..." -ForegroundColor Yellow

& sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= $displayName obj= $fullAccount password= $ServicePassword

if ($LASTEXITCODE -ne 0) {
    Write-Error "Service creation failed with exit code $LASTEXITCODE"
    exit 1
}

& sc.exe description $serviceName "Monitors USB/HID peripheral devices and reports to Peripheral Monitor Server"
& sc.exe failure     $serviceName reset= 60 actions= restart/5000/restart/10000/restart/30000

Write-Host "        Service installed as: $fullAccount" -ForegroundColor Green

# ── STEP 9: Start the service ─────────────────────────────────────────────
Write-Host "[ 9/9 ] Starting service..." -ForegroundColor Yellow

Start-Service $serviceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4

$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-Host "        Status: RUNNING" -ForegroundColor Green
}
else {
    Write-Host "        Status: $($svc.Status)" -ForegroundColor Red
    Write-Host "        Check Event Viewer -> Windows Logs -> Application" -ForegroundColor Yellow
}

# ── Summary ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Installation Complete" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Account  : $fullAccount"
Write-Host "  Status   : $($svc.Status)"
Write-Host "  Exe      : $exePath"
Write-Host "  Config   : $configPath"
Write-Host "  Logs     : $LogPath"
Write-Host "  SignalR  : $ServerUrl/monitor"
Write-Host "  WMI      : root + root\cimv2 configured (persists reboots)"
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""