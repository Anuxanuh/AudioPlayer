@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "NOVEL_PWSH=pwsh.exe"
where pwsh.exe >nul 2>nul
if not errorlevel 1 goto run
set "NOVEL_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%NOVEL_PWSH%" goto run
echo PowerShell 7 is required. Please install it and retry.
set "NOVEL_EXIT=1"
goto finish
:run
"%NOVEL_PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "NOVEL_EXIT=%ERRORLEVEL%"
:finish
if not "%NOVEL_EXIT%"=="0" echo BUILD FAILED. See the error above.
if /i not "%~1"=="--no-pause" pause
exit /b %NOVEL_EXIT%
