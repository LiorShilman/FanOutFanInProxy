# test-3machine-machineA.ps1
# Machine A: ACCC sender + GCCC sender + receivers on both sides
#
# Usage:
#   .\test-3machine-machineA.ps1 -ProxyAirIp 192.168.1.200 -ProxyGroundIp 192.168.1.206
#
# Forward  (ACCC->GCCC): Machine A :5000 -> Machine B -> Machine C -> Machine A :7000
# Reverse  (GCCC->ACCC): Machine A :7100 -> Machine C -> Machine B -> Machine A :5001

param(
    [Parameter(Mandatory)]
    [string]$ProxyAirIp,                  # Machine B IP (FOFI AIR)
    [Parameter(Mandatory)]
    [string]$ProxyGroundIp,               # Machine C IP (FOFI GROUND)
    [int]$AcccSendPort    = 5000,         # ACCC -> Machine B
    [int]$GcccSendPort    = 7100,         # GCCC -> Machine C
    [int]$GcccListenPort  = 7000,         # Machine A listens for GCCC receive
    [int]$AcccListenPort  = 5001,         # Machine A listens for ACCC receive
    [int]$IntervalMs      = 1000
)

$ErrorActionPreference = "Stop"

# ── Background listeners ────────────────────────────────────────────────────

$gcccListener = Start-Job -ScriptBlock {
    param($port)
    $ep     = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)
    $client = [System.Net.Sockets.UdpClient]::new($ep)
    $seq = 0
    while ($true) {
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        $data   = $client.Receive([ref]$remote)
        $text   = [System.Text.Encoding]::UTF8.GetString($data)
        $seq++
        Write-Output ("[GCCC-RCV #{0}] from {1} -> {2}" -f $seq, $remote, $text)
    }
} -ArgumentList $GcccListenPort

$acccListener = Start-Job -ScriptBlock {
    param($port)
    $ep     = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)
    $client = [System.Net.Sockets.UdpClient]::new($ep)
    $seq = 0
    while ($true) {
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        $data   = $client.Receive([ref]$remote)
        $text   = [System.Text.Encoding]::UTF8.GetString($data)
        $seq++
        Write-Output ("[ACCC-RCV #{0}] from {1} -> {2}" -f $seq, $remote, $text)
    }
} -ArgumentList $AcccListenPort

# ── Header ──────────────────────────────────────────────────────────────────

Write-Host "==========================================" -ForegroundColor Yellow
Write-Host " Machine A  --  Full Duplex Test"          -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Yellow
Write-Host (" ACCC send  -> {0}:{1}  (forward)" -f $ProxyAirIp,    $AcccSendPort)   -ForegroundColor White
Write-Host (" GCCC send  -> {0}:{1}  (reverse)" -f $ProxyGroundIp, $GcccSendPort)   -ForegroundColor Cyan
Write-Host (" GCCC recv  <- :{0}     (forward arrive)" -f $GcccListenPort)           -ForegroundColor Green
Write-Host (" ACCC recv  <- :{0}     (reverse arrive)" -f $AcccListenPort)           -ForegroundColor Magenta
Write-Host (" Interval: {0}ms   Ctrl+C to stop" -f $IntervalMs)                      -ForegroundColor White
Write-Host ""

# ── Senders ─────────────────────────────────────────────────────────────────

$acccSender  = [System.Net.Sockets.UdpClient]::new()
$gcccSender  = [System.Net.Sockets.UdpClient]::new()
$acccTarget  = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ProxyAirIp),    $AcccSendPort)
$gcccTarget  = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ProxyGroundIp), $GcccSendPort)
$counter = 0

try {
    while ($true) {
        # Print any received packets
        $out = Receive-Job $gcccListener
        if ($out) { Write-Host $out -ForegroundColor Green }
        $out = Receive-Job $acccListener
        if ($out) { Write-Host $out -ForegroundColor Magenta }

        $counter++
        $ts = Get-Date -Format 'HH:mm:ss.fff'

        # ACCC -> GCCC (forward)
        $msg   = ("ACCC-PKT-{0}  {1}" -f $counter, $ts)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $sent  = $acccSender.Send($bytes, $bytes.Length, $acccTarget)
        Write-Host ("[ACCC-SND #{0}] {1} bytes -> {2}:{3}  | {4}" -f $counter, $sent, $ProxyAirIp, $AcccSendPort, $msg) -ForegroundColor White

        # GCCC -> ACCC (reverse)
        $msg   = ("GCCC-PKT-{0}  {1}" -f $counter, $ts)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $sent  = $gcccSender.Send($bytes, $bytes.Length, $gcccTarget)
        Write-Host ("[GCCC-SND #{0}] {1} bytes -> {2}:{3}  | {4}" -f $counter, $sent, $ProxyGroundIp, $GcccSendPort, $msg) -ForegroundColor Cyan

        Start-Sleep -Milliseconds $IntervalMs
    }
}
finally {
    $acccSender.Close()
    $gcccSender.Close()
    Stop-Job  $gcccListener; Remove-Job $gcccListener
    Stop-Job  $acccListener; Remove-Job $acccListener
    Write-Host "Stopped." -ForegroundColor Yellow
}
