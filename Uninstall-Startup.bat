@echo off
cd /d "%~dp0"
echo Removing AppLocker from Windows startup...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CustomAppLocker" /f
if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] AppLocker removed from Windows startup.
) else (
    echo.
    echo AppLocker was not found in Windows startup entries.
)
pause
