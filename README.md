# NPS Kiosk (KioskMax)

Uma solução Windows para exibir pesquisas NPS em modo kiosque com controle pela bandeja do sistema.

Repositório: https://github.com/maxyuri99/KioskMax

## Recursos

- Display em tela cheia com WebView2 (Chromium)
- Sempre visível (TopMost) e resiliência a falhas
- Multi‑monitor: escolha por índice/ID ou primário
- Controlador na bandeja (tray) para iniciar/parar, trocar URL, monitor, recarregar
- Roteador de janelas opcional (evita que outras janelas invadam o monitor do NPS)
- Watchdog opcional (serviço) para reinício automático
- Logs rotativos e configuração persistida em `%ProgramData%`

## Componentes

- Display: `src/KioskBrowser` (WinForms + WebView2)
- Controller (bandeja): `src/KioskController`
- WindowRouter: `src/WindowRouter`
- Watchdog: `src/NpsWatchdog`
- Biblioteca compartilhada: `src/NpsShared`

## Requisitos

- Windows 10/11 x64
- WebView2 Runtime (obrigatório em produção)
- .NET 8 SDK (apenas para desenvolvimento)

## Como executar (dev)

```powershell
# Restaurar e compilar
dotnet restore
dotnet build -c Debug

# Abrir Browser e Controller rapidamente
./run-both-simple.ps1
```

## Publicando pacote portátil

Gera executáveis auto‑contidos (não precisa .NET instalado) e monta um bundle único:

```powershell
# Criar bundle
powershell -ExecutionPolicy Bypass -File build/scripts/publish-bundle.ps1 -Configuration Release -Runtime win-x64 -Zip

# Saída
build/publish/
  NpsKiosk-Portable/
  NpsKiosk-Portable_YYYYMMDD_HHMM.zip
```

Conteúdo do bundle:
- Browser/Controller/Router/Watchdog publicados (single-file)
- Pasta `icones/` (se existir)
- `config/config.sample.json`
- Scripts `run-both*`
- Este README

## Instalador (recomendado)

Gera um instalador `.exe` via Inno Setup que instala em `C:\\Program Files\\UpMais\\NpsKiosk`, cria atalhos no Menu Iniciar e opção para atalho na área de trabalho.

Pré‑requisito: Inno Setup 6 (ISCC). Se o instalador do Inno Setup estiver em caminho diferente, use `-InnoSetupPath` no comando.

```powershell
# Construir instalador (gera também o bundle antes)
powershell -ExecutionPolicy Bypass -File build/scripts/build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 1.0.0

# Saída
build/publish/installer/
  KioskMaxSetup_1.0.0.exe
```

Dica: Coloque um ícone `app.ico` na pasta `icones/` para o instalador e atalhos usarem o ícone do KioskMax.

## Configuração

Arquivo persistido em:
```
%ProgramData%\\UpMais\\NpsKiosk\\config.json
```

Exemplo (padrão):
```json
{
  "npsUrl": "https://seu-endereco/nps",
  "monitorPolicy": "Explicit",
  "monitorDeviceId": "",
  "monitorIndex": 0,
  "topMost": true,
  "kioskFullScreen": true,
  "autoRecover": true,
  "dpiMode": "PerMonitorV2",
  "routerEnabled": true,
  "routerTarget": "Opposite",
  "logLevel": "Information"
}
```

## Operação

- Use o ícone da bandeja (KioskMax) para:
  - Iniciar/Parar o Display
  - Alterar URL, Monitor, Recarregar
  - Abrir pastas de Config e Logs
  - Ativar/Desativar o WindowRouter

## Solução de problemas

- Display não abre: verifique WebView2 Runtime e logs em `%ProgramData%\\UpMais\\NpsKiosk\\logs`
- Monitor incorreto: troque pelo menu da bandeja ou edite `monitorIndex` no `config.json`
- Janelas sobrepondo: habilite `topMost` e/ou WindowRouter

## Licença

Consulte `LICENSE`.
