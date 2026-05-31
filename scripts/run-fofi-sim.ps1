#Requires -Version 5.1
<#
.SYNOPSIS
    FOFI AIR / FOFI GROUND simulation runner.

.PARAMETER Mode
    Sim        - Direct link: FOFI AIR connects straight to FOFI GROUND (default)
    WithLink   - Via local link-sim proxy on :6000 (LOS stand-in)
    FofiAir    - FOFI AIR + ACCC only  (FOFI GROUND on another machine)
    FofiGround - FOFI GROUND + GCCC only (FOFI AIR on another machine)

.PARAMETER NoBuild
    Skip dotnet build.

.PARAMETER NoDashboard
    Skip launching dashboards.

.EXAMPLE
    .\run-fofi-sim.ps1
    .\run-fofi-sim.ps1 -Mode WithLink
    .\run-fofi-sim.ps1 -Mode FofiAir
    .\run-fofi-sim.ps1 -NoBuild -NoDashboard
#>
param(
    [ValidateSet("Sim","WithLink","FofiAir","FofiGround")]
    [string]$Mode = "Sim",
    [switch]$NoBuild,
    [switch]$NoDashboard
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$ConfigSim = Join-Path $RepoRoot "config\sim"
$ConfigAir = Join-Path $RepoRoot "config\fofi-air"
$ConfigGnd = Join-Path $RepoRoot "config\fofi-ground"

# --- Executables -------------------------------------------------------------
function Resolve-Exe([string]$rel) {
    $p = Join-Path $RepoRoot $rel
    if (-not (Test-Path $p)) { throw "Not found: $p  (run dotnet build first)" }
    return $p
}

$ProxyExe      = Resolve-Exe "src\TcpProxy.Service\bin\Debug\net472\TcpProxy.Service.exe"
$DashboardExe  = Join-Path $RepoRoot "src\TcpProxy.Dashboard\bin\Debug\net472\TcpProxy.Dashboard.exe"
$UpstreamExe   = Resolve-Exe "tools\UpstreamSimulator\bin\Debug\net472\UpstreamSimulator.exe"
$DownstreamExe = Resolve-Exe "tools\DownstreamSimulator\bin\Debug\net472\DownstreamSimulator.exe"

# --- Config paths ------------------------------------------------------------
$FofiAirSim      = Join-Path $ConfigAir "proxy-fofi-air-sim.yaml"
$FofiAirWithLink = Join-Path $ConfigAir "proxy-fofi-air-withlink.yaml"
$FofiGndSim      = Join-Path $ConfigGnd "proxy-fofi-ground-sim.yaml"
$LinkSimCfg      = Join-Path $ConfigSim "proxy-link-sim.yaml"
$GcccSimCfg      = Join-Path $ConfigSim "gccc-sim.yaml"

# --- Kill any leftover processes from previous runs -------------------------
$staleNames = "TcpProxy.Service","DownstreamSimulator","UpstreamSimulator","TcpProxy.Dashboard"
$stale = Get-Process -Name $staleNames -ErrorAction SilentlyContinue
if ($stale) {
    Write-Host "Stopping leftover processes from previous run..." -ForegroundColor DarkYellow
    $stale | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

# --- Build -------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "Building solution..." -ForegroundColor Cyan
    & dotnet build (Join-Path $RepoRoot "TcpProxy.sln") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    Write-Host "Build OK." -ForegroundColor Green
    Write-Host ""
}

# --- Helpers -----------------------------------------------------------------
function Start-Win([string]$Title, [string]$Exe, [string]$ConfigFile) {
    $cmd     = '$host.UI.RawUI.WindowTitle = "' + $Title + '"; ' +
               '& "' + $Exe + '" --config "' + $ConfigFile + '"'
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($cmd))
    Start-Process powershell.exe -ArgumentList "-NoExit", "-EncodedCommand", $encoded
}

function Start-Dashboard([string]$Title, [int]$StatusPort) {
    if ($NoDashboard) { return }
    if (-not (Test-Path $DashboardExe)) {
        Write-Host "  Dashboard exe not found - build first." -ForegroundColor Yellow
        return
    }
    $cmd     = '$host.UI.RawUI.WindowTitle = "' + $Title + '"; ' +
               '& "' + $DashboardExe + '" --host 127.0.0.1 --port ' + $StatusPort
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($cmd))
    Start-Process powershell.exe -ArgumentList "-NoExit", "-EncodedCommand", $encoded
}

