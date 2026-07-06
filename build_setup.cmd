@echo off
:START
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_setup.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    goto START
)

echo.
echo Installing DLP...
"%~dp0dist\installer\DLP_Setup.exe" /SILENT /CLOSEAPPLICATIONS /NORESTART
if errorlevel 1 (
    echo.
    echo Install failed.
)

echo.
pause
goto START
