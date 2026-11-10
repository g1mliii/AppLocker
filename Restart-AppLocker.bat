@echo off
setlocal enabledelayedexpansion
title Restarting AppLocker
cls
echo ===================================================
echo               Restarting AppLocker
echo ===================================================
echo.
echo Stopping current AppLocker instance...
taskkill /F /IM AppLocker.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
echo Starting fresh AppLocker instance...
start "" "%~dp0AppLocker.exe"
ping -n 2 127.0.0.1 >nul
echo.
echo [SUCCESS] AppLocker has restarted and is running in the background!
echo Look for the golden padlock icon in your system tray (^ near the clock).
echo.
echo Press any key to close this window...
pause >nul
