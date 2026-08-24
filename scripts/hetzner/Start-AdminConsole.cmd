@echo off
REM Open the OWS admin console on the Hetzner dev server: starts it, tunnels to it, opens
REM the browser, and stops it again when you close this window with Ctrl+C.
REM
REM Double-click for a normal session. Any arguments are passed through to the PowerShell
REM script, so from a prompt you can also run:
REM   Start-AdminConsole.cmd -Update        (pull origin/main and rebuild on the server)
REM   Start-AdminConsole.cmd -KeepRunning   (leave the console running server-side)
REM   Start-AdminConsole.cmd -Port 44411    (if 44410 is busy locally)
setlocal
set SCRIPT_DIR=%~dp0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Start-AdminConsole.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
    echo.
    echo Admin console launcher exited with code %EXITCODE%.
    pause
)
endlocal & exit /b %EXITCODE%
