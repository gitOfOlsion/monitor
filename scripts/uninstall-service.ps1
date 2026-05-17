# Run as Administrator
$serviceName = "PeripheralMonitorAgent"

Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
sc.exe delete $serviceName
Write-Host "✅ Service uninstalled" -ForegroundColor Green

# Optional: Remove files
Remove-Item "C:\Program Files\PeripheralMonitor" -Recurse -Force -ErrorAction SilentlyContinue