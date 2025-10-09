using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using Serilog;

namespace NpsDisplay;

public partial class MainForm : Form
{
    private WebView2 _webView;
    private NpsConfig _config;
    private IpcServer? _ipcServer;
    private System.Windows.Forms.Timer _resilienceTimer;
    private Screen _targetScreen;
    private bool _isInitialized;

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
        
        // Determine target screen
        _targetScreen = DetermineTargetScreen();
        
        // Set bounds to target screen
        this.StartPosition = FormStartPosition.Manual;
        this.Bounds = _targetScreen.Bounds;
        
        // Setup WebView2 container
        _webView.Dock = DockStyle.Fill;
        this.Controls.Add(_webView);
        
        this.Text = "Kiosk Browser";
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

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            
            await _webView.EnsureCoreWebView2Async(env);
            
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

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
            
            _webView.CoreWebView2.Navigate(_config.NpsUrl);
            _isInitialized = true;
            
            Log.Information("WebView2 initialized and navigated to: {Url}", _config.NpsUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2");
            MessageBox.Show($"Failed to initialize browser: {ex.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    core.Navigate(url);
                    _config.NpsUrl = url;
                    ConfigManager.SaveConfig(_config);
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
            ConfigManager.SaveConfig(_config);
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
            ConfigManager.SaveConfig(_config);
        });

        return new IpcResponse(true, $"TopMost: {this.TopMost}");
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
        var status = new Dictionary<string, string>
        {
            { "url", _config.NpsUrl },
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
}

