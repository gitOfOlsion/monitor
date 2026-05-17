# 1. Check exact computer name and username
Write-Host "COMPUTERNAME: $env:COMPUTERNAME"
Write-Host "USERNAME: $env:USERNAME"

# 2. Create service — paste your password
$user = "$env:COMPUTERNAME\$env:USERNAME"
$pass = "Uet@2026@"  # ← replace this

sc.exe create PeripheralMonitorAgent `
    binPath= "`"C:\Program Files\PeripheralMonitor\Agent\PeripheralMonitor.Agent.exe`"" `
    start= auto `
    DisplayName= "Peripheral Monitor Agent" `
    obj= $user `
    password= $pass

# 3. Set description and auto-restart
sc.exe description PeripheralMonitorAgent "Monitors USB/HID devices"
sc.exe failure PeripheralMonitorAgent reset= 60 actions= restart/5000/restart/10000/restart/30000

# 4. Grant folder permissions using icacls (bypasses ACL translation errors)
icacls "C:\Program Files\PeripheralMonitor\Agent" /grant "$env:USERNAME:(OI)(CI)F" /T
icacls "C:\ProgramData\PeripheralMonitor\Logs"    /grant "$env:USERNAME:(OI)(CI)F" /T

# 5. Start service
Start-Service PeripheralMonitorAgent
Start-Sleep -Seconds 3
Get-Service PeripheralMonitorAgent