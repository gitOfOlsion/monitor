@echo off
:: ═══════════════════════════════════════════════════════════════
:: Peripheral Monitor Agent — Installer Launcher
:: Right-click this file → "Run as administrator"
:: ═══════════════════════════════════════════════════════════════

:: Check for admin rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

:: Run the PowerShell installer from the same directory as this batch file
cd /d "%~dp0"
echo.
echo ══════════════════════════════════════════════════
echo   Peripheral Monitor Agent — Installing...
echo ══════════════════════════════════════════════════
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"

echo.
echo ══════════════════════════════════════════════════
echo   Done. You can close this window.
echo ══════════════════════════════════════════════════
echo.
pause
