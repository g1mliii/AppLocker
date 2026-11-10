@echo off
setlocal enabledelayedexpansion
title AppLocker Launcher
cls
echo ===================================================
echo               AppLocker Launcher
echo ===================================================
echo.

tasklist /FI "IMAGENAME eq AppLocker.exe" 2>nul | find /i "AppLocker.exe" >nul
if %ERRORLEVEL% equ 0 (
    echo [STATUS] AppLocker is ALREADY RUNNING actively in the background!
) else (
    echo Starting AppLocker in background...
    start "" "%~dp0AppLocker.exe"
    ping -n 2 127.0.0.1 >nul
    echo [STATUS] AppLocker has been started successfully!
)

echo.
echo ---------------------------------------------------
echo NOTE: AppLocker runs silently as a background service.
echo It is designed to run without keeping a command window open.
echo.
echo You will find the golden padlock icon in your SYSTEM TRAY
echo (bottom-right corner next to the clock; click the
echo  '^' arrow if the icon is hidden).
echo.
echo Try opening your locked app (e.g. Brave) to see it in action!
echo ---------------------------------------------------
echo.
echo Press any key to close this launcher window...
pause >nul
