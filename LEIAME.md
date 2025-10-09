# Sistema NPS Kiosk

Uma solução completa de kiosque para exibir pesquisas NPS (Net Promoter Score) em um monitor touchscreen dedicado em ambientes de varejo/caixa.

## 🎯 Recursos

- **Display NPS em Tela Cheia**: Display baseado em WebView2 dedicado e travado em um monitor específico
- **Sempre Visível**: Garante que o NPS permaneça visível acima de todas as outras janelas
- **Suporte Multi-Monitor**: Direcionamento inteligente de monitor (por índice, ID do dispositivo ou primário)
- **Controlador na Bandeja do Sistema**: Gerenciamento fácil do display, URL e configurações
- **Auto-Recuperação**: Reinicia automaticamente se o display travar ou parar
- **Roteador de Janelas**: Move automaticamente outras janelas para longe da tela NPS
- **Comunicação IPC**: Named pipes para comunicação confiável entre processos
- **DPI Aware**: Renderização adequada em configurações mistas de DPI/escala (PerMonitorV2)

## 📦 Componentes

### 1. NpsDisplay (WinForms + WebView2)
A aplicação principal que mostra a URL do NPS em modo tela cheia.

**Recursos:**
- Janela sem bordas, tela cheia
- Sempre no topo (TopMost)
- WebView2 com controles de navegação desabilitados
- Servidor IPC para controle remoto
- Auto-recuperação de crashes
- Imposição dos limites do monitor

### 2. NpsTray (App na Bandeja do Sistema)
O centro de controle para gerenciar o sistema kiosk.

**Recursos:**
- Iniciar/Parar NpsDisplay
- Alterar URL em tempo real
- Alternar entre monitores
- Recarregar navegador
- Ativar/desativar Roteador de Janelas
- Abrir pastas de config e logs
- Monitoramento de status

### 3. NpsWatchdog (Serviço Windows)
Serviço opcional para resiliência de nível empresarial.

**Recursos:**
- Monitora a saúde do NpsDisplay via ping IPC
- Reinicia automaticamente se travado ou com crash
- Intervalos de verificação configuráveis
- Limitação de reinicializações para evitar loops

### 4. WindowRouter (Processo Auxiliar)
Processo opcional que mantém o monitor NPS "limpo".

**Recursos:**
- Usa hooks WinEvent para detectar novas janelas
- Move automaticamente janelas não-NPS para o monitor do operador
- Exceções configuráveis
- Baixo overhead

## 🚀 Início Rápido

### Pré-requisitos

