# Script para rodar KioskBrowser (Display) e KioskController (Controller) simultaneamente
# Este script resolve o problema de dependência entre os projetos

Write-Host "=== KioskMax - Iniciando Controller e Browser ===" -ForegroundColor Green

# Verifica se estamos no diretório correto
if (-not (Test-Path "src")) {
    Write-Host "Erro: Execute este script na raiz do projeto (onde está a pasta src)" -ForegroundColor Red
    exit 1
}

# Compila ambos os projetos
Write-Host "Compilando projetos..." -ForegroundColor Yellow
try {
    # Compila NpsShared primeiro (dependência comum)
    Write-Host "Compilando NpsShared..." -ForegroundColor Cyan
    dotnet build src/NpsShared/NpsShared.csproj --configuration Debug --verbosity quiet
    
    # Compila KioskBrowser (Display)
    Write-Host "Compilando KioskBrowser (Display)..." -ForegroundColor Cyan
    dotnet build src/NpsDisplay/NpsDisplay.csproj --configuration Debug --verbosity quiet
    
    # Compila KioskController (Tray)
    Write-Host "Compilando KioskController (Tray)..." -ForegroundColor Cyan
    dotnet build src/KioskController/KioskController.csproj --configuration Debug --verbosity quiet

    # Compila WindowRouter
    Write-Host "Compilando WindowRouter..." -ForegroundColor Cyan
    dotnet build src/WindowRouter/WindowRouter.csproj --configuration Debug --verbosity quiet
    
    Write-Host "Compilação concluída com sucesso!" -ForegroundColor Green
}
catch {
    Write-Host "Erro na compilação: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Cria um diretório temporário para copiar os executáveis
$tempDir = "temp-run"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempDir | Out-Null

# Copia os executáveis necessários
Write-Host "Preparando executáveis..." -ForegroundColor Yellow

# Copia KioskBrowser
$displaySource = "src/NpsDisplay/bin/Debug/net8.0-windows"
$displayDest = "$tempDir/KioskBrowser"
Copy-Item $displaySource -Destination $displayDest -Recurse -Force

# Copia KioskController
$traySource = "src/KioskController/bin/Debug/net8.0-windows"
$trayDest = "$tempDir/KioskController"
Copy-Item $traySource -Destination $trayDest -Recurse -Force

# Copia WindowRouter ao lado do Tray para que o Tray consiga iniciá-lo
$routerSource = "src/WindowRouter/bin/Debug/net8.0-windows"
if (Test-Path $routerSource) {
    Copy-Item (Join-Path $routerSource "WindowRouter.exe") -Destination "$trayDest/" -Force -ErrorAction SilentlyContinue
}

# Copia NpsShared para ambos os diretórios
$sharedSource = "src/NpsShared/bin/Debug/net8.0"
Copy-Item "$sharedSource/NpsShared.dll" -Destination "$displayDest/" -Force
Copy-Item "$sharedSource/NpsShared.pdb" -Destination "$displayDest/" -Force
Copy-Item "$sharedSource/NpsShared.dll" -Destination "$trayDest/" -Force
Copy-Item "$sharedSource/NpsShared.pdb" -Destination "$trayDest/" -Force

# Copia KioskBrowser.exe (ou NpsDisplay.exe legado) para o diretório do Controller (compat)
if (Test-Path "$displayDest/KioskBrowser.exe") {
    Copy-Item "$displayDest/KioskBrowser.exe" -Destination "$trayDest/" -Force
} elseif (Test-Path "$displayDest/NpsDisplay.exe") {
    Copy-Item "$displayDest/NpsDisplay.exe" -Destination "$trayDest/KioskBrowser.exe" -Force
}

Write-Host "Executáveis preparados!" -ForegroundColor Green

# Função para limpar recursos
function Cleanup {
    Write-Host "`nParando processos..." -ForegroundColor Yellow
    
    # Para os processos específicos se ainda estiverem rodando
    if ($displayProcess -and -not $displayProcess.HasExited) {
        $displayProcess.Kill()
        $displayProcess.WaitForExit(5000)
    }
    if ($trayProcess -and -not $trayProcess.HasExited) {
        $trayProcess.Kill()
        $trayProcess.WaitForExit(5000)
    }
    
    # Fallback: para qualquer processo com esses nomes
    Get-Process -Name "NpsDisplay" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process -Name "KioskController" -ErrorAction SilentlyContinue | Stop-Process -Force
    
    # Remove diretório temporário
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
    
    Write-Host "Limpeza concluída!" -ForegroundColor Green
}

# Configura o handler para Ctrl+C
$null = Register-EngineEvent PowerShell.Exiting -Action { Cleanup }

Write-Host "`nIniciando KioskBrowser..." -ForegroundColor Cyan
if (Test-Path "$displayDest\KioskBrowser.exe") {
    $displayProcess = Start-Process -FilePath "$displayDest\KioskBrowser.exe" -PassThru -WindowStyle Normal
} else {
    $displayProcess = Start-Process -FilePath "$displayDest\NpsDisplay.exe" -PassThru -WindowStyle Normal
}

Write-Host "Iniciando KioskController..." -ForegroundColor Cyan
$trayProcess = Start-Process -FilePath "$trayDest\KioskController.exe" -PassThru -WindowStyle Normal

Write-Host "`n=== Ambos os projetos estão rodando! ===" -ForegroundColor Green
Write-Host "KioskBrowser PID: $($displayProcess.Id)" -ForegroundColor Gray
Write-Host "KioskController PID: $($trayProcess.Id)" -ForegroundColor Gray
Write-Host "`nPressione Ctrl+C para parar ambos os projetos" -ForegroundColor Yellow

# Aguarda os processos
try {
    while (-not $displayProcess.HasExited -or -not $trayProcess.HasExited) {
        Start-Sleep -Seconds 1
        
        # Verifica se algum processo falhou
        if ($displayProcess.HasExited -and $displayProcess.ExitCode -ne 0) {
            Write-Host "KioskBrowser falhou com código: $($displayProcess.ExitCode)" -ForegroundColor Red
        }
        if ($trayProcess.HasExited -and $trayProcess.ExitCode -ne 0) {
            Write-Host "KioskController falhou com código: $($trayProcess.ExitCode)" -ForegroundColor Red
        }
    }
}
finally {
    Cleanup
}

Write-Host "`nAplicação finalizada!" -ForegroundColor Green
