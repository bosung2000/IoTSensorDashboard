@echo off
setlocal

REM ============================================================
REM  Start all three apps in order.
REM
REM    ControlRoom -> (5s) -> SensorFarm -> (2s) -> Dashboard
REM
REM  ControlRoom owns the broker, so it must be up first.
REM  Out-of-order start still works (clients retry forever),
REM  it is just delayed by the reconnect backoff.
REM
REM  NOTE: this file is ASCII on purpose.
REM        cmd parses batch files using the current code page,
REM        so non-ASCII comments break the script depending on locale.
REM        Korean guidance lives in README.md and tools\run.ps1.
REM ============================================================

set "BASE=%~dp0"

REM Do NOT hardcode the TFM folder.
REM A stale TFM folder under bin\Debug once made a verification script
REM launch an 8-day-old binary, which looked like "the fix did not apply".
REM Find the most recently built exe instead.

call :find CONTROLROOM "%BASE%src\IoTSensorDashboard.ControlRoom" IoTSensorDashboard.ControlRoom.exe
call :find SENSORFARM  "%BASE%src\IoTSensorDashboard.SensorFarm"  IoTSensorDashboard.SensorFarm.exe
call :find DASHBOARD   "%BASE%src\IoTSensorDashboard.Dashboard"   IoTSensorDashboard.Dashboard.exe

if not defined CONTROLROOM goto notbuilt
if not defined SENSORFARM  goto notbuilt
if not defined DASHBOARD   goto notbuilt

echo [ControlRoom] %CONTROLROOM%
start "" "%CONTROLROOM%"
call :wait 5

echo [SensorFarm]  %SENSORFARM%
start "" "%SENSORFARM%"
call :wait 2

echo [Dashboard]   %DASHBOARD%
start "" "%DASHBOARD%"

echo.
echo All three apps started.
echo Pick a publish rate in SensorFarm to make data flow (default is stopped).
goto end

:find
set "%~1="
for /f "delims=" %%F in ('dir /b /s /o-d "%~2\bin\Debug\%~3" 2^>nul') do (
    if not defined %~1 set "%~1=%%F"
)
goto :eof

REM Use ping instead of timeout.
REM timeout fails immediately when stdin is redirected (CI, scripts).
:wait
set /a "PINGS=%~1+1"
ping -n %PINGS% 127.0.0.1 >nul 2>&1
goto :eof

:notbuilt
echo.
echo Executable not found. Build first:
echo     dotnet build IoTSensorDashboard.sln
echo.
pause

:end
endlocal
