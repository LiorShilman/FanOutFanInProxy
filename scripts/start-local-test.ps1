#Requires -Version 5.1
param(
    [string]$Config      = "",
    [switch]$NoBuild,
    [switch]$NoDashboard
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$ConfigDir = if ($Config) { $Config } else { Join-Path $RepoRoot "config\local-test" }

function Resolve-Exe([string]$rel) {
    $p = Join-Path $RepoRoot $rel
    if (-not (Test-Path $p)) { throw "Executable not found: $p" }
    return $p
}

$ProxyExe      = Resolve-Exe "src\TcpProxy.Service\bin\Debug\net472\TcpProxy.Service.exe"
$DashboardExe  = Join-Path $RepoRoot "src\TcpProxy.Dashboard\bin\Debug\net472\TcpProxy.Dashboard.exe"
$UpstreamExe   = Resolve-Exe "tools\UpstreamSimulator\bin\Debug\net472\UpstreamSimulator.exe"
$DownstreamExe = Resolve-Exe "tools\DownstreamSimulator\bin\Debug\net472\DownstreamSimulator.exe"
$ProxyConfig   = Join-Path $ConfigDir "proxy.yaml"

if (-not $NoBuild) {
    Write-Host "Building solution..." -ForegroundColor Cyan
    & dotnet build (Join-Path $RepoRoot "TcpProxy.sln") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    Write-Host "Build OK." -ForegroundColor Green
}

function Start-Component([string]$Title, [string]$Exe, [string]$ConfigFile) {
    $script  = '$host.UI.RawUI.WindowTitle = "' + $Title + '"; ' +
               '& "' + $Exe + '" --config "' + $ConfigFile + '"'
    $encoded = [Convert]::ToBase64String(
                   [System.Text.Encoding]::Unicode.GetBytes($script))
    Start-Process powershell.exe -ArgumentList "-NoExit", "-EncodedCommand", $encoded
}

Write-Host ""
Write-Host "=============================" -ForegroundColor Yellow
Write-Host " TCP Proxy  LOCAL TEST MODE " -ForegroundColor Yellow
Write-Host "=============================" -ForegroundColor Yellow
Write-Host "Config: $ProxyConfig"
Write-Host ""

# 1 - DS simulator (reads proxy.yaml: downstreams x channels)
Write-Host "[1/4] DownstreamSimulator" -ForegroundColor Cyan
Start-Component "DownstreamSimulator" $DownstreamExe $ProxyConfig
Start-Sleep -Milliseconds 700

# 2 - Proxy
Write-Host "[2/4] TcpProxy.Service" -ForegroundColor Cyan
Start-Component "TcpProxy.Service" $ProxyExe $ProxyConfig
Start-Sleep -Milliseconds 1200

# 3 - Dashboard
if (-not $NoDashboard -and (Test-Path $DashboardExe)) {
    Write-Host "[3/4] Dashboard  :19001" -ForegroundColor Cyan
    Start-Process $DashboardExe
    Start-Sleep -Milliseconds 500
} else {
    Write-Host "[3/4] Dashboard not found - run dotnet build first." -ForegroundColor Red
}

# 4 - Upstream simulator
Write-Host "[4/4] UpstreamSimulator" -ForegroundColor Cyan
Start-Component "UpstreamSimulator" $UpstreamExe (Join-Path $ConfigDir "upstream-sim.yaml")

Write-Host ""
Write-Host "All components started." -ForegroundColor Green
Write-Host "  Proxy    127.0.0.1:28100 (MC)  :28101 (DATA)" -ForegroundColor White
Write-Host "  Upstream 127.0.0.1:28000 (MC)  :28001 (DATA)" -ForegroundColor White
Write-Host "  Dashboard 127.0.0.1:19001" -ForegroundColor White
Write-Host ""
Write-Host "To add a DS: add entry under 'downstreams:' in proxy.yaml and restart." -ForegroundColor Yellow
Write-Host ""
Write-Host "Press any key to STOP all components..." -ForegroundColor Yellow
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

Write-Host "Stopping..." -ForegroundColor Red
$names = "TcpProxy.Service","DownstreamSimulator","UpstreamSimulator","TcpProxy.Dashboard"
Get-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Done." -ForegroundColor Green
