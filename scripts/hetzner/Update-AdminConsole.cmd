@echo off
REM First run, and after pushing any admin console change: pulls origin/main on the dev
REM server and rebuilds the console image before opening it. Slower than Start-AdminConsole
REM (it runs npm install and dotnet publish on the box), so use that one day to day.
setlocal
set SCRIPT_DIR=%~dp0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Start-AdminConsole.ps1" -Update %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
    echo.
    echo Admin console update exited with code %EXITCODE%.
    pause
)
endlocal & exit /b %EXITCODE%
