# ============================================================
# Deploy do GatewayAPI como serviço do Windows.
#
# Ordem: publica a versão nova em uma pasta separada -> só então para o serviço ->
# troca as pastas -> inicia -> confere /health. Se a versão nova não responder,
# volta para a versão anterior e termina com erro.
#
# Estrutura em $Raiz (padrão C:\Services\GatewayAPI):
#   app\           versão em execução
#   app-anterior\  versão anterior (usada para desfazer)
#   app-novo\      área de publicação
#   logs\          logs do gateway (não são apagados no deploy)
#
# Precisa rodar como administrador (o runner do GitHub Actions roda como SYSTEM).
# ============================================================
param(
    [string]$Origem = (Split-Path $PSScriptRoot -Parent),
    [string]$Raiz = 'C:\Services\GatewayAPI',
    [string]$NomeServico = 'GatewayAPI',
    [string]$Versao = 'manual',
    [string]$UrlSaude = 'http://127.0.0.1:5000/health',
    # Tarefa agendada usada antes da migração para serviço; fica desativada como reserva
    [string]$TarefaLegada = 'GatewayAPI'
)

$ErrorActionPreference = 'Stop'

$app = Join-Path $Raiz 'app'
$anterior = Join-Path $Raiz 'app-anterior'
$novo = Join-Path $Raiz 'app-novo'
$exe = Join-Path $app 'GatewayAPI.exe'

function Parar-Gateway {
    $servico = Get-Service -Name $NomeServico -ErrorAction SilentlyContinue
    if ($servico -and $servico.Status -ne 'Stopped') {
        Stop-Service -Name $NomeServico -Force
        $servico.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    # Garante que nenhum processo antigo continue segurando a porta ou os arquivos
    Get-Process -Name 'GatewayAPI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Testar-Saude([string]$versaoEsperada) {
    for ($i = 1; $i -le 20; $i++) {
        Start-Sleep -Seconds 2
        try {
            $r = Invoke-RestMethod -Uri $UrlSaude -TimeoutSec 5
            if ($r.status -eq 'ok' -and (-not $versaoEsperada -or $r.versao -eq $versaoEsperada)) { return $true }
        } catch { }
    }
    return $false
}

Write-Host "1. Publicando a versao $Versao (o gateway continua no ar)..."
if (Test-Path $novo) { Remove-Item $novo -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $Raiz 'logs') | Out-Null
dotnet publish (Join-Path $Origem 'GatewayAPI.csproj') -c Release -o $novo --nologo -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $novo 'GatewayAPI.exe'))) {
    throw 'A publicacao falhou. O gateway em execucao nao foi alterado.'
}
Set-Content -Path (Join-Path $novo 'versao.txt') -Value $Versao -Encoding ascii

Write-Host '2. Parando o gateway...'
$tarefa = Get-ScheduledTask -TaskName $TarefaLegada -ErrorAction SilentlyContinue
$tarefaEstavaAtiva = $tarefa -and $tarefa.State -ne 'Disabled'
if ($tarefaEstavaAtiva) {
    Stop-ScheduledTask -TaskName $TarefaLegada -ErrorAction SilentlyContinue
    Disable-ScheduledTask -TaskName $TarefaLegada | Out-Null
    Write-Host "   Tarefa agendada '$TarefaLegada' desativada (o gateway passa a rodar como servico)."
}
Parar-Gateway

Write-Host '3. Trocando as pastas...'
$haviaVersaoAnterior = Test-Path $app
if (Test-Path $anterior) { Remove-Item $anterior -Recurse -Force }
if ($haviaVersaoAnterior) { Rename-Item $app $anterior }
Rename-Item $novo $app

if (-not (Get-Service -Name $NomeServico -ErrorAction SilentlyContinue)) {
    Write-Host "   Criando o servico '$NomeServico'..."
    New-Service -Name $NomeServico -BinaryPathName "`"$exe`"" -DisplayName 'GatewayAPI' `
        -Description 'Gateway que roteia tarefas para os programas internos' -StartupType Automatic | Out-Null
    # Reinicia sozinho se o processo cair
    & sc.exe failure $NomeServico reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
}

Write-Host '4. Iniciando e conferindo a saude...'
Start-Service -Name $NomeServico
if (Testar-Saude $Versao) {
    Write-Host "SUCESSO: versao $Versao no ar." -ForegroundColor Green
    exit 0
}

Write-Host 'ERRO: a versao nova nao respondeu em /health. Desfazendo...' -ForegroundColor Red
Parar-Gateway
if (Test-Path $app) { Rename-Item $app (Join-Path $Raiz ("app-falhou-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))) }

if ($haviaVersaoAnterior) {
    Rename-Item $anterior $app
    Start-Service -Name $NomeServico
    if (Testar-Saude $null) { Write-Host 'Versao anterior restaurada e respondendo.' }
    else { Write-Host 'ATENCAO: a versao anterior tambem nao respondeu. O gateway esta fora do ar.' -ForegroundColor Red }
}
elseif ($tarefaEstavaAtiva) {
    # Primeira migracao sem versao anterior como servico: volta para a tarefa agendada
    Enable-ScheduledTask -TaskName $TarefaLegada | Out-Null
    Start-ScheduledTask -TaskName $TarefaLegada
    Write-Host "Tarefa agendada '$TarefaLegada' reativada e iniciada."
}
exit 1
