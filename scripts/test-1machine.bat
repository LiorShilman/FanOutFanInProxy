@echo off
setlocal

REM ================================================================
REM  FOFI 1-Machine Full Loopback Test
REM  Starts: FOFI GROUND + FOFI AIR + both Dashboards + test traffic
REM
REM  Usage:
REM    Double-click  OR  test-1machine.bat
REM    test-1machine.bat Release        (Release build)
REM    test-1machine.bat Debug 500      (Debug, 500ms interval)
REM
REM  Ctrl+C to stop all processes.
REM ================================================================

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Debug

set INTERVAL=%~2
if "%INTERVAL%"=="" set INTERVAL=1000

set BASE=%~dp0..
set SVC=%BASE%\src\TcpProxy.Service\bin\%CONFIG%\net472\TcpProxy.Service.exe
set DSH=%BASE%\src\TcpProxy.Dashboard\bin\%CONFIG%\net472\TcpProxy.Dashboard.exe
set AIR_CFG=%BASE%\config\fofi-air\proxy-fofi-air-1machine.yaml
set GND_CFG=%BASE%\config\fofi-ground\proxy-fofi-ground-1machine.yaml

if not exist "%SVC%" (
    echo.
    echo  ERROR: exe not found:
    echo  %SVC%
    echo.
    echo  Build the solution first ^(Ctrl+Shift+B in VS2022^)
    echo.
    pause
    exit /b 1
)

echo.
echo  ==========================================
echo   FOFI 1-Machine Simulation  [%CONFIG%]
echo  ==========================================
echo.

echo  [1/4] FOFI GROUND proxy...
start "FOFI GROUND" "%SVC%" --config "%GND_CFG%"
timeout /t 1 /nobreak > nul

echo  [2/4] FOFI AIR proxy...
start "FOFI AIR" "%SVC%" --config "%AIR_CFG%"
timeout /t 2 /nobreak > nul

if exist "%DSH%" (
    echo  [3/4] Dashboards...
    start "Dashboard FOFI AIR   :19001" "%DSH%"
    start "Dashboard FOFI GROUND :19011" "%DSH%" --port 19011
    timeout /t 1 /nobreak > nul
) else (
    echo  [3/4] Dashboard exe not found, skipping.
)

echo  [4/4] Test traffic ^(Ctrl+C to stop all^)...
echo.

powershell -ExecutionPolicy Bypass -NoProfile ^
    -File "%~dp0test-1machine.ps1" ^
    -Config "%CONFIG%" ^
    -IntervalMs %INTERVAL% ^
    -TrafficOnly

echo.
echo  Simulation stopped.
echo  Note: FOFI AIR and FOFI GROUND windows may need to be closed manually.
echo.
pause
