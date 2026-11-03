using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using Serilog;
using NpsShared.Utilities;

namespace NpsDisplay;

public partial class MainForm : Form
{
    private WebView2 _webView;
    private NpsConfig _config;
    private IpcServer? _ipcServer;
    private System.Windows.Forms.Timer _resilienceTimer;
    private Screen _targetScreen;
    private bool _isInitialized;
    private int _secretClickCount;
    private DateTime _secretClickWindowStart;
    private string? _kioskScriptId;
    private string _paginaJson = string.Empty;
    private bool _carregada;
    private string _telaLocalAtual = string.Empty;
    private System.Windows.Forms.Timer? _timerNovaTentativa;
    private int _tentativasFalhas;
    private bool _semConexao;
    private DateTime _navegacaoIniciadaEm = DateTime.UtcNow;
    private DateTime _paginaEm = DateTime.MinValue;
    private DateTime _ultimaRecargaVigia = DateTime.MinValue;
    private int _recargasVigia;
    private string _motivoVigia = string.Empty;
    private System.Windows.Forms.Timer? _timerInicializacao;
    private int _falhasInicializacao;

    private static readonly int[] EsperaNovaTentativaSegundos = { 5, 10, 20, 30, 60 };
    private static readonly TimeSpan LimiteCarregamento = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LimitePaginaParada = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IntervaloMinimoRecargaVigia = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Sonda = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly JsonSerializerOptions JsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MainForm()
    {
        InitializeComponent();
        _webView = new WebView2();
        _config = ConfigManager.LoadConfig();
        _resilienceTimer = new System.Windows.Forms.Timer();
        _targetScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];

