# NPS Kiosk (KioskMax)

Agente Windows do Kiosk UP+: exibe a tela do cliente (frente de caixa) em modo kiosque, é controlado pelo Gestão (banco de dados) e hospeda o serviço TEF (substitui o TEFManager Delphi na porta 8069).

Repositório: https://github.com/maxyuri99/KioskMax

## Recursos

- Display em tela cheia com WebView2 (Chromium)
- Sempre visível (TopMost) e resiliência a falhas
- Multi‑monitor: padrão primário; escolha por índice/ID ou primário
- Controlador na bandeja (tray) para iniciar/parar, trocar URL, monitor, recarregar
- Roteador de janelas opcional (evita que outras janelas invadam o monitor do NPS)
- Watchdog opcional (serviço) para reinício automático
- Logs rotativos e configuração persistida em `%ProgramData%`
- Atalhos globais: Ctrl+Alt+K/H para abrir o Controller
- Display oculto na barra de tarefas

## Arquitetura v2 (controle pelo Gestão + TEF)

- O `KioskController` é o agente: a cada ~5 s envia o sinal ao Gestão (`POST {gestaoApiUrl}/kiosk/dispositivo/sinal`), aplica a configuração quando `nuVersaoConfig` ou a situação mudam e executa os comandos da fila (`RECARREGAR`, `REINICIAR_TELA`, `LIMPAR_CACHE`, `REINICIAR_ROTEADOR`, `REINICIAR_TEF`, `IMPRIMIR_TESTE_TEF`, `REINICIAR_AGENTE`). Contrato completo em `frontUpMais-docs/kiosk-contrato.md`.
- Primeira execução: o agente gera o identificador (GUID) e se registra; o kiosk fica `PENDENTE` até ser aprovado no Gestão. O token é guardado em `kiosk.token` (DPAPI, escopo máquina) na pasta de configuração.
- Telas locais (geradas pelo KioskBrowser, fundo azul `#243d92`): `AGUARDANDO` (pendente), `BLOQUEADO` e `SEM_TELA` (ativo sem caixa/tela).
- Com a situação `ATIVO` o banco manda: caixa/tela (URL), exibir, monitor, sempre visível, roteador, TEF e impressão. Sem rede o agente continua com a última configuração aplicada (backoff 5→10→20→30 s).
- O KioskBrowser injeta `window.upKiosk` antes de qualquer script da página, recebe `{tipo:'ESTADO'}` da página (devolvido ao Gestão em `tela.pagina`) e aceita `{tipo:'RECARREGAR'}` / `{tipo:'KIOSK'}` do agente.
- TEF (`src/KioskTef`, hospedado no Controller): servidor HTTP em `localhost:8069` (`/tef/teste`, `/pagar`, `/consultar`, `/imprimir`), mesma pasta `%APPDATA%\TEFManagerUP` do Delphi. A impressão usa a configuração do banco quando o kiosk está `ATIVO`; caso contrário, o INI `%APPDATA%\TEFManagerUP\TEFManager.ini`.
- Sem `gestaoApiUrl` o comportamento é o antigo (somente local); o TEF sobe se `tefAtivo`.
- Menu da bandeja e Controller: status "Gestão" e "TEF", "Vínculo com o Gestão..." (URL, identificador, situação), "Imprimir teste TEF" e grupo "Gestão / TEF" (impressora editável só quando o kiosk não é controlado pelo Gestão).

Campos novos do `config.json`: `gestaoApiUrl` (vazio = modo local), `kioskIdentificador` (gerado), `kioskContexto` (último `upKiosk`), `tefAtivo` (padrão `true`), além de controle interno (`ultimaVersaoConfig`, `ultimaSituacao`, `telaLocal`, `gestaoExibir`, `tefGestao`).

Instalador: `KioskMaxSetup_2.0.0.exe /GESTAOURL=https://.../api` grava `gestaoApiUrl` no `config.json` na instalação.

Os executáveis do Controller ficam grandes (~190 MB single-file) porque o TEF usa ASP.NET Core embutido.

## Componentes

- Display: `src/KioskBrowser` (WinForms + WebView2)
- Controller / agente (bandeja): `src/KioskController`
- TEF: `src/KioskTef`
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
powershell -ExecutionPolicy Bypass -File build/scripts/build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 2.0.0

# Saída
build/publish/installer/
  KioskMaxSetup_2.0.0.exe
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
  "monitorPolicy": "Primary",
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

Observações:
- Se você escolher um monitor manualmente pelo Controller, a política muda para "Explicit" automaticamente e persiste.

## Operação

- Use o ícone da bandeja (KioskMax) para:
  - Iniciar/Parar o Display
  - Alterar URL, Monitor, Recarregar
  - Abrir pastas de Config e Logs
  - Ativar/Desativar o WindowRouter
  - Usar "Use Primary (Auto)" para alternar o uso do monitor primário

- Atalhos globais:
  - Ctrl+Alt+K ou Ctrl+Alt+H: abre o Controller (mesmo com o Display em foco)

## Solução de problemas

- Display não abre: verifique WebView2 Runtime e logs em `%ProgramData%\\UpMais\\NpsKiosk\\logs`
- Monitor incorreto: troque pelo menu da bandeja ou edite `monitorIndex` no `config.json`
- Janelas sobrepondo: habilite `topMost` e/ou WindowRouter
- Ícone da bandeja inacessível (Display cobre o monitor): use Ctrl+Alt+K/H para abrir o Controller

## Licença

Consulte `LICENSE`.
