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

    private static readonly int[] EsperaNovaTentativaSegundos = { 5, 10, 20, 30 };

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
            };
            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2");
            MessageBox.Show($"Failed to initialize browser: {ex.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

        _timerNovaTentativa ??= new System.Windows.Forms.Timer();
        _timerNovaTentativa.Stop();
        _timerNovaTentativa.Tick -= TimerNovaTentativa_Tick;
        _timerNovaTentativa.Tick += TimerNovaTentativa_Tick;
        _timerNovaTentativa.Interval = espera * 1000;
        _timerNovaTentativa.Start();
        Log.Information("Tela do cliente nao carregou; nova tentativa em {Segundos}s (tentativa {N})", espera, _tentativasFalhas);
    }

    private void TimerNovaTentativa_Tick(object? sender, EventArgs e)
    {
        _timerNovaTentativa?.Stop();

        try
        {
            var core = _webView.CoreWebView2;
            if (core == null || _carregada || _telaLocalAtual.Length > 0 || string.IsNullOrWhiteSpace(_config.NpsUrl))
                return;

            core.Navigate(_config.NpsUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao tentar recarregar a tela do cliente");
            AgendarNovaTentativa();
        }
    }

    private void PararNovaTentativa()
    {
        _tentativasFalhas = 0;
        _timerNovaTentativa?.Stop();
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
                IpcCommandType.Ping => new IpcResponse(true, "Pong"),

                IpcCommandType.Reload => await HandleReloadCommand(),

                IpcCommandType.SetUrl => await HandleSetUrlCommand(command),

                IpcCommandType.SetMonitor => HandleSetMonitorCommand(command),

                IpcCommandType.ToggleTopMost => HandleToggleTopMostCommand(),

                IpcCommandType.Shutdown => HandleShutdownCommand(),

                IpcCommandType.GetStatus => HandleGetStatusCommand(),

                IpcCommandType.SetKioskContext => await HandleSetKioskContextCommand(command),

                IpcCommandType.NavigateLocal => HandleNavigateLocalCommand(command),

                IpcCommandType.PostToPage => HandlePostToPageCommand(command),

                _ => new IpcResponse(false, "Unknown command")
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling IPC command: {CommandType}", command.CommandType);
            return new IpcResponse(false, $"Error: {ex.Message}");
        }
    }

    private Task<IpcResponse> HandleReloadCommand()
    {
        bool hasCore = false;
        try
        {
            Invoke(() =>
            {
                var core = _webView.CoreWebView2;
                hasCore = core != null;
                if (core != null)
                {
                    core.Reload();
                }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new IpcResponse(false, $"Error: {ex.Message}"));
        }

        return Task.FromResult(hasCore
            ? new IpcResponse(true, "Reloaded")
            : new IpcResponse(false, "WebView2 not initialized"));
    }

    private Task<IpcResponse> HandleSetUrlCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("url", out var url))
        {
            return Task.FromResult(new IpcResponse(false, "Missing 'url' parameter"));
        }

        bool hasCore = false;
        try
        {
            Invoke(() =>
            {
                var core = _webView.CoreWebView2;
                hasCore = core != null;
                if (core != null)
                {
                    _telaLocalAtual = string.Empty;
                    core.Navigate(url);
                    _config.NpsUrl = url;
                    _config.TelaLocal = string.Empty;
                    ConfigManager.Update(c =>
                    {
                        c.NpsUrl = url;
                        c.TelaLocal = string.Empty;
                    });
                }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new IpcResponse(false, $"Error: {ex.Message}"));
        }

        return Task.FromResult(hasCore
            ? new IpcResponse(true, $"URL changed to: {url}")
            : new IpcResponse(false, "WebView2 not initialized"));
    }

    private IpcResponse HandleSetMonitorCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("monitorIndex", out var indexStr) ||
            !int.TryParse(indexStr, out var monitorIndex))
        {
            return new IpcResponse(false, "Invalid 'monitorIndex' parameter");
        }

        var screens = Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length)
        {
            return new IpcResponse(false, $"Invalid monitor index. Available: 0-{screens.Length - 1}");
        }

        Invoke(() =>
        {
            _targetScreen = screens[monitorIndex];
            this.Bounds = _targetScreen.Bounds;
            _config.MonitorIndex = monitorIndex;
            ConfigManager.Update(c => c.MonitorIndex = monitorIndex);
            Log.Information("Switched to monitor {Index}: {Device}", monitorIndex, _targetScreen.DeviceName);
        });

        return new IpcResponse(true, $"Switched to monitor {monitorIndex}");
    }

    private IpcResponse HandleToggleTopMostCommand()
    {
        Invoke(() =>
        {
            this.TopMost = !this.TopMost;
            _config.TopMost = this.TopMost;
            ConfigManager.Update(c => c.TopMost = _config.TopMost);
        });

        return new IpcResponse(true, $"TopMost: {this.TopMost}");
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

        var tcs = new TaskCompletionSource<IpcResponse>();
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

    private IpcResponse HandleNavigateLocalCommand(IpcCommand command)
    {
        command.Parameters.TryGetValue("tipo", out var tipo);
        command.Parameters.TryGetValue("html", out var html);
        command.Parameters.TryGetValue("nome", out var nome);
        command.Parameters.TryGetValue("identificador", out var identificador);
        command.Parameters.TryGetValue("mensagem", out var mensagem);

        if (string.IsNullOrWhiteSpace(html) && !PaginasLocais.EhTipoValido(tipo))
        {
            return new IpcResponse(false, "Missing 'html' or valid 'tipo' parameter");
        }

        var hasCore = false;
        Invoke(() =>
        {
            var core = _webView.CoreWebView2;
            hasCore = core != null;
            if (core == null) return;

            if (!string.IsNullOrWhiteSpace(html))
            {
                _telaLocalAtual = "HTML";
                core.NavigateToString(html);
            }
            else
            {
                NavigateLocalPage(tipo!, nome, identificador, mensagem);
            }
        });

        return hasCore
            ? new IpcResponse(true, "Local page shown")
            : new IpcResponse(false, "WebView2 not initialized");
    }

    private IpcResponse HandlePostToPageCommand(IpcCommand command)
    {
        if (!command.Parameters.TryGetValue("json", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return new IpcResponse(false, "Missing 'json' parameter");
        }

        var hasCore = false;
        Invoke(() =>
        {
            var core = _webView.CoreWebView2;
            hasCore = core != null;
            core?.PostWebMessageAsJson(json);
        });

        return hasCore
            ? new IpcResponse(true, "Posted")
            : new IpcResponse(false, "WebView2 not initialized");
    }

    private IpcResponse HandleShutdownCommand()
    {
        Invoke(() =>
        {
            Log.Information("Shutdown requested via IPC");
            Application.Exit();
        });

        return new IpcResponse(true, "Shutting down");
    }

    private IpcResponse HandleGetStatusCommand()
    {
        var currentUrl = string.Empty;
        try
        {
            Invoke(() => currentUrl = _webView.CoreWebView2?.Source ?? string.Empty);
        }
        catch { }

        var status = new Dictionary<string, string>
        {
            { "url", _config.NpsUrl },
            { "currentUrl", currentUrl },
            { "local", _telaLocalAtual },
            { "carregada", _carregada.ToString() },
            { "paginaJson", _paginaJson },
            { "monitor", _targetScreen.DeviceName },
            { "monitorIndex", Array.IndexOf(Screen.AllScreens, _targetScreen).ToString() },
            { "topMost", this.TopMost.ToString() },
            { "initialized", _isInitialized.ToString() }
        };

        return new IpcResponse(true, "OK", status);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _resilienceTimer?.Stop();
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