        InitializeForm();
        InitializeWebView();
        InitializeIpc();
        InitializeResilience();
    }

    private void InitializeForm()
    {
        // Remove border and maximize
        this.FormBorderStyle = FormBorderStyle.None;
        this.WindowState = FormWindowState.Normal; // Set to normal first
        this.TopMost = _config.TopMost;
        this.ShowInTaskbar = false;

        // Determine target screen
        _targetScreen = DetermineTargetScreen();

        // Set bounds to target screen
        this.StartPosition = FormStartPosition.Manual;
        this.Bounds = _targetScreen.Bounds;

        // Setup WebView2 container
        _webView.Dock = DockStyle.Fill;
        this.Controls.Add(_webView);

        this.Text = "Kiosk Browser";
        try
        {
            this.Icon = IconLoader.TryLoadIcon(new[] { "browser_256.png", "browser_128.png", "browser_64.png", "browser_32.png" });
        }
        catch { }
        this.BackColor = Color.Black;

        Log.Information("Form initialized on monitor: {Monitor} ({Bounds})", 
            _targetScreen.DeviceName, _targetScreen.Bounds);
    }

    private Screen DetermineTargetScreen()
    {
        var screens = Screen.AllScreens;

        if (_config.MonitorPolicy == "Primary")
        {
            return Screen.PrimaryScreen ?? screens[0];
        }

        // Explicit mode - try device ID first, then index
        if (!string.IsNullOrEmpty(_config.MonitorDeviceId))
        {
            var screenByDevice = screens.FirstOrDefault(s => 
                s.DeviceName.Equals(_config.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));
            if (screenByDevice != null)
            {
                return screenByDevice;
            }
        }

        // Fall back to index
        if (_config.MonitorIndex >= 0 && _config.MonitorIndex < screens.Length)
        {
            return screens[_config.MonitorIndex];
        }

        // Default to primary
        return Screen.PrimaryScreen ?? screens[0];
    }

    private async void InitializeWebView()
    {
        try
        {
            // Use a stable, app-owned user data folder to avoid TEMP locks between restarts
            var appDir = Path.GetDirectoryName(ConfigManager.GetConfigPath())!;
            var userDataFolder = Path.Combine(appDir, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var opcoesAmbiente = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = MontarArgumentosNavegador()
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, opcoesAmbiente);

            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // (Secret click removed per user request)

            // Recover automatically if the WebView2 process crashes
            _webView.CoreWebView2.ProcessFailed += (s, e) =>
            {
                try
                {
                    Log.Error("WebView2 process failed: {Reason}", e.Reason);
                    if (!IsDisposed)
                    {
                        BeginInvoke(new Action(RecreateWebViewAndNavigate));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error handling WebView2 ProcessFailed");
                }
            };

            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                _carregada = false;
                _paginaJson = string.Empty;
                _paginaEm = DateTime.MinValue;
                _navegacaoIniciadaEm = DateTime.UtcNow;
            };
            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (_semConexao) return;

                var paginaDeErro = _webView.CoreWebView2?.Source?.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase) == true;
                _carregada = e.IsSuccess && !paginaDeErro;
                if (_carregada)
                {
                    PararNovaTentativa();
                    return;
                }

                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                    return;

                Log.Warning("Navigation failed: {Status}", e.WebErrorStatus);
                AgendarNovaTentativa();
            };

            await RegisterKioskScriptAsync();

            if (PaginasLocais.EhTipoValido(_config.TelaLocal))
            {
                NavigateLocalPage(_config.TelaLocal, null, null, null);
                Log.Information("WebView2 initialized with local page: {Tipo}", _config.TelaLocal);
            }
            else
            {
                _telaLocalAtual = string.Empty;
                _webView.CoreWebView2.Navigate(_config.NpsUrl);
                Log.Information("WebView2 initialized and navigated to: {Url}", _config.NpsUrl);
            }
            _isInitialized = true;
            _falhasInicializacao = 0;
        }
        catch (Exception ex)
        {
            AgendarNovaInicializacao(ex);
        }
    }

    private void AgendarNovaInicializacao(Exception ex)
    {
        var espera = EsperaNovaTentativaSegundos[Math.Min(_falhasInicializacao, EsperaNovaTentativaSegundos.Length - 1)];
        _falhasInicializacao++;
        Log.Error(ex, "Failed to initialize WebView2; nova tentativa em {Segundos}s (tentativa {N})", espera, _falhasInicializacao);

        if (IsDisposed) return;
        _timerInicializacao ??= new System.Windows.Forms.Timer();
        _timerInicializacao.Stop();
        _timerInicializacao.Tick -= TimerInicializacao_Tick;
        _timerInicializacao.Tick += TimerInicializacao_Tick;
        _timerInicializacao.Interval = espera * 1000;
        _timerInicializacao.Start();
    }

    private void TimerInicializacao_Tick(object? sender, EventArgs e)
    {
        _timerInicializacao?.Stop();
        RecreateWebViewAndNavigate();
    }

    private const string ArgumentosNavegadorEconomicos = "--renderer-process-limit=1";

    private string MontarArgumentosNavegador() =>
        string.IsNullOrWhiteSpace(_config.WebViewArgumentosAdicionais)
            ? ArgumentosNavegadorEconomicos
            : $"{ArgumentosNavegadorEconomicos} {_config.WebViewArgumentosAdicionais.Trim()}";

    private void AgendarNovaTentativa()
    {
        if (_telaLocalAtual.Length > 0 || string.IsNullOrWhiteSpace(_config.NpsUrl))
            return;

        var indice = Math.Min(_tentativasFalhas, EsperaNovaTentativaSegundos.Length - 1);
        var espera = EsperaNovaTentativaSegundos[indice];
        _tentativasFalhas++;

        MostrarSemConexao(espera);

        _timerNovaTentativa ??= new System.Windows.Forms.Timer();
        _timerNovaTentativa.Stop();
        _timerNovaTentativa.Tick -= TimerNovaTentativa_Tick;
        _timerNovaTentativa.Tick += TimerNovaTentativa_Tick;
        _timerNovaTentativa.Interval = espera * 1000;
        _timerNovaTentativa.Start();
        Log.Information("Tela do cliente nao carregou; nova tentativa em {Segundos}s (tentativa {N})", espera, _tentativasFalhas);
    }

    private void MostrarSemConexao(int segundos)
    {
        var core = _webView.CoreWebView2;
        if (core == null) return;

        var nome = !string.IsNullOrWhiteSpace(_config.KioskContexto?.NmKiosk) ? _config.KioskContexto!.NmKiosk : Environment.MachineName;
        _semConexao = true;
        _carregada = false;
        core.NavigateToString(PaginasLocais.Renderizar(PaginasLocais.SemConexao, nome, _config.KioskIdentificador,
            $"Nova tentativa em {segundos} segundos"));
    }

    private async void TimerNovaTentativa_Tick(object? sender, EventArgs e)
    {
        _timerNovaTentativa?.Stop();

        try
        {
            var url = _config.NpsUrl;
            if (_webView.CoreWebView2 == null || _telaLocalAtual.Length > 0 || string.IsNullOrWhiteSpace(url))
                return;

            if (!await ServidorRespondeAsync(url))
            {
                AgendarNovaTentativa();
                return;
            }

            var core = _webView.CoreWebView2;
            if (core == null || _telaLocalAtual.Length > 0 || !_semConexao) return;
            _semConexao = false;
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao tentar recarregar a tela do cliente");
            AgendarNovaTentativa();
        }
    }

    private static async Task<bool> ServidorRespondeAsync(string url)
    {
        try
        {
            using var resposta = await Sonda.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return (int)resposta.StatusCode < 500;
        }
        catch (Exception ex)
        {
            Log.Debug("Servidor da tela ainda sem resposta: {Mensagem}", ex.Message);
            return false;
        }
    }

    private void PararNovaTentativa()
    {
        _tentativasFalhas = 0;
        _semConexao = false;
        _timerNovaTentativa?.Stop();
    }

    private void VigiarPagina()
    {
        var core = _webView.CoreWebView2;
        if (!_isInitialized || core == null || _telaLocalAtual.Length > 0 || _semConexao || _timerNovaTentativa?.Enabled == true
            || string.IsNullOrWhiteSpace(_config.NpsUrl))
            return;

        var agora = DateTime.UtcNow;
        if (agora - _ultimaRecargaVigia < IntervaloMinimoRecargaVigia) return;

        string motivo;
        if (!_carregada && agora - _navegacaoIniciadaEm > LimiteCarregamento)
            motivo = $"a página não carregou em {LimiteCarregamento.TotalSeconds:0}s";
        else if (_carregada && _paginaEm != DateTime.MinValue && agora - _paginaEm > LimitePaginaParada)
            motivo = $"a página parou de enviar o estado há {(agora - _paginaEm).TotalSeconds:0}s";
        else
            return;

        _ultimaRecargaVigia = agora;
        _recargasVigia++;
        _motivoVigia = motivo;
        Log.Warning("Vigia da tela: recarregando porque {Motivo}", motivo);
        core.Navigate(_config.NpsUrl);
    }

    private async Task RegisterKioskScriptAsync()
    {
        var core = _webView.CoreWebView2;
        if (core == null) return;

        if (_kioskScriptId != null)
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(_kioskScriptId); } catch { }
            _kioskScriptId = null;
        }

        if (_config.KioskContexto == null) return;

        var json = JsonSerializer.Serialize(_config.KioskContexto, JsonCamelCase);
        _kioskScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.upKiosk = {json};");
    }

    private void NavigateLocalPage(string tipo, string? nome, string? identificador, string? mensagem)
    {
        var core = _webView.CoreWebView2;
        if (core == null) return;

        var nomeKiosk = !string.IsNullOrWhiteSpace(nome)
            ? nome!
            : (!string.IsNullOrWhiteSpace(_config.KioskContexto?.NmKiosk) ? _config.KioskContexto!.NmKiosk : Environment.MachineName);
        var id = !string.IsNullOrWhiteSpace(identificador) ? identificador! : _config.KioskIdentificador;

        _telaLocalAtual = tipo;
        PararNovaTentativa();
        core.NavigateToString(PaginasLocais.Renderizar(tipo, nomeKiosk, id, mensagem));
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var node = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
            if (node == null) return;
            var tipo = node["tipo"]?.GetValue<string>();
            if (!string.Equals(tipo, "ESTADO", StringComparison.OrdinalIgnoreCase)) return;

            node.Remove("tipo");
            node["em"] = DateTimeOffset.Now.ToString("o");
            _paginaJson = node.ToJsonString();
            _paginaEm = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Invalid web message ignored");
        }
    }

    private void RecreateWebViewAndNavigate()
    {
        try
        {
            // Remove and dispose old control completely
            if (_webView != null)
            {
                Controls.Remove(_webView);
                _webView.Dispose();
            }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(_webView);
            _isInitialized = false;

            InitializeWebView();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to recreate WebView2 control");
        }
    }

    private void InitializeIpc()
    {
        try
        {
            _ipcServer = new IpcServer(HandleIpcCommand);
            _ipcServer.Start();
            Log.Information("IPC Server started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start IPC server");
        }
    }

    private void InitializeResilience()
    {
        _resilienceTimer.Interval = 3000; // Check every 3 seconds
        _resilienceTimer.Tick += ResilienceTimer_Tick;
        _resilienceTimer.Start();
        Log.Information("Resilience timer started");
    }

    private void ResilienceTimer_Tick(object? sender, EventArgs e)
    {
        if (!_config.AutoRecover) return;

        try
        {
            // Ensure TopMost
            if (this.TopMost != _config.TopMost)
            {
                this.TopMost = _config.TopMost;
                Log.Warning("TopMost state was changed, reapplied");
            }

            // Ensure correct screen bounds
            if (this.Bounds != _targetScreen.Bounds)
            {
                this.Bounds = _targetScreen.Bounds;
                Log.Warning("Window bounds changed, reapplied");
            }

            // Check if WebView2 is still healthy
            if (_isInitialized && _webView.CoreWebView2 == null)
            {
                Log.Error("WebView2 crashed, attempting to reinitialize");
                InitializeWebView();
            }

            VigiarPagina();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in resilience timer");
        }
    }

    private async Task<IpcResponse> HandleIpcCommand(IpcCommand command)
    {
        try
        {
            Log.Information("Handling IPC command: {CommandType}", command.CommandType);

            return command.CommandType switch
            {
                IpcCommandType.Ping => await NaInterfaceAsync(() => new IpcResponse(true, "Pong")),

                IpcCommandType.Reload => await HandleReloadCommand(),

                IpcCommandType.SetUrl => await HandleSetUrlCommand(command),

                IpcCommandType.SetMonitor => await HandleSetMonitorCommand(command),

                IpcCommandType.ToggleTopMost => await HandleToggleTopMostCommand(),

                IpcCommandType.Shutdown => await HandleShutdownCommand(),

                IpcCommandType.GetStatus => await HandleGetStatusCommand(),

                IpcCommandType.SetKioskContext => await HandleSetKioskContextCommand(command),

                IpcCommandType.NavigateLocal => await HandleNavigateLocalCommand(command),

                IpcCommandType.PostToPage => await HandlePostToPageCommand(command),

                _ => new IpcResponse(false, "Unknown command")
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling IPC command: {CommandType}", command.CommandType);
            return new IpcResponse(false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the action on the UI thread without blocking the IPC thread (BeginInvoke), so a frozen UI only
    /// makes the IPC server answer with a timeout instead of hanging the agent.
    /// </summary>
    private Task<T> NaInterfaceAsync<T>(Func<T> acao)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    tcs.TrySetResult(acao());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    private Task<IpcResponse> HandleReloadCommand()
    {
        return NaInterfaceAsync(() =>
        {
            var core = _webView.CoreWebView2;
            if (core == null) return new IpcResponse(false, "WebView2 not initialized");

            if (_semConexao || (_telaLocalAtual.Length == 0 && !_carregada && !string.IsNullOrWhiteSpace(_config.NpsUrl)))
            {
                PararNovaTentativa();
                core.Navigate(_config.NpsUrl);
            }
            else
            {
                core.Reload();
            }
            return new IpcResponse(true, "Reloaded");
        });
    }

    private Task<IpcResponse> HandleSetUrlCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("url", out var url))
        {
            return Task.FromResult(new IpcResponse(false, "Missing 'url' parameter"));
        }

        return NaInterfaceAsync(() =>
        {
            var core = _webView.CoreWebView2;
            if (core == null) return new IpcResponse(false, "WebView2 not initialized");

            _telaLocalAtual = string.Empty;
            _config.NpsUrl = url;
            _config.TelaLocal = string.Empty;
            PararNovaTentativa();
            core.Navigate(url);
            return new IpcResponse(true, $"URL changed to: {url}");
        });
    }

    private Task<IpcResponse> HandleSetMonitorCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("monitorIndex", out var indexStr) ||
            !int.TryParse(indexStr, out var monitorIndex))
        {
            return Task.FromResult(new IpcResponse(false, "Invalid 'monitorIndex' parameter"));
        }

        return NaInterfaceAsync(() =>
        {
            var screens = Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                return new IpcResponse(false, $"Invalid monitor index. Available: 0-{screens.Length - 1}");
            }

            _targetScreen = screens[monitorIndex];
            this.Bounds = _targetScreen.Bounds;
            _config.MonitorIndex = monitorIndex;
            Log.Information("Switched to monitor {Index}: {Device}", monitorIndex, _targetScreen.DeviceName);
            return new IpcResponse(true, $"Switched to monitor {monitorIndex}");
        });
    }

    private Task<IpcResponse> HandleToggleTopMostCommand()
    {
        return NaInterfaceAsync(() =>
        {
            this.TopMost = !this.TopMost;
            _config.TopMost = this.TopMost;
            return new IpcResponse(true, $"TopMost: {this.TopMost}");
        });
    }

    private async Task<IpcResponse> HandleSetKioskContextCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("json", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return new IpcResponse(false, "Missing 'json' parameter");
        }

        var contexto = JsonSerializer.Deserialize<KioskContexto>(json, JsonCamelCase);
        if (contexto == null)
        {
            return new IpcResponse(false, "Invalid kiosk context");
        }

        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try
            {
                _config.KioskContexto = contexto;
                await RegisterKioskScriptAsync();

                var core = _webView.CoreWebView2;
                if (core != null)
                {
                    var payload = JsonSerializer.SerializeToNode(contexto, JsonCamelCase) as JsonObject ?? new JsonObject();
                    payload["tipo"] = "KIOSK";
                    core.PostWebMessageAsJson(payload.ToJsonString());
                }

                tcs.TrySetResult(new IpcResponse(true, "Kiosk context applied"));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply kiosk context");
                tcs.TrySetResult(new IpcResponse(false, $"Error: {ex.Message}"));
            }
        }));

        return await tcs.Task;
    }

    private Task<IpcResponse> HandleNavigateLocalCommand(IpcCommand command)
    {
        command.Parameters.TryGetValue("tipo", out var tipo);
        command.Parameters.TryGetValue("html", out var html);
        command.Parameters.TryGetValue("nome", out var nome);
        command.Parameters.TryGetValue("identificador", out var identificador);
        command.Parameters.TryGetValue("mensagem", out var mensagem);

        if (string.IsNullOrWhiteSpace(html) && !PaginasLocais.EhTipoValido(tipo))
        {
            return Task.FromResult(new IpcResponse(false, "Missing 'html' or valid 'tipo' parameter"));
        }

        return NaInterfaceAsync(() =>
        {
            var core = _webView.CoreWebView2;
            if (core == null) return new IpcResponse(false, "WebView2 not initialized");

            if (!string.IsNullOrWhiteSpace(html))
            {
                _telaLocalAtual = "HTML";
                PararNovaTentativa();
                core.NavigateToString(html);
            }
            else
            {
                NavigateLocalPage(tipo!, nome, identificador, mensagem);
            }
            return new IpcResponse(true, "Local page shown");
        });
    }

    private Task<IpcResponse> HandlePostToPageCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("json", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return Task.FromResult(new IpcResponse(false, "Missing 'json' parameter"));
        }

        return NaInterfaceAsync(() =>
        {
            var core = _webView.CoreWebView2;
            if (core == null) return new IpcResponse(false, "WebView2 not initialized");
            core.PostWebMessageAsJson(json);
            return new IpcResponse(true, "Posted");
        });
    }

    private Task<IpcResponse> HandleShutdownCommand()
    {
        return NaInterfaceAsync(() =>
        {
            Log.Information("Shutdown requested via IPC");
            BeginInvoke(new Action(Application.Exit));
            return new IpcResponse(true, "Shutting down");
        });
    }

    private Task<IpcResponse> HandleGetStatusCommand()
    {
        return NaInterfaceAsync(() =>
        {
            var status = new Dictionary<string, string>
            {
                { "url", _config.NpsUrl },
                { "currentUrl", _semConexao ? string.Empty : _webView.CoreWebView2?.Source ?? string.Empty },
                { "local", _semConexao ? PaginasLocais.SemConexao : _telaLocalAtual },
                { "carregada", _carregada.ToString() },
                { "paginaJson", _paginaJson },
                { "monitor", _targetScreen.DeviceName },
                { "monitorIndex", Array.IndexOf(Screen.AllScreens, _targetScreen).ToString() },
                { "topMost", this.TopMost.ToString() },
                { "initialized", _isInitialized.ToString() },
                { "recargasVigia", _recargasVigia.ToString() },
                { "motivoVigia", _motivoVigia }
            };

            return new IpcResponse(true, "OK", status);
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _resilienceTimer?.Stop();
        _timerNovaTentativa?.Stop();
        _timerInicializacao?.Stop();
        _ipcServer?.Dispose();
        _webView?.Dispose();
        base.OnFormClosing(e);
    }

    // Prevent Alt+F4
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Alt && e.KeyCode == Keys.F4)
        {
            e.Handled = true;
            Log.Debug("Alt+F4 blocked");
        }
        base.OnKeyDown(e);
    }

    private void TryShowController()
    {
        try
        {
            if (TryBringExistingControllerToFront())
                return;

            // Start Controller if not running
            var exePath = ResolveSiblingExecutablePath("KioskController.exe");
            if (!File.Exists(exePath))
            {
                Log.Warning("KioskController.exe not found at: {Path}", exePath);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false
            });

            // Give it a moment and attempt to bring to front
            Task.Delay(800).ContinueWith(_ => TryBringExistingControllerToFront());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show controller");
        }
    }

    private bool TryBringExistingControllerToFront()
    {
        try
        {
            // Try by window title first
            var hWnd = FindWindow(null, "KioskMax - Controller");
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, 9); // SW_RESTORE
                SetForegroundWindow(hWnd);
                return true;
            }

            // Fallback: search process by name and try main window
            foreach (var p in Process.GetProcessesByName("KioskController"))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private string ResolveSiblingExecutablePath(string fileName)
    {
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
        var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

        var sameDir = Path.Combine(exeDir, fileName);
        if (File.Exists(sameDir)) return sameDir;

        var rootsToSearch = new List<string>();
        void AddAncestors(string start)
        {
            try
            {
                var current = new DirectoryInfo(start);
                for (int i = 0; i < 8 && current != null; i++)
                {
                    rootsToSearch.Add(current.FullName);
                    current = current.Parent;
                }
            }
            catch { }
        }
        AddAncestors(exeDir);
        AddAncestors(runtimeBase);

        foreach (var root in rootsToSearch.Distinct())
        {
            var s1 = Path.Combine(root, fileName);
            if (File.Exists(s1)) return s1;
            var s2 = Path.Combine(root, "KioskController", fileName);
            if (File.Exists(s2)) return s2;
            var d1 = Path.Combine(root, "KioskController", "bin", "Debug", "net8.0-windows", fileName);
            if (File.Exists(d1)) return d1;
            var d2 = Path.Combine(root, "src", "KioskController", "bin", "Debug", "net8.0-windows", fileName);
            if (File.Exists(d2)) return d2;
            var p1 = Path.Combine(root, "build", "publish", "KioskController", fileName);
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(root, "temp-run", "KioskController", fileName);
            if (File.Exists(p2)) return p2;
        }

        foreach (var root in rootsToSearch.Distinct())
        {
            try
            {
                var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found!;
            }
            catch { }
        }

        return sameDir;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

