@echo off
REM Script para rodar KioskBrowser (Display) e KioskController (Controller) simultaneamente (versão Windows CMD)
REM Este script resolve o problema de dependência entre os projetos

echo === KioskMax - Iniciando Controller e Browser ===

REM Verifica se estamos no diretório correto
if not exist "src" (
    echo Erro: Execute este script na raiz do projeto (onde está a pasta src)
    pause
    exit /b 1
)

REM Compila ambos os projetos
echo Compilando projetos...
echo Compilando NpsShared...
dotnet build src/NpsShared/NpsShared.csproj --configuration Debug --verbosity quiet
if errorlevel 1 (
    echo Erro na compilação do NpsShared
    pause
    exit /b 1
)

echo Compilando KioskBrowser (Display)...
dotnet build src/NpsDisplay/NpsDisplay.csproj --configuration Debug --verbosity quiet
if errorlevel 1 (
    echo Erro na compilação do NpsDisplay
    pause
    exit /b 1
)

echo Compilando KioskController (Tray)...
dotnet build src/KioskController/KioskController.csproj --configuration Debug --verbosity quiet
if errorlevel 1 (
    echo Erro na compilação do KioskController
    pause
    exit /b 1
)

echo Compilação concluída com sucesso!

REM Cria um diretório temporário para copiar os executáveis
set tempDir=temp-run
if exist "%tempDir%" rmdir /s /q "%tempDir%"
mkdir "%tempDir%"

REM Copia os executáveis necessários
echo Preparando executáveis...

REM Copia Browser
set displaySource=src\NpsDisplay\bin\Debug\net8.0-windows
set displayDest=%tempDir%\KioskBrowser
xcopy "%displaySource%" "%displayDest%" /e /i /y >nul

REM Copia Controller
set traySource=src\KioskController\bin\Debug\net8.0-windows
set trayDest=%tempDir%\KioskController
xcopy "%traySource%" "%trayDest%" /e /i /y >nul

REM Copia NpsShared para ambos os diretórios
set sharedSource=src\NpsShared\bin\Debug\net8.0
copy "%sharedSource%\NpsShared.dll" "%displayDest%\" >nul
copy "%sharedSource%\NpsShared.pdb" "%displayDest%\" >nul
copy "%sharedSource%\NpsShared.dll" "%trayDest%\" >nul
copy "%sharedSource%\NpsShared.pdb" "%trayDest%\" >nul

REM Copia KioskBrowser.exe (ou NpsDisplay.exe legada) para o diretório do Controller
if exist "%displayDest%\KioskBrowser.exe" (
  copy "%displayDest%\KioskBrowser.exe" "%trayDest%\" >nul
) else if exist "%displayDest%\NpsDisplay.exe" (
  copy "%displayDest%\NpsDisplay.exe" "%trayDest%\KioskBrowser.exe" >nul
)

echo Executáveis preparados!

echo.
echo Iniciando KioskBrowser...
if exist "%displayDest%\KioskBrowser.exe" (
  start "KioskBrowser" /D "%displayDest%" "KioskBrowser.exe"
) else (
  start "KioskBrowser" /D "%displayDest%" "NpsDisplay.exe"
)

echo Iniciando KioskController...
start "KioskController" /D "%trayDest%" "KioskController.exe"

echo.
echo === Ambos os projetos estão rodando! ===
echo.
echo Pressione qualquer tecla para parar ambos os projetos...
pause >nul

REM Para os processos
echo Parando processos...
taskkill /f /im "KioskBrowser.exe" >nul 2>&1
taskkill /f /im "NpsDisplay.exe" >nul 2>&1
taskkill /f /im "KioskController.exe" >nul 2>&1
taskkill /f /im "KioskController.exe" >nul 2>&1

REM Remove diretório temporário
if exist "%tempDir%" rmdir /s /q "%tempDir%"

echo Limpeza concluída!
echo.
echo Aplicação finalizada!
pause
