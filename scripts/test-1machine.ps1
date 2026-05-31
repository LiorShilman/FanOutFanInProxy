# test-1machine.ps1
# Single-machine full loopback simulation
#
# Starts: FOFI GROUND + FOFI AIR (both as separate processes)
# Runs:   ACCC sender + GCCC sender + both receivers
#
# Usage:
#   .\test-1machine.ps1
#   .\test-1machine.ps1 -Config Release
#   .\test-1machine.ps1 -Dashboard
#   .\test-1machine.ps1 -IntervalMs 500
#
# Port layout:
#   ACCC send -> 127.0.0.1:5000 -> FOFI AIR
#   FOFI AIR  -> 127.0.0.1:5200 (LOS) or :5202 (SNV) -> FOFI GROUND
#   FOFI GND  -> 127.0.0.1:7000 -> GCCC recv
#
#   GCCC send -> 127.0.0.1:7100 -> FOFI GROUND
#   FOFI GND  -> 127.0.0.1:5210 (LOS) or :5212 (SNV) -> FOFI AIR
#   FOFI AIR  -> 127.0.0.1:5001 -> ACCC recv

param(
    [string]$Config     = "Debug",
    [int]$IntervalMs    = 1000,
    [switch]$Dashboard,
    [switch]$TrafficOnly  # skip proxy startup (used when called from BAT)
)

$ErrorActionPreference = "Stop"

$base   = Split-Path $PSScriptRoot -Parent
$svcExe = Join-Path $base "src\TcpProxy.Service\bin\$Config\net472\TcpProxy.Service.exe"
$dshExe = Join-Path $base "src\TcpProxy.Dashboard\bin\$Config\net472\TcpProxy.Dashboard.exe"
$airCfg = Join-Path $base "config\fofi-air\proxy-fofi-air-1machine.yaml"
$gndCfg = Join-Path $base "config\fofi-ground\proxy-fofi-ground-1machine.yaml"

if (-not (Test-Path $svcExe)) {
    Write-Host "ERROR: exe not found: $svcExe" -ForegroundColor Red
    Write-Host "Build the solution first (Ctrl+Shift+B in VS2022)." -ForegroundColor Red
    exit 1
}

$procGnd = $null
$procAir = $null
$gcccJob = $null
$acccJob = $null
$sender1 = $null
$sender2 = $null

