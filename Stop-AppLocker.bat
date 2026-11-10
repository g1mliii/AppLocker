@echo off
cd /d "%~dp0"
echo Stopping AppLocker...
"%~dp0AppLocker.exe" --stop
taskkill /F /IM AppLocker.exe >nul 2>&1
echo AppLocker stopped.
