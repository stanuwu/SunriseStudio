@echo off
rem Double-click: full Solforge test build into 09-solforge\publish-test-<stamp> (see build-solforge.ps1 for options).
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-solforge.ps1" %*
echo.
pause
