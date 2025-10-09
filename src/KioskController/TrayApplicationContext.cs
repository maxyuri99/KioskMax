using System.Diagnostics;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using Serilog;

namespace KioskController;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    private Process? _displayProcess;
    private System.Windows.Forms.Timer _statusCheckTimer;
    private NpsConfig _config;
	private SynchronizationContext? _uiContext;
	private bool _isShuttingDown;

    private ToolStripMenuItem _startStopMenuItem = null!;
    private ToolStripMenuItem _statusMenuItem = null!;
    private ToolStripMenuItem _routerMenuItem = null!;
    private ToolStripMenuItem _openControllerMenuItem = null!;
    private ControllerForm? _controllerForm;

    public TrayApplicationContext()
    {
        _config = ConfigManager.LoadConfig();
		_uiContext = SynchronizationContext.Current;
        
        // Create context menu
        _contextMenu = new ContextMenuStrip();
        BuildContextMenu();

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = TryLoadIcon(new[] { "kioskcontroller_32.png", "kioskcontroller_64.png", "kioskcontroller_256.png", "kioskcontroller_1024.png" }),
            ContextMenuStrip = _contextMenu,
            Visible = true,
            Text = "KioskMax"
        };

        _trayIcon.DoubleClick += TrayIcon_DoubleClick;

        // Status check timer
        _statusCheckTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000 // Check every 5 seconds
        };
        _statusCheckTimer.Tick += StatusCheckTimer_Tick;
        _statusCheckTimer.Start();

        Log.Information("Tray application initialized");

        // Open controller window on startup so the user sees controls
        ShowController();

        // Ensure cleanup on app exit and session end
        Application.ApplicationExit += (_, __) =>
        {
            try { CleanupAll(); } catch { }
        };
        Microsoft.Win32.SystemEvents.SessionEnding += (_, __) =>
        {
            try { CleanupAll(); } catch { }
        };

        // Auto-start WindowRouter if enabled
        if (_config.RouterEnabled)
        {
            try
            {
                StartWindowRouter();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to auto-start WindowRouter");
            }
        }

        // Auto-start display if configured
        if (_config.AutoRecover)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1000); // Small delay
                if (!await IpcClient.IsDisplayRunningAsync())
                {
                    StartDisplay();
                }
            });
        }
    }

    private void BuildContextMenu()
    {
        _statusMenuItem = new ToolStripMenuItem("Status: Checking...");
        _statusMenuItem.Enabled = false;

        _startStopMenuItem = new ToolStripMenuItem("Start Display", null, StartStopDisplay_Click);

        var changeUrlMenuItem = new ToolStripMenuItem("Change URL...", null, ChangeUrl_Click);
        var changeMonitorMenuItem = new ToolStripMenuItem("Change Monitor", null, BuildMonitorSubmenu());
        var reloadMenuItem = new ToolStripMenuItem("Reload", null, Reload_Click);

        _routerMenuItem = new ToolStripMenuItem($"Window Router: {(_config.RouterEnabled ? "ON" : "OFF")}", 
            null, ToggleRouter_Click);

        _openControllerMenuItem = new ToolStripMenuItem("Open Controller...", null, OpenController_Click);

        var openConfigMenuItem = new ToolStripMenuItem("Open Config Folder", null, OpenConfig_Click);
        var openLogsMenuItem = new ToolStripMenuItem("Open Logs Folder", null, OpenLogs_Click);
        var aboutAuthorMenuItem = new ToolStripMenuItem("Autor: Max Yuri (GitHub)", null, OpenAuthor_Click);
        var exitMenuItem = new ToolStripMenuItem("Exit", null, Exit_Click);

        _contextMenu.Items.Add(_statusMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_startStopMenuItem);
        _contextMenu.Items.Add(reloadMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_openControllerMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(changeUrlMenuItem);
        _contextMenu.Items.Add(changeMonitorMenuItem);
        _contextMenu.Items.Add(_routerMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(openConfigMenuItem);
        _contextMenu.Items.Add(openLogsMenuItem);
        _contextMenu.Items.Add(aboutAuthorMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitMenuItem);
    }

    private ToolStripMenuItem[] BuildMonitorSubmenu()
    {
        var screens = Screen.AllScreens;
        var menuItems = new List<ToolStripMenuItem>();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var index = i;
            var menuItem = new ToolStripMenuItem(
                $"Monitor {i + 1}: {screen.Bounds.Width}x{screen.Bounds.Height} {(screen.Primary ? "(Primary)" : "")}",
                null,
                async (s, e) => await SetMonitor_Click(index)
            );
            
            if (i == _config.MonitorIndex)
            {
                menuItem.Checked = true;
            }

            menuItems.Add(menuItem);
        }

        return menuItems.ToArray();
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        Log.Information("Tray icon double-click: attempting reload or start");
        Task.Run(async () =>
        {
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            Log.Information("Double-click status: IsDisplayRunning={IsRunning}", isRunning);
            if (isRunning)
            {
                await IpcClient.ReloadAsync();
                Log.Information("Sent reload to Display");
            }
            else
            {
                StartDisplay();
            }
        });
    }

    private async void StatusCheckTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateStatus();
    }

    private async Task UpdateStatus()
    {
        try
        {
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            
            if (_contextMenu.InvokeRequired)
            {
                _contextMenu.Invoke(() =>
                {
                    _statusMenuItem.Text = isRunning ? "Status: ✓ Running" : "Status: ✗ Stopped";
                    _startStopMenuItem.Text = isRunning ? "Stop Display" : "Start Display";
                });
            }
            else
            {
                _statusMenuItem.Text = isRunning ? "Status: ✓ Running" : "Status: ✗ Stopped";
                _startStopMenuItem.Text = isRunning ? "Stop Display" : "Start Display";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating status");
            try
            {
                if (_contextMenu.InvokeRequired)
                {
                    _contextMenu.Invoke(() =>
                    {
                        _statusMenuItem.Text = "Status: ✗ Stopped";
                        _startStopMenuItem.Text = "Start Display";
                    });
                }
                else
                {
                    _statusMenuItem.Text = "Status: ✗ Stopped";
                    _startStopMenuItem.Text = "Start Display";
                }
            }
            catch { }
        }
    }

    private async void StartStopDisplay_Click(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("Start/Stop clicked by user");
            SetBusy(true, "Processing...");
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            Log.Information("IsDisplayRunning={IsRunning}", isRunning);
            
            if (isRunning)
            {
                Log.Information("Invoking StopDisplay()");
                await StopDisplay();
            }
            else
            {
                Log.Information("Invoking StartDisplay()");
                StartDisplay();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error toggling display");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartDisplay()
    {
        try
        {
            Log.Information("StartDisplay(): begin");
            if (_displayProcess != null && !_displayProcess.HasExited)
            {
                Log.Warning("Display process already running");
                return;
            }

            var displayExePath = GetDisplayExecutablePath();
            Log.Information("Resolved KioskBrowser path: {Path}", displayExePath);
            
            if (!File.Exists(displayExePath))
            {
                MessageBox.Show($"KioskBrowser.exe not found at:\n{displayExePath}\n\nPlease ensure it's in the same directory.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log.Error("Display executable not found: {Path}", displayExePath);
                return;
            }

            _displayProcess = Process.Start(new ProcessStartInfo
            {
                FileName = displayExePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(displayExePath) ?? AppDomain.CurrentDomain.BaseDirectory
            });

            Log.Information("Display process started: PID {PID}", _displayProcess?.Id);
            if (_displayProcess != null)
            {
                _displayProcess.EnableRaisingEvents = true;
                _displayProcess.Exited += (s, e) =>
                {
                    try
                    {
                        var code = _displayProcess?.ExitCode;
                        Log.Warning("Display process exited quickly with code {Code}", code);
                    }
                    catch { }
                };
            }
            
            // Give it a moment to start
            Task.Delay(1000).ContinueWith(_ => UpdateStatus());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start display");
            MessageBox.Show($"Failed to start display: {ex.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopDisplay()
    {
        try
        {
            var response = await IpcClient.ShutdownAsync();
            
            if (response.Success)
            {
                Log.Information("Display stopped via IPC");
            }
            else
            {
                Log.Warning("Display shutdown command failed, attempting process kill");
                
                if (_displayProcess != null && !_displayProcess.HasExited)
                {
                    _displayProcess.Kill();
                    _displayProcess.WaitForExit(5000);
                }
            }

            await UpdateStatus();
            _displayProcess = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop display");
            throw;
        }
    }

    private async void Reload_Click(object? sender, EventArgs e)
    {
        try
        {
            SetBusy(true, "Reloading...");
            var response = await IpcClient.ReloadAsync();
            
            if (response.Success)
            {
                Log.Information("Display reloaded");
            }
            else
            {
                MessageBox.Show($"Reload failed: {response.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reload");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ChangeUrl_Click(object? sender, EventArgs e)
    {
        try
        {
            SetBusy(true, "Applying URL...");
            var input = new global::KioskController.InputDialog("Enter new NPS URL:", _config.NpsUrl);
            
            if (input.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(input.InputValue))
            {
                var newUrl = input.InputValue;
                var response = await IpcClient.SetUrlAsync(newUrl);
                
                if (response.Success)
                {
                    _config.NpsUrl = newUrl;
                    ConfigManager.SaveConfig(_config);
                    Log.Information("URL changed to: {Url}", newUrl);
                    MessageBox.Show("URL updated successfully", "Success", 
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show($"Failed to change URL: {response.Message}", "Error", 
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to change URL");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SetMonitor_Click(int monitorIndex)
    {
        try
        {
            SetBusy(true, "Changing monitor...");
            var response = await IpcClient.SetMonitorAsync(monitorIndex);
            
            if (response.Success)
            {
                _config.MonitorIndex = monitorIndex;
                ConfigManager.SaveConfig(_config);
                Log.Information("Monitor changed to index: {Index}", monitorIndex);
                
                // Rebuild menu to update checkmarks
                _contextMenu.Items.Clear();
                BuildContextMenu();
            }
            else
            {
                MessageBox.Show($"Failed to change monitor: {response.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to change monitor");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ToggleRouter_Click(object? sender, EventArgs e)
    {
        SetBusy(true, _config.RouterEnabled ? "Stopping Router..." : "Starting Router...");
        _config.RouterEnabled = !_config.RouterEnabled;
        ConfigManager.SaveConfig(_config);
        _routerMenuItem.Text = $"Window Router: {(_config.RouterEnabled ? "ON" : "OFF")}";
        Log.Information("Window Router toggled: {Enabled}", _config.RouterEnabled);

        try
        {
            if (_config.RouterEnabled)
            {
                StartWindowRouter();
                MessageBox.Show("Window Router enabled and started.", "Window Router", 
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                StopWindowRouter();
                MessageBox.Show("Window Router disabled and stopped.", "Window Router", 
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to toggle Window Router");
            MessageBox.Show($"Failed to toggle Window Router: {ex.Message}", "Window Router", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenController_Click(object? sender, EventArgs e)
    {
        ShowController();
    }

    private void ShowController()
    {
        try
        {
            if (_controllerForm == null || _controllerForm.IsDisposed)
            {
                _controllerForm = new ControllerForm();
            }
            _controllerForm.Show();
            _controllerForm.BringToFront();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show controller");
        }
    }

    private void OpenConfig_Click(object? sender, EventArgs e)
    {
        ConfigManager.OpenConfigDirectory();
    }

    private void OpenLogs_Click(object? sender, EventArgs e)
    {
        ConfigManager.OpenLogDirectory();
    }

    private void OpenAuthor_Click(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/maxyuri99/",
                UseShellExecute = true
            });
        }
        catch { }
    }

	private void Exit_Click(object? sender, EventArgs e)
    {
		Log.Information("Exit requested by user");
		BeginCleanupAndExit();
    }

    private string GetDisplayExecutablePath()
    {
        // Deriva os diretórios base tanto do runtime quanto do executável real
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
        var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

        // 1) Same directory as Tray (prefer o diretório do executável)
        var sameDirPath = Path.Combine(exeDir, "KioskBrowser.exe");
        if (File.Exists(sameDirPath)) return sameDirPath;

        // Constrói uma lista ampla de raízes subindo vários níveis
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
            // Novos layouts
            var k1 = Path.Combine(root, "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k1)) return k1;

            var k2 = Path.Combine(root, "src", "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k2)) return k2;

            var k3 = Path.Combine(root, "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k3)) return k3;

            var k4 = Path.Combine(root, "KioskBrowser", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k4)) return k4;

            var k5 = Path.Combine(root, "build", "publish", "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k5)) return k5;

            var k6 = Path.Combine(root, "temp-run", "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k6)) return k6;

            // Legado (NpsDisplay)
            var n1 = Path.Combine(root, "NpsDisplay", "NpsDisplay.exe");
            if (File.Exists(n1)) return n1;

            var n2 = Path.Combine(root, "src", "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n2)) return n2;

            var n3 = Path.Combine(root, "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n3)) return n3;

            var n4 = Path.Combine(root, "NpsDisplay", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n4)) return n4;
        }

        // Busca ampla como último recurso
        foreach (var root in rootsToSearch.Distinct())
        {
            try
            {
                var found = Directory.EnumerateFiles(root, "KioskBrowser.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found!;
            }
            catch { }
            try
            {
                var legacy = Directory.EnumerateFiles(root, "NpsDisplay.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(legacy)) return legacy!;
            }
            catch { }
        }

        return sameDirPath;
    }

    private Process? _routerProcess;

    private void StartWindowRouter()
    {
        if (_routerProcess != null && !_routerProcess.HasExited)
        {
            return;
        }

        var exePath = ResolveSiblingExecutablePath("WindowRouter.exe");
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("WindowRouter.exe not found", exePath);
        }

        _routerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false
        });
        Log.Information("WindowRouter started: PID {PID}", _routerProcess?.Id);
    }

    private void StopWindowRouter()
    {
        try
        {
            if (_routerProcess != null && !_routerProcess.HasExited)
            {
                _routerProcess.Kill();
                _routerProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop WindowRouter process cleanly");
        }
        finally
        {
            _routerProcess = null;
        }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        try
        {
            _trayIcon.Text = isBusy && !string.IsNullOrWhiteSpace(message)
                ? $"KioskMax - {message}"
                : "KioskMax";
            _contextMenu.Enabled = !isBusy;
            Cursor.Current = isBusy ? Cursors.WaitCursor : Cursors.Default;
        }
        catch { }
    }

    private string ResolveSiblingExecutablePath(string fileName)
    {
		var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
		var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
		var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

		var sameDir = Path.Combine(exeDir, fileName);
		if (File.Exists(sameDir)) return sameDir;

		// Build a comprehensive set of candidate roots by walking up ancestors
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
			// 1) Sibling layout: <root>\WindowRouter.exe or <root>\WindowRouter\WindowRouter.exe
			var s1 = Path.Combine(root, fileName);
			if (File.Exists(s1)) return s1;
			var s2 = Path.Combine(root, "WindowRouter", fileName);
			if (File.Exists(s2)) return s2;

			// 2) Dev bin layouts
			var d1 = Path.Combine(root, "WindowRouter", "bin", "Debug", "net8.0-windows", fileName);
			if (File.Exists(d1)) return d1;
			var d2 = Path.Combine(root, "src", "WindowRouter", "bin", "Debug", "net8.0-windows", fileName);
			if (File.Exists(d2)) return d2;
			var d3 = Path.Combine(root, "..", "src", "WindowRouter", "bin", "Debug", "net8.0-windows", fileName);
			var d3Full = Path.GetFullPath(d3);
			if (File.Exists(d3Full)) return d3Full;

			// 3) Published layout
			var p1 = Path.Combine(root, "build", "publish", "WindowRouter", fileName);
			if (File.Exists(p1)) return p1;
			var p2 = Path.Combine(root, "Router", fileName);
			if (File.Exists(p2)) return p2;
			var p3 = Path.Combine(root, "temp-run", "WindowRouter", fileName);
			if (File.Exists(p3)) return p3;
		}

		// Broad search fallback from the nearest plausible roots
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

    private static void TryKillByName(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch { }
            }
        }
        catch { }
    }

	private void CleanupAll()
    {
		try
		{
			// Kill known processes quickly without blocking UI
			try
			{
				if (_routerProcess != null && !_routerProcess.HasExited)
				{
					_routerProcess.Kill();
				}
			}
			catch { }
			try
			{
				if (_displayProcess != null && !_displayProcess.HasExited)
				{
					_displayProcess.Kill();
				}
			}
			catch { }

            TryKillByName("WindowRouter");
            TryKillByName("KioskBrowser");
            TryKillByName("NpsDisplay");
		}
		catch { }
    }

	private void BeginCleanupAndExit()
	{
		if (_isShuttingDown) return;
		_isShuttingDown = true;
		try { _statusCheckTimer.Stop(); } catch { }
		try { _trayIcon.Visible = false; } catch { }
		try { _contextMenu.Enabled = false; } catch { }

		Task.Run(() =>
		{
			try { CleanupAll(); } catch { }
			try
			{
				// Garante o encerramento no thread de UI mesmo se o SynchronizationContext não tiver sido capturado
				if (_contextMenu != null)
				{
					if (_contextMenu.IsHandleCreated)
					{
						_contextMenu.BeginInvoke(new Action(() => Application.Exit()));
					}
					else
					{
						Application.Exit();
					}
				}
				else
				{
					Application.Exit();
				}
			}
			catch
			{
				try { Application.Exit(); } catch { }
			}
		});

		// Fallback final: agenda um encerramento forçado por processo externo (não bloqueante)
		try
		{
            Process.Start(new ProcessStartInfo
			{
				FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Start-Sleep -Milliseconds 500; Stop-Process -Name KioskBrowser,WindowRouter,KioskController -Force -ErrorAction SilentlyContinue; Stop-Process -Name NpsDisplay -Force -ErrorAction SilentlyContinue\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
		}
		catch { }
	}

    private static Icon TryLoadIcon(string[] candidateFiles)
    {
        try
        {
            var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
            var roots = new List<string>();
            void AddAnc(string start)
            {
                try
                {
                    var d = new DirectoryInfo(start);
                    for (int i = 0; i < 8 && d != null; i++)
                    {
                        roots.Add(d.FullName);
                        d = d.Parent;
                    }
                }
                catch { }
            }
            AddAnc(runtimeBase);
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) AddAnc(Path.GetDirectoryName(exe)!);

            foreach (var root in roots.Distinct())
            {
                var iconDir = Path.Combine(root, "icones");
                if (!Directory.Exists(iconDir)) continue;
                foreach (var file in candidateFiles)
                {
                    var path = Path.Combine(iconDir, file);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        using var bmp = new Bitmap(path);
                        var h = bmp.GetHicon();
                        return Icon.FromHandle(h);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusCheckTimer?.Dispose();
            _trayIcon?.Dispose();
            _contextMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}

