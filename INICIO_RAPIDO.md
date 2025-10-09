## Início Rápido

Este guia mostra somente o essencial para rodar o NPS Kiosk.

### Pré‑requisitos (Windows)
- .NET 8 SDK
- WebView2 Runtime

### Rodar com um comando (recomendado)
No PowerShell, na raiz do projeto:
```powershell
./run-both-simple.ps1
```
Ele abre o NpsDisplay e o Controlador (NpsTray) em janelas separadas.

### Rodar manualmente (dois terminais)
```powershell
# Terminal 1
cd src/NpsDisplay
dotnet run

# Terminal 2
cd src/NpsTray
dotnet run
```

### Configuração
Arquivo criado em:
```
%ProgramData%\UpMais\NpsKiosk\config.json
```
Edite `npsUrl` e `monitorIndex` conforme necessário.

Para instalação em produção, consulte `INSTALACAO.md`. 

Para fehcar tudo - Stop-Process -Name NpsTray,NpsDisplay,WindowRouter -Force -ErrorAction SilentlyContinue; dotnet build -c Debug -v minimal

