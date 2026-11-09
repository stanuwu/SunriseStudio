@echo off
setlocal
title Solforge Editor + MCP Build Pipeline

echo =====================================================================
echo  Starting Solforge Editor + MCP Build Pipeline...
echo =====================================================================
echo.

set "SCRIPT_PATH=%~dp0Build-Solforge.ps1"

where pwsh >nul 2>nul
if %ERRORLEVEL% EQU 0 goto use_pwsh

where powershell >nul 2>nul
if %ERRORLEVEL% EQU 0 goto use_powershell

echo [ERROR] Neither pwsh nor powershell was found in your PATH.
echo Please ensure PowerShell is installed.
echo.
pause
exit /b 1

:use_pwsh
set "PS_EXE=pwsh"
goto run_script

:use_powershell
set "PS_EXE=powershell"
goto run_script

:run_script
echo Running build pipeline with %PS_EXE%...
echo.

"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%"
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

echo.
if %BUILD_EXIT_CODE% EQU 0 (
    echo =====================================================================
    echo  SUCCESS: Solforge Editor and MCP have been built and published!
    echo =====================================================================
) else (
    echo =====================================================================
    echo  FAILURE: Build process exited with error code %BUILD_EXIT_CODE%.
    echo =====================================================================
)
echo.
echo Press any key to close this window . . .
pause >nul
exit /b %BUILD_EXIT_CODE%
