using System.Diagnostics;
using Serilog;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;

namespace NpsTray;

public partial class ControllerForm : Form
{
    private readonly System.Windows.Forms.Timer _statusTimer;
    private NpsConfig _config;
    private bool _lastIsDisplayRunning;

    public ControllerForm()
    {
        InitializeComponent();

        _config = ConfigManager.LoadConfig();

        txtUrl.Text = _config.NpsUrl;
        chkRouter.Checked = _config.RouterEnabled;

        PopulateMonitors();

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000
        };
        _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
        _statusTimer.Start();

        Shown += async (_, _) => await UpdateStatusAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Prevent accidental exit via 'X' while components are running
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Evita deadlock: não chamar async .GetResult() no thread de UI
            var displayRunning = _lastIsDisplayRunning;
            var routerRunning = false;
            try { routerRunning = Process.GetProcessesByName("WindowRouter").Length > 0; } catch { }

            if (displayRunning || routerRunning)
            {
                var result = MessageBox.Show(
                    "O Kiosk Browser ou o WindowRouter estão em execução.\n\n" +
                    "Sim = Sair e encerrar tudo\nNão = Minimizar para a bandeja\nCancelar = Voltar", 
                    "KioskMax", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                // Cancel default close behavior
                e.Cancel = true;

                if (result == DialogResult.Yes)
                {
                    // Exit entire app; cleanup será feito pelo contexto
                    this.Hide();
                    BeginInvoke(new Action(() => Application.Exit()));
                    return;
                }
                if (result == DialogResult.No)
                {
                    // Just hide to tray
                    this.Hide();
                    return;
                }
                // Cancel = do nothing
                return;
            }

            // Default: hide instead of close
            e.Cancel = true;
            this.Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            _lastIsDisplayRunning = isRunning;
            lblStatus.Text = isRunning ? "Status: ✓ Running" : "Status: ✗ Stopped";
            btnStartStop.Text = isRunning ? "Stop" : "Start";
        }
        catch
        {
            _lastIsDisplayRunning = false;
            lblStatus.Text = "Status: ✗ Stopped";
            btnStartStop.Text = "Start";
        }
    }

    private void PopulateMonitors()
    {
        cmbMonitors.Items.Clear();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = $"Monitor {i + 1}: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (Primary)" : string.Empty)}";
            cmbMonitors.Items.Add(label);
        }

        if (_config.MonitorIndex >= 0 && _config.MonitorIndex < screens.Length)
        {
            cmbMonitors.SelectedIndex = _config.MonitorIndex;
        }
        else if (screens.Length > 0)
        {
            cmbMonitors.SelectedIndex = 0;
        }
    }

    private void btnOpenLogs_Click(object sender, EventArgs e)
    {
        ConfigManager.OpenLogDirectory();
    }

    private void btnOpenConfig_Click(object sender, EventArgs e)
    {
        ConfigManager.OpenConfigDirectory();
    }

    private async void btnStartStop_Click(object sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            btnStartStop.Enabled = false;
            btnReload.Enabled = false;
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            if (isRunning)
            {
                var result = await IpcClient.ShutdownAsync();
                if (!result.Success)
                {
                    MessageBox.Show($"Shutdown failed: {result.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                StartDisplayProcess();
            }

            await Task.Delay(800);
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnStartStop.Enabled = true;
            btnReload.Enabled = true;
        }
    }

    private async void btnReload_Click(object sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            btnStartStop.Enabled = false;
            btnReload.Enabled = false;
            var response = await IpcClient.ReloadAsync();
            if (!response.Success)
            {
                MessageBox.Show($"Reload failed: {response.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnStartStop.Enabled = true;
            btnReload.Enabled = true;
        }
    }

    private async void btnApplyUrl_Click(object sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            btnApplyUrl.Enabled = false;
            var newUrl = txtUrl.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newUrl))
            {
                MessageBox.Show("URL cannot be empty", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var isRunning = await IpcClient.IsDisplayRunningAsync();
            if (isRunning)
            {
                var response = await IpcClient.SetUrlAsync(newUrl);
                if (!response.Success)
                {
                    MessageBox.Show($"Failed to set URL: {response.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    _config.NpsUrl = newUrl;
                    ConfigManager.SaveConfig(_config);
                }
            }
            else
            {
                _config.NpsUrl = newUrl;
                ConfigManager.SaveConfig(_config);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnApplyUrl.Enabled = true;
        }
    }

    private async void cmbMonitors_SelectedIndexChanged(object sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            cmbMonitors.Enabled = false;
            var index = cmbMonitors.SelectedIndex;
            if (index < 0) return;

            var isRunning = await IpcClient.IsDisplayRunningAsync();
            if (isRunning)
            {
                var response = await IpcClient.SetMonitorAsync(index);
                if (!response.Success)
                {
                    MessageBox.Show($"Failed to set monitor: {response.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    _config.MonitorIndex = index;
                    ConfigManager.SaveConfig(_config);
                }
            }
            else
            {
                // Persist locally for próxima inicialização do Display
                _config.MonitorIndex = index;
                ConfigManager.SaveConfig(_config);
                await UpdateStatusAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            cmbMonitors.Enabled = true;
        }
    }

    private void chkRouter_CheckedChanged(object sender, EventArgs e)
    {
        _config.RouterEnabled = chkRouter.Checked;
        ConfigManager.SaveConfig(_config);
    }

    private void StartDisplayProcess()
    {
        var exePath = GetDisplayExecutablePath();
        if (!File.Exists(exePath))
        {
            MessageBox.Show($"KioskBrowser.exe not found at:\n{exePath}\n\nPlease ensure it's in the same directory.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Log.Information("Controller Start: resolved KioskBrowser path: {Path}", exePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Controller failed to start display");
            MessageBox.Show($"Failed to start display: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetDisplayExecutablePath()
    {
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
        var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

        var sameDirPath = Path.Combine(exeDir, "KioskBrowser.exe");
        if (File.Exists(sameDirPath)) return sameDirPath;

        // Build a comprehensive set of roots by walking up several parents
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

        // Try common dev and temp-run layouts under each root
        foreach (var root in rootsToSearch.Distinct())
        {
            // new layouts
            var k1 = Path.Combine(root, "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k1)) return k1;

            var k2 = Path.Combine(root, "src", "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k2)) return k2;

            var k3 = Path.Combine(root, "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k3)) return k3;

            var k4 = Path.Combine(root, "KioskBrowser", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k4)) return k4;

            // legacy layouts
            var n1 = Path.Combine(root, "NpsDisplay", "NpsDisplay.exe");
            if (File.Exists(n1)) return n1;

            var n2 = Path.Combine(root, "src", "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n2)) return n2;

            var n3 = Path.Combine(root, "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n3)) return n3;

            var n4 = Path.Combine(root, "NpsDisplay", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n4)) return n4;

            // broad search fallback (limited to each root subtree)
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
}


