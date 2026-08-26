@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_setup.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

echo.
echo Installing DLP...
"%~dp0dist\installer\DLP_Setup.exe" /SILENT /CLOSEAPPLICATIONS /NORESTART
if errorlevel 1 (
    echo.
    echo Install failed.
    exit /b 1
)

echo.
echo Build and install completed.
exit /b 0
