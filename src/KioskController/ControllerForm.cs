using System.Diagnostics;
using Serilog;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using NpsShared.Utilities;

namespace KioskController;

public partial class ControllerForm : Form
{
    private readonly System.Windows.Forms.Timer _statusTimer;
    private NpsConfig _config;
    private bool _lastIsDisplayRunning;
    private readonly AgenteKiosk _agente;

    public ControllerForm(AgenteKiosk agente)
    {
        _agente = agente;
        InitializeComponent();

        try
        {
            this.Icon = IconLoader.TryLoadIcon(new[] { "kioskcontroller_256.png", "kioskcontroller_128.png", "kioskcontroller_64.png", "kioskcontroller_32.png" });
        }
        catch { }

        _config = ConfigManager.LoadConfig();

        var travaLocal = new System.Windows.Forms.Timer { Interval = 1000 };
        travaLocal.Tick += (_, _) =>
        {
            if (Visible && !SenhaLocal.Liberado) Hide();
        };
        travaLocal.Start();
        FormClosed += (_, _) => travaLocal.Dispose();

        txtUrl.Text = _config.NpsUrl;
        chkRouter.Checked = _config.RouterEnabled;

        cmbRouterFilter.Items.AddRange(new object[] { "Strict", "Relaxed" });
        var filterMode = string.Equals(_config.RouterWindowFilterMode, "Strict", StringComparison.OrdinalIgnoreCase)
            ? "Strict"
            : "Relaxed";
        cmbRouterFilter.SelectedItem = filterMode;
        cmbRouterFilter.SelectedIndexChanged += cmbRouterFilter_SelectedIndexChanged;

        chkRouterDebugLogging.Checked = _config.RouterDebugLogging;
        chkRouterDebugLogging.CheckedChanged += chkRouterDebugLogging_CheckedChanged;

        PopulateMonitors();
        InitializeGestaoGroup();

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000
        };
        _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
        _statusTimer.Start();

        Shown += async (_, _) => await UpdateStatusAsync();
        FormClosed += (_, _) => _statusTimer.Dispose();
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
					"Sair encerra o navegador Kiosk e o Window Router.\n\nConfirma encerrar tudo?",
					"KioskMax",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Question,
					MessageBoxDefaultButton.Button2);

				e.Cancel = true;

				if (result == DialogResult.Yes)
				{
					if (!_agente.Tef.TentarReservar())
					{
						MessageBox.Show("Há uma transação TEF em andamento. Aguarde terminar no pinpad antes de fechar o KioskMax.",
							"KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
						return;
					}

					this.Hide();
					BeginInvoke(new Action(() => Application.Exit()));
					return;
				}

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
            AtualizarGestao();
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
        if (!SenhaLocal.Solicitar(this, "abrir a pasta de configuração")) return;
        ConfigManager.OpenConfigDirectory();
    }

    private async void btnStartStop_Click(object sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "iniciar ou parar a tela")) return;
        try
        {
            UseWaitCursor = true;
            btnStartStop.Enabled = false;
            btnReload.Enabled = false;
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            if (isRunning)
            {
                await _agente.Tela.PararAsync();
            }
            else
            {
                var ok = await _agente.Tela.IniciarAsync();
                if (!ok)
                {
                    MessageBox.Show($"KioskBrowser.exe could not be started.\nExpected at:\n{GerenciadorTela.ResolverCaminhoExe()}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
        if (!SenhaLocal.Solicitar(this, "trocar o endereço da tela")) return;
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
                    _config = ConfigManager.Update(c =>
                    {
                        c.NpsUrl = newUrl;
                        c.TelaLocal = string.Empty;
                    });
                }
            }
            else
            {
                _config = ConfigManager.Update(c =>
                {
                    c.NpsUrl = newUrl;
                    c.TelaLocal = string.Empty;
                });
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
                    _config = ConfigManager.Update(c =>
                    {
                        c.MonitorPolicy = "Explicit";
                        c.MonitorIndex = index;
                        c.MonitorDeviceId = string.Empty;
                    });
                }
            }
            else
            {
                // Persist locally for próxima inicialização do Display
                _config = ConfigManager.Update(c =>
                {
                    c.MonitorPolicy = "Explicit";
                    c.MonitorIndex = index;
                    c.MonitorDeviceId = string.Empty;
                });
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
        var habilitado = chkRouter.Checked;
        _config = ConfigManager.Update(c => c.RouterEnabled = habilitado);
    }

    private void cmbRouterFilter_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var item = cmbRouterFilter.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(item))
            return;

        _config = ConfigManager.Update(c => c.RouterWindowFilterMode = item);
    }

    private void chkRouterDebugLogging_CheckedChanged(object? sender, EventArgs e)
    {
        var depuracao = chkRouterDebugLogging.Checked;
        _config = ConfigManager.Update(c => c.RouterDebugLogging = depuracao);
    }
}