1. **Windows 10/11** (x64)
2. **.NET 8 SDK** (para desenvolvimento) ou **.NET 8 Runtime** (para implantação)
3. **WebView2 Runtime** (Evergreen) - [Baixe aqui](https://developer.microsoft.com/microsoft-edge/webview2/)
4. **VS Code** ou **Cursor** com extensões C# (para desenvolvimento)

### Compilando

```bash
# Clone/navegue até o repositório
cd nps-kiosk

# Restaurar pacotes
dotnet restore

# Compilar todos os projetos
dotnet build --configuration Release

# Ou compilar individualmente
dotnet build src/NpsDisplay/NpsDisplay.csproj -c Release
dotnet build src/NpsTray/NpsTray.csproj -c Release
dotnet build src/NpsWatchdog/NpsWatchdog.csproj -c Release
dotnet build src/WindowRouter/WindowRouter.csproj -c Release
```

### Publicando (Auto-contido)

Para implantação sem requerer instalação do .NET nas máquinas de destino:

```bash
# NpsDisplay
dotnet publish src/NpsDisplay/NpsDisplay.csproj -c Release -r win-x64 ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o build/publish/NpsDisplay

# NpsTray
dotnet publish src/NpsTray/NpsTray.csproj -c Release -r win-x64 ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o build/publish/NpsTray

# NpsWatchdog
dotnet publish src/NpsWatchdog/NpsWatchdog.csproj -c Release -r win-x64 ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o build/publish/NpsWatchdog

# WindowRouter
dotnet publish src/WindowRouter/WindowRouter.csproj -c Release -r win-x64 ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o build/publish/WindowRouter
```

### Instalação

1. **Copiar Arquivos**:
   ```
   C:\Program Files\UpMais\NpsKiosk\
   ├── Display\
   │   └── NpsDisplay.exe
   ├── Tray\
   │   └── NpsTray.exe
   ├── Watchdog\
   │   └── NpsWatchdog.exe
   └── Router\
       └── WindowRouter.exe
   ```

2. **Garantir que o WebView2 está instalado** (necessário para NpsDisplay)

3. **Criar Tarefa Agendada** para o NpsTray iniciar automaticamente no logon:
   ```powershell
   # Veja build/scripts/install-scheduled-task.ps1
   ```

4. **(Opcional) Instalar Serviço Watchdog**:
   ```powershell
   # Veja build/scripts/install-watchdog-service.ps1
   ```

## ⚙️ Configuração

A configuração é armazenada em:
```
%ProgramData%\UpMais\NpsKiosk\config.json
```

**Configuração Padrão:**
```json
{
  "npsUrl": "https://nps.upmais.com.br/display",
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

**Opções de Configuração:**

- `npsUrl`: A URL para exibir no kiosque
- `monitorPolicy`: "Explicit" ou "Primary"
- `monitorDeviceId`: Nome específico do dispositivo do monitor (ex: "\\\\.\\DISPLAY1")
- `monitorIndex`: Índice do monitor (0 = primeiro, 1 = segundo, etc.)
- `topMost`: Manter janela do display sempre no topo
- `kioskFullScreen`: Habilitar modo kiosk tela cheia
- `autoRecover`: Habilitar recursos de recuperação automática
- `dpiMode`: Modo de reconhecimento DPI (PerMonitorV2 recomendado)
- `routerEnabled`: Habilitar funcionalidade Roteador de Janelas
- `routerTarget`: "Opposite" ou índice de monitor explícito
- `logLevel`: Verbosidade dos logs (Debug, Information, Warning, Error)

## 📋 Uso

### Operações Diárias

1. **Sistema inicia** → Tarefa Agendada inicia NpsTray automaticamente
2. NpsTray verifica se NpsDisplay está rodando, inicia se necessário
3. NpsDisplay abre no monitor touch configurado em tela cheia
4. Roteador de Janelas (se habilitado) move outras janelas para tela do operador
5. Use o ícone da bandeja do sistema para gerenciar o display

### Opções do Menu da Bandeja

- **Status**: Mostra se NpsDisplay está rodando
- **Iniciar/Parar Display**: Liga/desliga o display
- **Recarregar**: Atualiza a página web
- **Alterar URL**: Atualiza a URL do NPS
- **Alterar Monitor**: Seleciona qual monitor usar
- **Roteador de Janelas**: Liga/desliga roteamento automático
- **Abrir Pasta Config**: Acessa arquivos de configuração
- **Abrir Pasta Logs**: Visualiza logs da aplicação
- **Sair**: Fecha a aplicação da bandeja

## 🔧 Solução de Problemas

### Display não inicia
- Verifique se o WebView2 Runtime está instalado
- Verifique os logs em `%ProgramData%\UpMais\NpsKiosk\logs\`
- Verifique o caminho do NpsDisplay.exe no NpsTray

### Display aparece no monitor errado
- Use o menu da bandeja: "Alterar Monitor"
- Ou edite `config.json` e defina o `monitorIndex` correto
- Verifique as conexões dos monitores antes de iniciar

### Outras janelas aparecem sobre o NPS
- Garanta que `topMost` está como `true` no config
- Habilite o Roteador de Janelas para roteamento automático
- Verifique se o timer de resiliência está funcionando

### WebView2 mostra erro
- Instale/atualize o WebView2 Runtime
- Verifique conectividade com internet para a URL do NPS
- Revise os logs para erros de navegação

## 📁 Estrutura do Projeto

```
nps-kiosk/
├── src/
│   ├── NpsDisplay/          # Aplicação display principal
│   ├── NpsTray/             # Controlador bandeja sistema
│   ├── NpsWatchdog/         # Serviço Windows
│   ├── WindowRouter/        # Auxiliar roteamento janelas
│   └── NpsShared/           # Biblioteca compartilhada
├── build/
│   ├── publish/             # Executáveis publicados
│   └── scripts/             # Scripts de instalação
├── config/
│   └── default.config.json  # Configuração padrão
└── LEIAME.md
```

## 📝 Logs

Os logs são armazenados em:
```
%ProgramData%\UpMais\NpsKiosk\logs\
├── NpsDisplay-AAAAMMDD.log
├── NpsTray-AAAAMMDD.log
├── NpsWatchdog-AAAAMMDD.log
└── WindowRouter-AAAAMMDD.log
```

Os logs rotacionam diariamente e retêm 7-30 dias dependendo do componente.

## 📄 Licença

Copyright © 2025 UpMais. Todos os direitos reservados.

## 🤝 Suporte

Para problemas ou questões:
- Verifique os logs primeiro
- Revise este LEIAME
- Contato: [seu-email-suporte]

---

**Versão**: 1.0.0  
**Última Atualização**: 2025-01-09

