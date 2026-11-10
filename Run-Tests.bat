@echo off
cd /d "%~dp0"
echo Compiling Test Suite...
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /main:AppLocker.Tests.TestSuite /r:System.Windows.Forms.dll,System.Drawing.dll,System.Web.Extensions.dll /out:"%~dp0Tests\TestSuite.exe" "%~dp0AppLocker.cs" "%~dp0Tests\TestSuite.cs"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Test suite compilation failed.
    pause
    exit /b %ERRORLEVEL%
)

echo.
"%~dp0Tests\TestSuite.exe"
pause
