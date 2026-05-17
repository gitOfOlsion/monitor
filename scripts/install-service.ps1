# Peripheral Monitor Agent - Windows Service Installer
# Run as Administrator

param(
    [string]$ServerUrl = "http://localhost:5000",
    [string]$LogPath = "C:\ProgramData\PeripheralMonitor\Logs",
    [string]$ServiceUser = "",      # e.g. ".\YourUsername" or "DOMAIN\Username"
    [String]$ServicePassword = ""   # your Windows password
)

$serviceName    = "PeripheralMonitorAgent"
$displayName    = "Peripheral Monitor Agent"
$exePath        = "C:\Program Files\PeripheralMonitor\Agent\PeripheralMonitor.Agent.exe"
$installDir     = "C:\Program Files\PeripheralMonitor\Agent"
$configPath     = "$installDir\appsettings.json"

# ── 1. Stop & remove existing service ───────────────────────────────────
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "⏹ Stopping existing service..."
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName
    Start-Sleep -Seconds 2
}

# ── 2. Create directories ────────────────────────────────────────────────
New-Item -Path $installDir -ItemType Directory -Force | Out-Null
New-Item -Path $LogPath    -ItemType Directory -Force | Out-Null

# ── 3. Copy published files ──────────────────────────────────────────────
$source = "$PSScriptRoot\..\publish\agent\"
if (Test-Path $source) {
    Copy-Item -Path "$source\*" -Destination $installDir -Recurse -Force
    Write-Host "✅ Files copied"
} else {
    Write-Warning "Publish output not found at: $source"
    Write-Warning "Run: dotnet publish Agent/Agent.csproj -c Release -r win-x64 --self-contained true"
    exit 1
}

# ── 4. Write appsettings.json ────────────────────────────────────────────
@{
    SignalRUrl = "$ServerUrl/monitor"
    Logging    = @{
        LogLevel = @{
            Default   = "Information"
            Microsoft = "Warning"
        }
    }
} | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
Write-Host "✅ Config written"

# ── 5. Write empty token placeholder ────────────────────────────────────
@{ JwtToken = "" } | ConvertTo-Json | 
    Set-Content -Path "$installDir\appsettings.local.json" -Encoding UTF8

# ── 6. Grant permissions on folders ─────────────────────────────────────
foreach ($path in @($installDir, $LogPath)) {
       $acl = Get-Acl $path

    # SYSTEM = S-1-5-18 (works on ALL Windows languages)
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-20")
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $systemSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))

    # Administrators = S-1-5-32-544 (works on ALL Windows languages)
    $adminSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $adminSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))

    # Current user — use env vars directly, no translation needed
    if ($ServiceUser -ne "") {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $ServiceUser, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
    }

    Set-Acl $path $acl
}
Write-Host "✅ Permissions set"

# ── 7. Create service ────────────────────────────────────────────────────
if ($ServiceUser -ne "" -and $ServicePassword -ne "") {

    # ✅ Normalize username — sc.exe needs COMPUTERNAME\Username format
    $normalizedUser = if ($ServiceUser -match "\\") {
        $ServiceUser  # already has domain\user or .\user
    } else {
        "$env:COMPUTERNAME\$ServiceUser"  # add computer name prefix
    }

    Write-Host "🔧 Installing service as: $normalizedUser" -ForegroundColor Cyan

    $result = & sc.exe create $serviceName `
        binPath= "`"$exePath`"" `
        start= auto `
        DisplayName= $displayName `
        obj= $normalizedUser `
        password= $ServicePassword

    Write-Host $result

    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Service creation failed. Trying LOCAL SYSTEM fallback..." -ForegroundColor Yellow
        & sc.exe create $serviceName `
            binPath= "`"$exePath`"" `
            start= auto `
            DisplayName= $displayName
    }
} else {
    Write-Host "🔧 Installing service as LOCAL SYSTEM" -ForegroundColor Cyan
    & sc.exe create $serviceName `
        binPath= "`"$exePath`"" `
        start= auto `
        DisplayName= $displayName
}

sc.exe description $serviceName "Monitors USB/HID devices and reports to Peripheral Monitor Server"
sc.exe failure $serviceName reset= 60 actions= restart/5000/restart/10000/restart/30000

# ── 8. Start the service ─────────────────────────────────────────────────
Start-Service $serviceName
Start-Sleep -Seconds 3

$svc = Get-Service $serviceName
Write-Host ""
Write-Host "Service status: $($svc.Status)" -ForegroundColor $(if ($svc.Status -eq 'Running') {'Green'} else {'Red'})
Write-Host "📁 Logs:    $LogPath"
Write-Host "📁 Install: $installDir"
Write-Host "🔧 Manage:  services.msc → '$displayName'"
Write-Host ""
Write-Host "💡 If WMI events don't work, run with your Windows credentials:" -ForegroundColor Yellow
Write-Host "   .\install.ps1 -ServiceUser '.\YourUsername' -ServicePassword 'YourPassword'" -ForegroundColor Yellow