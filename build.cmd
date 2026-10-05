@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "BUILD_PWSH=pwsh.exe"
where pwsh.exe >nul 2>nul
if not errorlevel 1 goto run
set "BUILD_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%BUILD_PWSH%" goto run
echo PowerShell 7 is required. Install it, then run this file again:
echo https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows
set "BUILD_EXIT=1"
goto finish
:run
"%BUILD_PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "BUILD_EXIT=%ERRORLEVEL%"
:finish
echo.
if not "%BUILD_EXIT%"=="0" echo BUILD FAILED. See the error and build log above.
if "%~1"=="" pause
exit /b %BUILD_EXIT%
