# ============================================================
# SCRIPT PARA CRIAR TAREFA AGENDADA DE DEPLOY AUTOMÁTICO
# Uso: .\criar-tarefa.ps1 -NomeTarefa "MeuApp" -CaminhoBat "C:\projetos\meuapp\iniciar.bat"
# ============================================================

param(
    [Parameter(Mandatory=$true)]
    [string]$NomeTarefa,
    
    [Parameter(Mandatory=$true)]
    [string]$CaminhoBat
)

Write-Host "🚀 Criando tarefa agendada: $NomeTarefa" -ForegroundColor Cyan

# Remove tarefa antiga se existir
Unregister-ScheduledTask -TaskName $NomeTarefa -Confirm:$false -ErrorAction SilentlyContinue

# Define a ação
$action = New-ScheduledTaskAction `
    -Execute $CaminhoBat `
    -WorkingDirectory (Split-Path $CaminhoBat -Parent)

# Define o gatilho (inicia na inicialização do Windows)
$trigger = New-ScheduledTaskTrigger -AtStartup

# Configura para rodar como o usuário Administrator
$principal = New-ScheduledTaskPrincipal `
    -UserId "Administrator" `
    -LogonType Interactive `
    -RunLevel Highest

# Configurações importantes
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

# Registra a tarefa
Register-ScheduledTask `
    -TaskName $NomeTarefa `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description "Deploy automático - $NomeTarefa" `
    -Force

Write-Host "✅ Tarefa '$NomeTarefa' criada com sucesso!" -ForegroundColor Green
Write-Host ""
Write-Host "📋 Próximos passos:" -ForegroundColor Yellow
Write-Host "   1. Teste com: Start-ScheduledTask -TaskName '$NomeTarefa'"
Write-Host "   2. Verifique com: Get-Process | Where-Object { ... }"
Write-Host "   3. Configure o deploy.yml no GitHub"