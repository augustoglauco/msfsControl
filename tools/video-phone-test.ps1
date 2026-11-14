#Requires -Version 7
<#
  Teste do modulo de video do MonRem com o app spacedesk do celular.
  Rode em um PowerShell 7 ABERTO COMO ADMINISTRADOR.

  O que faz:
    1. compila o host (antes de mexer em qualquer coisa);
    2. para o spacedeskService (ele ocupa TCP/UDP 28252) e espera a porta liberar;
    3. cria 2 regras de firewall temporarias (TCP e UDP 28252);
    4. roda o host com video ligado, simulador falso e dados em pasta temporaria;
    5. ao sair (Ctrl+C ou erro): remove as regras e religa o spacedeskService.
#>
param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [int]$Port = 28252
)

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERRO: abra o PowerShell 7 como administrador e rode de novo.' -ForegroundColor Red
    exit 1
}

$svcName   = 'spacedeskService'
$ruleTcp   = 'MonRem video teste TCP'
$ruleUdp   = 'MonRem video teste UDP'
$project   = Join-Path $Repo 'src\host\MonRem.Host'
$dataDir   = Join-Path $env:TEMP 'monrem-phone-test'
$svc       = Get-Service $svcName -ErrorAction SilentlyContinue
$wasRunning = $null -ne $svc -and $svc.Status -eq 'Running'

if (-not (Test-Path $project)) { Write-Host "ERRO: nao achei $project" -ForegroundColor Red; exit 1 }

Write-Host '1/4 Compilando o host...' -ForegroundColor Cyan
dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'ERRO: a compilacao falhou. Nada foi alterado.' -ForegroundColor Red; exit 1 }

try {
    Write-Host "2/4 Parando $svcName (estava rodando: $wasRunning)..." -ForegroundColor Cyan
    if ($wasRunning) {
        Stop-Service $svcName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and
           (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        throw "A porta TCP $Port continua ocupada por outro processo."
    }

    Write-Host "3/4 Criando regras de firewall temporarias (porta $Port)..." -ForegroundColor Cyan
    Remove-NetFirewallRule -DisplayName $ruleTcp, $ruleUdp -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleTcp -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
    New-NetFirewallRule -DisplayName $ruleUdp -Direction Inbound -Protocol UDP -LocalPort $Port -Action Allow -Profile Any | Out-Null

    $ips = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.OperationalStatus -eq 'Up' -and $_.NetworkInterfaceType -ne 'Loopback' } |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and $_.Address.ToString() -notlike '169.254.*' } |
        ForEach-Object { $_.Address.ToString() }

    Write-Host ''
    Write-Host "4/4 Host no ar. Nome na lista do app: $env:COMPUTERNAME" -ForegroundColor Green
    Write-Host "    IPs deste PC (para digitar no app se a lista nao mostrar): $($ips -join ', ')" -ForegroundColor Green
    Write-Host '    Abra o spacedesk no celular, escolha o host. Esperado: barra clara cruzando a tela.' -ForegroundColor Green
    Write-Host '    Ctrl+C aqui encerra o teste e restaura o spacedesk.' -ForegroundColor Green
    Write-Host ''

    dotnet run --project $project -c Release --no-build -- `
        --MonRem:UseFakeSim=true `
        --MonRem:Video:Enabled=true `
        "--MonRem:Video:TcpPort=$Port" "--MonRem:Video:UdpPort=$Port" `
        "--MonRem:DataDirectory=$dataDir"
}
finally {
    Write-Host ''
    Write-Host 'Restaurando: removendo regras e religando o spacedesk...' -ForegroundColor Cyan
    Remove-NetFirewallRule -DisplayName $ruleTcp, $ruleUdp -ErrorAction SilentlyContinue
    if ($wasRunning) {
        Start-Service $svcName -ErrorAction Continue
        Write-Host "$svcName : $((Get-Service $svcName).Status)" -ForegroundColor Cyan
    }
}
