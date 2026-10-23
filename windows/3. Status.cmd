@echo off
title Valheim Patcher - Status
cd /d "%~dp0"
tasklist /FI "IMAGENAME eq valheim.exe" 2>nul | find /I "valheim.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Valheim is running. Close the game and run this again.
    pause
    exit /b 1
)
"%~dp0ValheimPatcher.exe" status %*
if errorlevel 1 goto fail
echo.
echo Done.
pause
exit /b 0
:fail
echo.
echo [FAILED] See the messages above.
pause
exit /b 1