# =============================================================================
Write-Host "================================================" -ForegroundColor Yellow
Write-Host "  FOFI AIR / FOFI GROUND -- SIM RUNNER" -ForegroundColor Yellow
Write-Host "  Mode: $Mode" -ForegroundColor Yellow
Write-Host "================================================" -ForegroundColor Yellow
Write-Host ""

switch ($Mode) {

    # -------------------------------------------------------------------------
    # SIM -- Direct connection (no range hardware needed)
    # -------------------------------------------------------------------------
    "Sim" {
        Write-Host "Topology:" -ForegroundColor Cyan
        Write-Host "  ACCC sim [:5100] -> FOFI AIR -> [:5200] -> FOFI GROUND -> [:5300] -> GCCC sim" -ForegroundColor White
        Write-Host ""

        Write-Host "[1/6] GCCC Simulator    (server :5300)" -ForegroundColor Cyan
        Start-Win "GCCC-Simulator" $UpstreamExe $GcccSimCfg
        Start-Sleep -Milliseconds 700

        Write-Host "[2/6] FOFI GROUND Proxy (server :5200  client->:5300)" -ForegroundColor Cyan
        Start-Win "FOFI-GROUND-Proxy" $ProxyExe $FofiGndSim
        Start-Sleep -Milliseconds 1000

        Write-Host "[3/6] FOFI GROUND Dashboard (:19011)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI GROUND" 19011
        Start-Sleep -Milliseconds 400

        Write-Host "[4/6] FOFI AIR Proxy    (server :5100  client->:5200)" -ForegroundColor Cyan
        Start-Win "FOFI-AIR-Proxy" $ProxyExe $FofiAirSim
        Start-Sleep -Milliseconds 1000

        Write-Host "[5/6] FOFI AIR Dashboard (:19001)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI AIR" 19001
        Start-Sleep -Milliseconds 400

        Write-Host "[6/6] ACCC Simulator    (generator->:5100)" -ForegroundColor Cyan
        Start-Win "ACCC-Simulator" $DownstreamExe $FofiAirSim

        Write-Host ""
        Write-Host "All 6 components started." -ForegroundColor Green
        Write-Host "  ACCC sim -> FOFI AIR (ds:5100 us->5200) -> FOFI GROUND (ds:5200 us->5300) -> GCCC sim" -ForegroundColor White
        Write-Host "  Dashboard FOFI AIR    :19001 / cmd :19002" -ForegroundColor Gray
        Write-Host "  Dashboard FOFI GROUND :19011 / cmd :19012" -ForegroundColor Gray
        Write-Host ""
        Write-Host "  > UPGRADE TO LOS:       edit proxy-fofi-air-sim.yaml  upstream.host=10.10.2.10  port=6000" -ForegroundColor Yellow
        Write-Host "  > UPGRADE TO SUPERNOVA: edit proxy-fofi-air-sim.yaml  upstream.host=10.10.3.10  port=6000" -ForegroundColor Yellow
    }

    # -------------------------------------------------------------------------
    # WITHLINK -- FOFI AIR -> local link-sim -> FOFI GROUND
    # -------------------------------------------------------------------------
    "WithLink" {
        Write-Host "Topology:" -ForegroundColor Cyan
        Write-Host "  ACCC sim -> FOFI AIR [:5100->:6000] -> link-sim [:6000->:5200] -> FOFI GROUND [:5200->:5300] -> GCCC sim" -ForegroundColor White
        Write-Host ""

        Write-Host "[1/7] GCCC Simulator    (server :5300)" -ForegroundColor Cyan
        Start-Win "GCCC-Simulator" $UpstreamExe $GcccSimCfg
        Start-Sleep -Milliseconds 700

        Write-Host "[2/7] FOFI GROUND Proxy (server :5200  client->:5300)" -ForegroundColor Cyan
        Start-Win "FOFI-GROUND-Proxy" $ProxyExe $FofiGndSim
        Start-Sleep -Milliseconds 1000

        Write-Host "[3/7] FOFI GROUND Dashboard (:19011)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI GROUND" 19011
        Start-Sleep -Milliseconds 400

        Write-Host "[4/7] Link Sim Proxy    (server :6000  client->:5200)  [LOS stand-in]" -ForegroundColor Cyan
        Start-Win "Link-Sim-Proxy" $ProxyExe $LinkSimCfg
        Start-Sleep -Milliseconds 800

        Write-Host "[5/7] FOFI AIR Proxy    (server :5100  client->:6000)" -ForegroundColor Cyan
        Start-Win "FOFI-AIR-Proxy" $ProxyExe $FofiAirWithLink
        Start-Sleep -Milliseconds 1000

        Write-Host "[6/7] FOFI AIR Dashboard (:19001)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI AIR" 19001
        Start-Sleep -Milliseconds 400

        Write-Host "[7/7] ACCC Simulator    (generator->:5100)" -ForegroundColor Cyan
        Start-Win "ACCC-Simulator" $DownstreamExe $FofiAirWithLink

        Write-Host ""
        Write-Host "All 7 components started." -ForegroundColor Green
        Write-Host "  ACCC sim -> FOFI AIR -> link-sim (LOS stand-in :6000) -> FOFI GROUND -> GCCC sim" -ForegroundColor White
        Write-Host "  Dashboard FOFI AIR    :19001 / cmd :19002" -ForegroundColor Gray
        Write-Host "  Dashboard FOFI GROUND :19011 / cmd :19012" -ForegroundColor Gray
        Write-Host ""
        Write-Host "  > ADD REAL LOS: stop Link-Sim window, edit proxy-fofi-air-withlink.yaml upstream.host=10.10.2.10" -ForegroundColor Yellow
    }

    # -------------------------------------------------------------------------
    # FOFIAIR -- only FOFI AIR side
    # -------------------------------------------------------------------------
    "FofiAir" {
        Write-Host "Starting FOFI AIR side only." -ForegroundColor Cyan
        Write-Host "FOFI GROUND must be running on another machine (or in FofiGround mode)." -ForegroundColor Yellow
        Write-Host ""

        Write-Host "[1/3] FOFI AIR Proxy    (server :5100  client per config)" -ForegroundColor Cyan
        Start-Win "FOFI-AIR-Proxy" $ProxyExe $FofiAirSim
        Start-Sleep -Milliseconds 1000

        Write-Host "[2/3] FOFI AIR Dashboard (:19001)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI AIR" 19001
        Start-Sleep -Milliseconds 400

        Write-Host "[3/3] ACCC Simulator    (generator->:5100)" -ForegroundColor Cyan
        Start-Win "ACCC-Simulator" $DownstreamExe $FofiAirSim

        Write-Host ""
        Write-Host "FOFI AIR + ACCC started." -ForegroundColor Green
        Write-Host "  Edit proxy-fofi-air-sim.yaml upstream.host/port to reach FOFI GROUND." -ForegroundColor Yellow
    }

    # -------------------------------------------------------------------------
    # FOFIGROUND -- only FOFI GROUND side
    # -------------------------------------------------------------------------
    "FofiGround" {
        Write-Host "Starting FOFI GROUND side only." -ForegroundColor Cyan
        Write-Host "FOFI AIR must be running on another machine (or in FofiAir mode)." -ForegroundColor Yellow
        Write-Host ""

        Write-Host "[1/3] GCCC Simulator    (server :5300)" -ForegroundColor Cyan
        Start-Win "GCCC-Simulator" $UpstreamExe $GcccSimCfg
        Start-Sleep -Milliseconds 700

        Write-Host "[2/3] FOFI GROUND Proxy (server :5200  client->:5300)" -ForegroundColor Cyan
        Start-Win "FOFI-GROUND-Proxy" $ProxyExe $FofiGndSim
        Start-Sleep -Milliseconds 1000

        Write-Host "[3/3] FOFI GROUND Dashboard (:19011)" -ForegroundColor Cyan
        Start-Dashboard "Dashboard - FOFI GROUND" 19011

        Write-Host ""
        Write-Host "FOFI GROUND + GCCC started." -ForegroundColor Green
        Write-Host "  FOFI GROUND listens on :5200 for FOFI AIR (or range link) to connect." -ForegroundColor Yellow
    }
}

# --- Teardown ----------------------------------------------------------------
Write-Host ""
Write-Host "Press any key to STOP all components..." -ForegroundColor Yellow
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

Write-Host "Stopping..." -ForegroundColor Red
$names = "TcpProxy.Service","DownstreamSimulator","UpstreamSimulator","TcpProxy.Dashboard"
Get-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Done." -ForegroundColor Green
