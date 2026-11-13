# Run in an ELEVATED PowerShell on the Windows host that runs spacedeskService.
# Usage:  .\capture.ps1 start   -> begin capture (then connect a spacedesk client)
#         .\capture.ps1 stop    -> stop and write spacedesk.pcapng
param([Parameter(Mandatory)][ValidateSet('start','stop')][string]$Action)

$Port = 28252
$Out  = Join-Path $PSScriptRoot 'spacedesk'

if ($Action -eq 'start') {
    pktmon stop 2>$null | Out-Null
    pktmon filter remove
    pktmon filter add spacedesk-tcp -t TCP -p $Port
    pktmon filter add spacedesk-udp -t UDP -p $Port
    pktmon start --capture --pkt-size 0 --file-name "$Out.etl"
    Write-Host "Capturing port $Port. Connect the client now, use it ~1 min (move mouse, type, touch), then run: .\capture.ps1 stop"
} else {
    pktmon stop
    pktmon etl2pcap "$Out.etl" --out "$Out.pcapng"
    Write-Host "Wrote $Out.pcapng"
}
