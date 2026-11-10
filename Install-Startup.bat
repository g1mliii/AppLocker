@echo off
cd /d "%~dp0"
echo Registering AppLocker to start automatically on Windows login...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CustomAppLocker" /t REG_SZ /d "\"%~dp0AppLocker.exe\"" /f
if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] AppLocker is now set to run automatically on Windows login.
) else (
    echo.
    echo [ERROR] Failed to add startup entry.
)
pause
