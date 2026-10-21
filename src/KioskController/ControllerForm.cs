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
        _config = ConfigManager.LoadConfig();
        InitializeComponent();
        MinimumSize = Size;

        try
        {
            this.Icon = IconLoader.TryLoadIcon(new[] { "kioskcontroller_256.png", "kioskcontroller_128.png", "kioskcontroller_64.png", "kioskcontroller_32.png" });
        }
        catch { }

        var travaLocal = new System.Windows.Forms.Timer { Interval = 1000 };
        travaLocal.Tick += (_, _) =>
        {
            if (Visible && !SenhaLocal.Liberado) Hide();
        };
        travaLocal.Start();
        FormClosed += (_, _) => travaLocal.Dispose();

        txtUrl.Text = _config.NpsUrl;
        chkRouter.Checked = _config.RouterEnabled;

        // Índices alinhados com FiltrosRouter: 0 = Relaxed, 1 = Strict.
        cmbRouterFilter.Items.AddRange(new object[] { "Amplo (recomendado)", "Restrito" });
        cmbRouterFilter.SelectedIndex = string.Equals(_config.RouterWindowFilterMode, "Strict", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        cmbRouterFilter.SelectedIndexChanged += cmbRouterFilter_SelectedIndexChanged;

        chkRouterDebugLogging.Checked = _config.RouterDebugLogging;
        chkRouterDebugLogging.CheckedChanged += chkRouterDebugLogging_CheckedChanged;

        PopulateMonitors();
        AtualizarGestao();

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
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Ocultar();
            return;
        }
        base.OnFormClosing(e);
    }

    public void Ocultar()
    {
        Hide();
        SenhaLocal.Bloquear();
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            var isRunning = await IpcClient.IsDisplayRunningAsync();
            MostrarSituacaoTela(isRunning);
            AtualizarGestao();
        }
        catch
        {
            MostrarSituacaoTela(false);
        }
    }

    private static readonly Color CorOk = Color.FromArgb(22, 128, 61);
    private static readonly Color CorAviso = Color.FromArgb(180, 110, 0);
    private static readonly Color CorErro = Color.FromArgb(190, 30, 45);
    private static readonly string[] FiltrosRouter = { "Relaxed", "Strict" };

    private void MostrarSituacaoTela(bool aberta)
    {
        _lastIsDisplayRunning = aberta;
        lblStatus.Text = aberta ? "● Tela: aberta" : "● Tela: fechada";
        lblStatus.ForeColor = aberta ? CorOk : CorErro;
        btnStartStop.Text = aberta ? "Parar tela" : "Iniciar tela";
    }

    private void PopulateMonitors()
    {
        cmbMonitors.Items.Clear();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = $"Monitor {i + 1}: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (principal)" : string.Empty)}";
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

    private void btnOpenLogs_Click(object? sender, EventArgs e)
    {
        ConfigManager.OpenLogDirectory();
    }

    private void btnOpenConfig_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "abrir a pasta de configuração")) return;
        ConfigManager.OpenConfigDirectory();
    }

    private async void btnStartStop_Click(object? sender, EventArgs e)
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
                    MessageBox.Show($"Não foi possível abrir o navegador do kiosk (KioskBrowser.exe).\nEsperado em:\n{GerenciadorTela.ResolverCaminhoExe()}",
                        "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            await Task.Delay(800);
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnStartStop.Enabled = true;
            btnReload.Enabled = true;
        }
    }

    private async void btnReload_Click(object? sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            btnStartStop.Enabled = false;
            btnReload.Enabled = false;
            var response = await IpcClient.ReloadAsync();
            if (!response.Success)
            {
                MessageBox.Show($"Não foi possível recarregar a página: {response.Message}", "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnStartStop.Enabled = true;
            btnReload.Enabled = true;
        }
    }

    private async void btnApplyUrl_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "trocar o endereço da tela")) return;
        try
        {
            UseWaitCursor = true;
            btnApplyUrl.Enabled = false;
            var newUrl = txtUrl.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newUrl))
            {
                MessageBox.Show("Informe o endereço da página.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var isRunning = await IpcClient.IsDisplayRunningAsync();
            if (isRunning)
            {
                var response = await IpcClient.SetUrlAsync(newUrl);
                if (!response.Success)
                {
                    MessageBox.Show($"Não foi possível trocar o endereço: {response.Message}", "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show(ex.Message, "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            btnApplyUrl.Enabled = true;
        }
    }

    private async void cmbMonitors_SelectedIndexChanged(object? sender, EventArgs e)
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
                    MessageBox.Show($"Não foi possível trocar o monitor: {response.Message}", "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show(ex.Message, "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            cmbMonitors.Enabled = true;
        }
    }

    private void chkRouter_CheckedChanged(object? sender, EventArgs e)
    {
        var habilitado = chkRouter.Checked;
        _config = ConfigManager.Update(c => c.RouterEnabled = habilitado);
    }

    private void cmbRouterFilter_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var indice = cmbRouterFilter.SelectedIndex;
        if (indice < 0 || indice >= FiltrosRouter.Length)
            return;

        var modo = FiltrosRouter[indice];
        _config = ConfigManager.Update(c => c.RouterWindowFilterMode = modo);
    }

    private void chkRouterDebugLogging_CheckedChanged(object? sender, EventArgs e)
    {
        var depuracao = chkRouterDebugLogging.Checked;
        _config = ConfigManager.Update(c => c.RouterDebugLogging = depuracao);
    }
}
