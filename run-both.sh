#!/bin/bash
# Script para rodar KioskBrowser (Display) e KioskController (Controller) simultaneamente (versão Linux/Mac)
# Este script resolve o problema de dependência entre os projetos

echo "=== NPS Kiosk - Iniciando ambos os projetos ==="

# Verifica se estamos no diretório correto
if [ ! -d "src" ]; then
    echo "Erro: Execute este script na raiz do projeto (onde está a pasta src)"
    exit 1
fi

# Compila ambos os projetos
echo "Compilando projetos..."
echo "Compilando NpsShared..."
dotnet build src/NpsShared/NpsShared.csproj --configuration Debug --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Erro na compilação do NpsShared"
    exit 1
fi

echo "Compilando KioskBrowser (Display)..."
dotnet build src/NpsDisplay/NpsDisplay.csproj --configuration Debug --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Erro na compilação do NpsDisplay"
    exit 1
fi

echo "Compilando KioskController (Tray)..."
dotnet build src/KioskController/KioskController.csproj --configuration Debug --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Erro na compilação do KioskController"
    exit 1
fi

echo "Compilação concluída com sucesso!"

# Cria um diretório temporário para copiar os executáveis
TEMP_DIR="temp-run"
rm -rf "$TEMP_DIR"
mkdir "$TEMP_DIR"

# Copia os executáveis necessários
echo "Preparando executáveis..."

# Copia KioskBrowser
DISPLAY_SOURCE="src/NpsDisplay/bin/Debug/net8.0-windows"
DISPLAY_DEST="$TEMP_DIR/KioskBrowser"
cp -r "$DISPLAY_SOURCE" "$DISPLAY_DEST"

# Copia KioskController
TRAY_SOURCE="src/KioskController/bin/Debug/net8.0-windows"
TRAY_DEST="$TEMP_DIR/KioskController"
cp -r "$TRAY_SOURCE" "$TRAY_DEST"

# Copia NpsShared para ambos os diretórios
SHARED_SOURCE="src/NpsShared/bin/Debug/net8.0"
cp "$SHARED_SOURCE/NpsShared.dll" "$DISPLAY_DEST/"
cp "$SHARED_SOURCE/NpsShared.pdb" "$DISPLAY_DEST/" 2>/dev/null || true
cp "$SHARED_SOURCE/NpsShared.dll" "$TRAY_DEST/"
cp "$SHARED_SOURCE/NpsShared.pdb" "$TRAY_DEST/" 2>/dev/null || true

# Copia KioskBrowser.exe (ou NpsDisplay.exe legado) para o diretório do Controller (compat)
if [ -f "$DISPLAY_DEST/KioskBrowser.exe" ]; then
  cp "$DISPLAY_DEST/KioskBrowser.exe" "$TRAY_DEST/"
elif [ -f "$DISPLAY_DEST/NpsDisplay.exe" ]; then
  cp "$DISPLAY_DEST/NpsDisplay.exe" "$TRAY_DEST/KioskBrowser.exe"
fi

echo "Executáveis preparados!"

# Função para limpar recursos
cleanup() {
    echo ""
    echo "Parando processos..."
    
    # Para os processos
    pkill -f "NpsDisplay.exe" 2>/dev/null || true
    pkill -f "KioskController.exe" 2>/dev/null || true
    
    # Remove diretório temporário
    rm -rf "$TEMP_DIR"
    
    echo "Limpeza concluída!"
}

# Configura o handler para Ctrl+C
trap cleanup EXIT INT TERM

echo ""
echo "Iniciando KioskBrowser..."
cd "$DISPLAY_DEST"
if [ -f "./KioskBrowser.exe" ]; then ./KioskBrowser.exe & else ./NpsDisplay.exe & fi
DISPLAY_PID=$!

echo "Iniciando KioskController..."
cd "$TRAY_DEST"
./KioskController.exe &
TRAY_PID=$!

echo ""
echo "=== Ambos os projetos estão rodando! ==="
echo "KioskBrowser PID: $DISPLAY_PID"
echo "KioskController PID: $TRAY_PID"
echo ""
echo "Pressione Ctrl+C para parar ambos os projetos"

# Aguarda os processos
wait $DISPLAY_PID $TRAY_PID

echo ""
echo "Aplicação finalizada!"
