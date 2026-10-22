@echo off
setlocal
rem Build the deploy package for a new station: self-contained FAAM.UI +
rem Horizon.Console + Horizon.ConfigTool, plus the %APPDATA%\Horizon seed
rem (config + products, history.db and dut\ dropped). Wraps publish.ps1.
rem
rem Usage:  build-deploy.bat [output-folder]     default C:\Horizon-deploy
rem
rem The output folder is DELETED and rebuilt on every run. Run on the dev box
rem where the vendor SDKs are installed. See deploy\README.md.

set "OUT=%~1"
if "%OUT%"=="" set "OUT=C:\Horizon-deploy"

rem Prefer PowerShell 7; fall back to the stock Windows PowerShell 5.1.
set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"

echo Building deploy package into %OUT%
echo.
%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Snapshot -CleanHistory -Output "%OUT%"
if errorlevel 1 goto failed

echo.
echo Done. Copy %OUT% to the new machine, then follow
echo "On the TARGET box" in deploy\README.md.
pause
exit /b 0

:failed
echo.
echo DEPLOY BUILD FAILED - see the errors above.
pause
exit /b 1
