@echo off
setlocal

REM ============================================================
REM  Restart SensorFarm only - for recovery testing.
REM
REM  Use it to check that the publisher can be restarted while
REM  ControlRoom keeps running, and everything reconnects on its own.
REM
REM  The reverse (restart ControlRoom only) is also a check:
REM  hand-verification scenario 7 - farm and dashboard must reconnect.
REM
REM  NOTE: ASCII on purpose - see the other batch file for why.
REM ============================================================

set "BASE=%~dp0"
set "SENSORFARM="

for /f "delims=" %%F in ('dir /b /s /o-d "%BASE%src\IoTSensorDashboard.SensorFarm\bin\Debug\IoTSensorDashboard.SensorFarm.exe" 2^>nul') do (
    if not defined SENSORFARM set "SENSORFARM=%%F"
)

if not defined SENSORFARM (
    echo.
    echo Executable not found. Build first:
    echo     dotnet build IoTSensorDashboard.sln
    echo.
    pause
    goto end
)

echo [SensorFarm] %SENSORFARM%
start "" "%SENSORFARM%"

:end
endlocal
