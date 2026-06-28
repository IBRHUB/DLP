@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_setup.ps1" %*
exit /b %ERRORLEVEL%
