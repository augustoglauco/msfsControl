# Run in an ELEVATED PowerShell (64-bit) on the Windows host.
# Logging proxy between the phone and the running vendor service (TLS-PSK on both sides).
# Usage:  .\proxy_test.ps1 start   -> check the service, run the proxy on tcp 28253 (+ discovery on udp 28252)
#         .\proxy_test.ps1 log     -> print the log summary
param(
    [Parameter(Mandatory)][ValidateSet('start','log')][string]$Action,
    [string]$Password = '',
    [string]$Python = 'C:\Users\augus\miniconda3\python.exe',
    [string]$Dir = $PSScriptRoot
)

$Proxy = Join-Path $Dir 'ref_server\proxy.py'
$Log   = Join-Path $Dir 'proxy_log.txt'

if ($Action -eq 'log') {
    if (Test-Path $Log) { Get-Content $Log | Select-String -Pattern 'type=|changed|connected|closed|failed|discovery' } else { Write-Host "No log at $Log" }
    return
}

if (-not (Test-Path $Proxy)) { Write-Host "Missing $Proxy"; return }
$svc = Get-Service spacedeskService
if ($svc.Status -ne 'Running') { Start-Service spacedeskService; Start-Sleep -Seconds 3; $svc = Get-Service spacedeskService }
Write-Host "spacedeskService: $($svc.Status)"
$ssl = (reg query "HKLM\SOFTWARE\datronicsoft\spacedesk\Service" /v EncryptionSslEnable /reg:64 | Select-String 'EncryptionSslEnable') -join ''
Write-Host $ssl.Trim()
if ($ssl -notmatch '0x1') { Write-Host 'EncryptionSslEnable is not 1: set it to 1 first (the proxy talks TLS-PSK to the service).'; return }
if (-not $Password) {
    # Same value the vendor service uses: read it instead of keeping a copy in this script.
    $Password = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\datronicsoft\spacedesk\Service' -Name EncryptionPassword -ErrorAction SilentlyContinue).EncryptionPassword
}
if (-not $Password) { Write-Host 'No EncryptionPassword in the registry: pass -Password XXXXXXXX.'; return }

Remove-Item $Log -ErrorAction SilentlyContinue
Write-Host 'Starting the proxy. On the phone choose the entry named PROXY. Close the window or press Ctrl+Break to stop.'
& $Python $Proxy --psk $Password --logfile $Log
