# Guia de Instalação - Sistema NPS Kiosk

Este documento explica **tudo que você precisa instalar** no seu PC para desenvolver e usar o sistema NPS Kiosk.

---

## 📋 O Que Você Precisa Instalar

### 1. .NET 8 SDK (Obrigatório para Desenvolvimento)

**O que é:** O kit de desenvolvimento do .NET, necessário para compilar o código.

**Download:**
- Acesse: https://dotnet.microsoft.com/download/dotnet/8.0
- Escolha: **.NET 8.0 SDK (v8.x.x)**
- Plataforma: **Windows x64**
- Tamanho: ~200 MB

**Como instalar:**
1. Baixe o instalador
2. Execute o arquivo `.exe`
3. Siga as instruções (Next, Next, Install)
4. Aguarde a instalação (~5 minutos)

**Como verificar se instalou:**
Abra o PowerShell ou CMD e execute:
```bash
dotnet --version
```

Deve mostrar algo como: `8.0.x`

---

### 2. WebView2 Runtime (Obrigatório para Rodar)

**O que é:** Navegador Chromium embutido que o NpsDisplay usa para mostrar a página do NPS.

**Download:**
- Acesse: https://go.microsoft.com/fwlink/p/?LinkId=2124703
- Escolha: **Evergreen Bootstrapper**
- Tamanho: ~2 MB (baixa o resto durante instalação)

**Como instalar:**
1. Baixe o instalador `MicrosoftEdgeWebview2Setup.exe`
2. Execute o arquivo
3. Aguarde (instala automaticamente)
4. Não há tela de instalação, apenas um breve loading

**Como verificar se instalou:**
Execute no PowerShell:
```powershell
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
```

Se mostrar informações, está instalado!

⚠️ **IMPORTANTE:** Sem o WebView2, o NpsDisplay **NÃO VAI FUNCIONAR!**

---

### 3. VS Code ou Cursor (Recomendado para Desenvolvimento)

**O que é:** Editor de código para trabalhar no projeto.

**Opção A - VS Code (Gratuito):**
- Download: https://code.visualstudio.com/
- Tamanho: ~90 MB
- Após instalar, adicione a extensão: **C# Dev Kit**

**Opção B - Cursor (Gratuito com IA):**
- Download: https://cursor.sh/
- Tamanho: ~90 MB
- C# já vem configurado

**Como instalar a extensão C# Dev Kit no VS Code:**
1. Abra o VS Code
2. Clique no ícone de Extensões (ou Ctrl+Shift+X)
3. Procure por "C# Dev Kit"
4. Clique em "Install"

---

## ✅ Checklist de Instalação

Use esta lista para verificar se tudo está instalado:

### Para Desenvolvimento:
- [ ] .NET 8 SDK instalado (`dotnet --version` funciona)
- [ ] WebView2 Runtime instalado
- [ ] VS Code ou Cursor instalado
- [ ] Extensão C# Dev Kit instalada (se VS Code)

### Para Produção (apenas rodar em PCs de caixa):
- [ ] WebView2 Runtime instalado
- [ ] (Opcional) .NET 8 Runtime - não precisa se usar publish auto-contido

---

## 🚀 Depois de Instalar

### Testar se Funciona:

```bash
# 1. Entre na pasta do projeto
cd nps-kiosk

# 2. Restaure os pacotes NuGet
dotnet restore

# 3. Compile o projeto
dotnet build
```

**Resultado esperado:**
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Se você ver isso, **está tudo funcionando!** ✅

---

## 🔧 Correções Aplicadas

Os seguintes erros que você encontrou foram **corrigidos**:

### ❌ Erro 1: `InvokeAsync` não existe
**Solução:** Alterado para usar `Invoke()` que é o método correto do WinForms.

### ❌ Erro 2: `NpsConfig` não encontrado no Watchdog
**Solução:** Adicionado `using NpsShared.Models;` no arquivo.

### ❌ Erro 3: Warnings CA1416 (APIs Windows-only)
**Solução:** Adicionado `<NoWarn>CA1416</NoWarn>` no projeto NpsShared.

### ❌ Erro 4: Warning WFAC010 (DPI no manifest)
**Solução:** Movido configuração DPI para `ApplicationHighDpiMode` no projeto.

### ❌ Erro 5: Warnings CS8618 (campos nullable)
**Solução:** Marcado campos como `= null!` para suprimir avisos.

---

## 📦 Estrutura de Instalação para Produção

Quando você for instalar em um PC de caixa, a estrutura será:

```
C:\Program Files\UpMais\NpsKiosk\
├── Display\
│   └── NpsDisplay.exe          ← Mostra o NPS
├── Tray\
│   └── NpsTray.exe             ← Controle na bandeja
├── Watchdog\
│   └── NpsWatchdog.exe         ← Serviço de monitoramento
└── Router\
    └── WindowRouter.exe        ← Move janelas automaticamente

%ProgramData%\UpMais\NpsKiosk\
├── config.json                 ← Configuração
└── logs\                       ← Logs do sistema
    ├── NpsDisplay-AAAAMMDD.log
    ├── NpsTray-AAAAMMDD.log
    ├── NpsWatchdog-AAAAMMDD.log
    └── WindowRouter-AAAAMMDD.log
```

---

## 🆘 Problemas Comuns

### "dotnet não é reconhecido..."
**Causa:** .NET SDK não instalado ou não está no PATH.
**Solução:** 
1. Instale o .NET 8 SDK
2. Reinicie o terminal/PowerShell
3. Tente novamente

### "WebView2 not found"
**Causa:** WebView2 Runtime não instalado.
**Solução:**
1. Baixe: https://go.microsoft.com/fwlink/p/?LinkId=2124703
2. Execute o instalador
3. Reinicie o aplicativo

### Build demora muito
**Causa:** Primeira compilação baixa pacotes NuGet.
**Solução:** É normal! Após a primeira vez, será rápido.

### Erros de compilação
**Causa:** Versão antiga do .NET ou arquivos corrompidos.
**Solução:**
```bash
# Limpe tudo
dotnet clean

# Restaure pacotes
dotnet restore

# Compile novamente
dotnet build
```

---

## 📞 Suporte

**Logs de Erro:**
```
%ProgramData%\UpMais\NpsKiosk\logs\
```

**Arquivo de Configuração:**
```
%ProgramData%\UpMais\NpsKiosk\config.json
```

**Abrir Pastas Rapidamente:**
```powershell
# Config e logs
explorer "%ProgramData%\UpMais\NpsKiosk"
```

---

## ✨ Próximos Passos

Após instalar tudo:

1. **Leia:** `INICIO_RAPIDO.md` para começar a usar
2. **Leia:** `LEIAME.md` para documentação completa
3. **Compile:** `dotnet build` para testar
4. **Execute:** `dotnet run --project src/NpsDisplay` para ver funcionando

---

**Última Atualização:** 2025-01-09  
**Versão:** 1.0.0

