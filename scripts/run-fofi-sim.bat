@echo off
setlocal

echo.
echo  ================================================
echo   FOFI AIR / FOFI GROUND  --  SIM RUNNER
echo  ================================================
echo.
echo   [1]  Sim        - Direct link  (FOFI AIR to FOFI GROUND, all localhost)
echo   [2]  WithLink   - Via link-sim (LOS stand-in on :6000)
echo   [3]  FofiAir    - FOFI AIR + ACCC only   (FOFI GROUND on another machine)
echo   [4]  FofiGround - FOFI GROUND + GCCC only (FOFI AIR  on another machine)
echo.
echo   [B]  Build + Sim    (force rebuild before starting)
echo   [N]  Sim  - No Dashboard
echo.

choice /c 1234BN /n /m "  Choose [1-4 / B / N]: "

if errorlevel 6 goto SIM_NODASH
if errorlevel 5 goto BUILD_SIM
if errorlevel 4 goto FOFIGROUND
if errorlevel 3 goto FOFIAIR
if errorlevel 2 goto WITHLINK
if errorlevel 1 goto SIM

:SIM
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode Sim -NoBuild
goto END

:WITHLINK
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode WithLink -NoBuild
goto END

:FOFIAIR
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode FofiAir -NoBuild
goto END

:FOFIGROUND
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode FofiGround -NoBuild
goto END

:BUILD_SIM
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode Sim
goto END

:SIM_NODASH
powershell.exe -ExecutionPolicy Bypass -File "%~dp0run-fofi-sim.ps1" -Mode Sim -NoBuild -NoDashboard
goto END

:END
endlocal