try {
    # -- Start proxies (skipped when called from BAT via -TrafficOnly) ----------

    if (-not $TrafficOnly) {
        Write-Host "Starting FOFI GROUND..." -ForegroundColor Yellow
        $procGnd = Start-Process -FilePath $svcExe `
            -ArgumentList "--config `"$gndCfg`"" `
            -PassThru

        Start-Sleep -Milliseconds 800

        Write-Host "Starting FOFI AIR..." -ForegroundColor Yellow
        $procAir = Start-Process -FilePath $svcExe `
            -ArgumentList "--config `"$airCfg`"" `
            -PassThru

        if ($Dashboard -and (Test-Path $dshExe)) {
            Start-Sleep -Milliseconds 500
            Write-Host "Opening dashboards..." -ForegroundColor Yellow
            Start-Process -FilePath $dshExe
            Start-Process -FilePath $dshExe -ArgumentList "--port 19011"
        }

        Write-Host "Waiting for proxies to initialize..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    } else {
        Write-Host "Traffic-only mode - proxies started externally." -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 500
    }

    # -- Background listeners ---------------------------------------------------

    $gcccJob = Start-Job -ScriptBlock {
        param($port)
        $ep  = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)
        $udp = [System.Net.Sockets.UdpClient]::new($ep)
        $seq = 0
        while ($true) {
            $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $data   = $udp.Receive([ref]$remote)
            $text   = [System.Text.Encoding]::UTF8.GetString($data)
            $seq++
            Write-Output ("[GCCC-RCV #{0}] {1}" -f $seq, $text)
        }
    } -ArgumentList 7000

    $acccJob = Start-Job -ScriptBlock {
        param($port)
        $ep  = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)
        $udp = [System.Net.Sockets.UdpClient]::new($ep)
        $seq = 0
        while ($true) {
            $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $data   = $udp.Receive([ref]$remote)
            $text   = [System.Text.Encoding]::UTF8.GetString($data)
            $seq++
            Write-Output ("[ACCC-RCV #{0}] {1}" -f $seq, $text)
        }
    } -ArgumentList 5001

    # -- Senders ----------------------------------------------------------------

    $sender1    = [System.Net.Sockets.UdpClient]::new()
    $sender2    = [System.Net.Sockets.UdpClient]::new()
    $acccTarget = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse("127.0.0.1"), 5000)
    $gcccTarget = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse("127.0.0.1"), 7100)

    # Wait for background listener jobs to bind their ports.
    # PS background jobs run asynchronously; without this wait the first send
    # arrives at port 5001 before acccJob has called UdpClient.new(), causing
    # the packet to be silently dropped even with SIO_UDP_CONNRESET suppressed.
    Write-Host "Waiting for listeners to bind..." -ForegroundColor DarkGray
    Start-Sleep -Milliseconds 1500

    # -- Header -----------------------------------------------------------------

    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Yellow
    Write-Host " 1-Machine Full Loopback Test"             -ForegroundColor Yellow
    Write-Host "==========================================" -ForegroundColor Yellow
    Write-Host " Forward: ACCC->:5000->AIR->:5200(LOS)->GND->:7000->GCCC" -ForegroundColor White
    Write-Host "          switch LOS/SNV via AIR dashboard :19001"         -ForegroundColor DarkGray
    Write-Host " Reverse: GCCC->:7100->GND->:5210(LOS)->AIR->:5001->ACCC" -ForegroundColor Cyan
    Write-Host "          switch LOS/SNV via GND dashboard :19011"         -ForegroundColor DarkGray
    Write-Host " Interval: $IntervalMs ms   Ctrl+C to stop"                -ForegroundColor White
    Write-Host ""

    $counter = 0

    while ($true) {
        $out = Receive-Job $gcccJob -ErrorAction SilentlyContinue
        if ($out) { Write-Host $out -ForegroundColor Green }
        $out = Receive-Job $acccJob -ErrorAction SilentlyContinue
        if ($out) { Write-Host $out -ForegroundColor Magenta }

        # Detect and report job failures without killing the loop
        if ($gcccJob.State -eq 'Failed') {
            Write-Host "[WARN] gcccJob failed: $($gcccJob.ChildJobs[0].JobStateInfo.Reason.Message)" -ForegroundColor Red
        }
        if ($acccJob.State -eq 'Failed') {
            Write-Host "[WARN] acccJob failed: $($acccJob.ChildJobs[0].JobStateInfo.Reason.Message)" -ForegroundColor Red
        }

        if ($procAir -and $procAir.HasExited) {
            Write-Host "[WARN] FOFI AIR exited (code $($procAir.ExitCode))" -ForegroundColor Red
        }
        if ($procGnd -and $procGnd.HasExited) {
            Write-Host "[WARN] FOFI GROUND exited (code $($procGnd.ExitCode))" -ForegroundColor Red
        }

        $counter++
        $ts = Get-Date -Format "HH:mm:ss.fff"

        $msg   = ("ACCC-PKT-{0}  {1}" -f $counter, $ts)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $null  = $sender1.Send($bytes, $bytes.Length, $acccTarget)
        Write-Host ("[ACCC-SND #{0}] {1}" -f $counter, $msg) -ForegroundColor White

        $msg   = ("GCCC-PKT-{0}  {1}" -f $counter, $ts)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $null  = $sender2.Send($bytes, $bytes.Length, $gcccTarget)
        Write-Host ("[GCCC-SND #{0}] {1}" -f $counter, $msg) -ForegroundColor Cyan

        Start-Sleep -Milliseconds $IntervalMs
    }
}
finally {
    Write-Host ""
    Write-Host "Stopping..." -ForegroundColor Yellow

    if ($sender1) { try { $sender1.Close() } catch {} }
    if ($sender2) { try { $sender2.Close() } catch {} }

    if ($gcccJob) {
        Stop-Job   $gcccJob -ErrorAction SilentlyContinue
        Remove-Job $gcccJob -ErrorAction SilentlyContinue
    }
    if ($acccJob) {
        Stop-Job   $acccJob -ErrorAction SilentlyContinue
        Remove-Job $acccJob -ErrorAction SilentlyContinue
    }

    if ($procAir -and -not $procAir.HasExited) {
        Stop-Process -Id $procAir.Id -Force -ErrorAction SilentlyContinue
        Write-Host "FOFI AIR stopped." -ForegroundColor Yellow
    }
    if ($procGnd -and -not $procGnd.HasExited) {
        Stop-Process -Id $procGnd.Id -Force -ErrorAction SilentlyContinue
        Write-Host "FOFI GROUND stopped." -ForegroundColor Yellow
    }

    Write-Host "Done." -ForegroundColor Yellow
}
