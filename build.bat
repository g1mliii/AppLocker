@echo off
cd /d "%~dp0"
echo Compiling AppLocker.cs...
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /optimize+ /r:System.Windows.Forms.dll,System.Drawing.dll,System.Web.Extensions.dll /out:"%~dp0AppLocker.exe" "%~dp0AppLocker.cs"
if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] AppLocker.exe built successfully.
) else (
    echo.
    echo [ERROR] Build failed.
)
pause
