# Script simples para rodar KioskBrowser (Display) e KioskController (Controller) simultaneamente
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
# Localiza o executável copiado (kiosk ou legado)
$displayExePath = (Get-ChildItem -Path $displayDest -Recurse -Filter "KioskBrowser.exe" | Select-Object -First 1).FullName
if (-not $displayExePath) { $displayExePath = (Get-ChildItem -Path $displayDest -Recurse -Filter "NpsDisplay.exe" | Select-Object -First 1).FullName }
$displayExeDir = Split-Path -Parent $displayExePath

# Copia KioskController
$traySource = "src/KioskController/bin/Debug/net8.0-windows"
$trayDest = "$tempDir/KioskController"
Copy-Item $traySource -Destination $trayDest -Recurse -Force
# Localiza o executável copiado
$trayExePath = (Get-ChildItem -Path $trayDest -Recurse -Filter "KioskController.exe" | Select-Object -First 1).FullName
$trayExeDir = Split-Path -Parent $trayExePath

# Copia o KioskBrowser.exe (ou NpsDisplay.exe legado) para o mesmo diretório do Controller
try {
    if ($displayExePath -and (Test-Path $displayExePath)) { Copy-Item $displayExePath -Destination $trayExeDir -Force -ErrorAction SilentlyContinue }
} catch { }

# Copia NpsShared para ambos os diretórios
$sharedSource = "src/NpsShared/bin/Debug/net8.0"
Copy-Item "$sharedSource/NpsShared.dll" -Destination "$displayDest/" -Force
Copy-Item "$sharedSource/NpsShared.pdb" -Destination "$displayDest/" -Force
Copy-Item "$sharedSource/NpsShared.dll" -Destination "$trayDest/" -Force
Copy-Item "$sharedSource/NpsShared.pdb" -Destination "$trayDest/" -Force

# Copia WindowRouter.exe para o diretório do Tray para facilitar o start automático
$routerSource = "src/WindowRouter/bin/Debug/net8.0-windows"
if (Test-Path $routerSource) {
    Copy-Item (Join-Path $routerSource "WindowRouter.exe") -Destination "$trayDest/" -Force -ErrorAction SilentlyContinue
}

# Não copiamos o executável para o diretório do Tray; o Tray inicia do diretório correto

Write-Host "Executáveis preparados!" -ForegroundColor Green

Write-Host "`n=== Iniciando aplicações ===" -ForegroundColor Green
Write-Host "KioskBrowser será iniciado em uma nova janela..." -ForegroundColor Cyan
Write-Host "KioskController será iniciado em uma nova janela..." -ForegroundColor Cyan
Write-Host "`nPressione qualquer tecla para parar ambos os projetos..." -ForegroundColor Yellow

# Inicia KioskBrowser no diretório real de build
Start-Process -FilePath "$displayExePath" -WorkingDirectory "$displayExeDir"

# Aguarda um pouco para o NpsDisplay inicializar
Start-Sleep -Seconds 2

# Inicia KioskController (Controller)
Start-Process -FilePath "$trayExePath" -WorkingDirectory "$trayExeDir"

# Aguarda input do usuário
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

Write-Host "`nParando processos..." -ForegroundColor Yellow

# Para os processos (novos nomes + legado)
Get-Process -Name "KioskBrowser" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "NpsDisplay" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "KioskController" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "NpsTray" -ErrorAction SilentlyContinue | Stop-Process -Force

# Remove diretório temporário com retry (arquivos bloqueados como WebView2Loader.dll)
if (Test-Path $tempDir) {
    $maxRetries = 5
    for ($i = 0; $i -lt $maxRetries; $i++) {
        try {
            Remove-Item $tempDir -Recurse -Force -ErrorAction Stop
            break
        } catch {
            Start-Sleep -Milliseconds 600
        }
    }
}

Write-Host "Limpeza concluída!" -ForegroundColor Green
Write-Host "`nAplicação finalizada!" -ForegroundColor Green
