# Run in an ELEVATED PowerShell (64-bit) on the Windows host.
# Usage:  .\phone_test.ps1 start    -> stop vendor service, run ref_server (TLS-PSK) with logging, restore service on exit
#         .\phone_test.ps1 restore  -> start vendor service again and print the log summary
#         .\phone_test.ps1 log      -> print the log summary only
param(
    [Parameter(Mandatory)][ValidateSet('start','restore','log')][string]$Action,
    [string]$Password = '',
    [int]$Port = 28252,
    [string]$Python = 'C:\Users\augus\miniconda3\python.exe',
    [string]$Dir = $PSScriptRoot
)

$Server = Join-Path $Dir 'ref_server\server.py'
$Log    = Join-Path $Dir 'ref_log.txt'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Log {
    if (Test-Path $Log) {
        Get-Content $Log | Select-String -Pattern 'tls|plain|ident|client msg|closed|failed'
    } else {
        Write-Host "No log at $Log"
    }
}

function Restore-Service {
    Start-Service spacedeskService -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    Get-Service spacedeskService | Format-List Name, Status
}

if ($Action -eq 'log') { Show-Log; return }

if (-not (Test-Admin)) { Write-Host 'Run this from an elevated (Administrator) PowerShell.'; return }

if ($Action -eq 'restore') { Restore-Service; Show-Log; return }

# start
if (-not $Password) {
    # Same value the vendor service uses: read it instead of keeping a copy in this script.
    $Password = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\datronicsoft\spacedesk\Service' -Name EncryptionPassword -ErrorAction SilentlyContinue).EncryptionPassword
}
if (-not $Password) { Write-Host 'No EncryptionPassword in the registry: pass -Password XXXXXXXX.'; return }
if (-not (Test-Path $Server)) { Write-Host "Missing $Server"; return }
if (-not (Select-String -Path $Server -Pattern 'PSK_SUFFIX' -Quiet)) {
    Write-Host 'server.py has no PSK_SUFFIX: old copy, copy the new ref_server first.'; return
}

Stop-Process -Name spacedeskConsole, spacedeskServiceTray -Force -ErrorAction SilentlyContinue
Stop-Service spacedeskService -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
$svc = Get-Service spacedeskService
Write-Host "spacedeskService: $($svc.Status)"
$busy = @(Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue) + @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
if ($svc.Status -ne 'Stopped' -or $busy.Count -gt 0) {
    Write-Host "Port $Port is still in use (PID $($busy.OwningProcess -join ',')). Aborting."
    return
}

Remove-Item $Log -ErrorAction SilentlyContinue
Write-Host "Starting ref_server on port $Port. Connect the phone now. Close the window or press Ctrl+Break to stop."
try {
    & $Python $Server --port $Port --psk $Password --logfile $Log
} finally {
    Write-Host 'Restoring spacedeskService...'
    Restore-Service
    Show-Log
}
