@echo off
title Valheim Patcher - Patch v9 + profiler
cd /d "%~dp0"
tasklist /FI "IMAGENAME eq valheim.exe" 2>nul | find /I "valheim.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Valheim is running. Close the game and run this again.
    pause
    exit /b 1
)
"%~dp0ValheimPatcher.exe" v9 profile %*
if errorlevel 1 goto fail
"%~dp0ValheimPatcher.exe" verify %*
if errorlevel 1 goto fail
echo.
echo Done. Play as usual, then send native_profile.log from
echo %USERPROFILE%\AppData\LocalLow\IronGate\Valheim
echo To remove the profiler run "1. Patch v9 (recommended).cmd" again.
pause
exit /b 0
:fail
echo.
echo [FAILED] See the messages above.
pause
exit /b 1
