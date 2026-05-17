Write-Host "=== SignalR Endpoint Test ===" -ForegroundColor Cyan

# 1. Check server process
Write-Host "`n[1] Server Process:" -ForegroundColor Yellow
Get-Process -Name "PeripheralMonitor.Server" -ErrorAction SilentlyContinue | Select-Object Id, StartTime

# 2. Check listening port
Write-Host "`n[2] Port 5000 Listener:" -ForegroundColor Yellow
netstat -ano | findstr :5000

# 3. Test negotiate endpoint
Write-Host "`n[3] Testing /monitor/negotiate:" -ForegroundColor Yellow
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/monitor/negotiate?negotiateVersion=1" -Method Post -ContentType "application/json" -UseBasicParsing
    Write-Host "✅ SUCCESS: $($response.StatusCode) - $($response.Content.Substring(0, [Math]::Min(100, $response.Content.Length)))..." -ForegroundColor Green
} catch {
    Write-Host "❌ FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

# 4. Check Agent config
Write-Host "`n[4] Agent Config:" -ForegroundColor Yellow
$config = "C:\Program Files\PeripheralMonitor\Agent\appsettings.local.json"
if (Test-Path $config) {
    Get-Content $config | ConvertFrom-Json | Select-Object SignalRUrl, JwtToken
} else {
    Write-Host "⚠️ appsettings.local.json not found - Agent will use fallback URL" -ForegroundColor Yellow
}

Write-Host "`n=== End Test ===" -ForegroundColor Cyan