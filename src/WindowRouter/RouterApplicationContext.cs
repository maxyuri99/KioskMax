using NpsShared.Utilities;
using System.Runtime.InteropServices;
using NpsShared.Configuration;
using NpsShared.Models;
using Serilog;

namespace WindowRouter;

/// <summary>
/// Application context that runs the window routing service
/// </summary>
public class RouterApplicationContext : ApplicationContext
{
    private const int WmHotkey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int RouterPauseHotkeyId = 0x7042;

    private readonly WindowHookManager? _hookManager;
    private readonly NotifyIcon? _trayIcon;
    private RouterHotkeyWindow? _hotkeyWindow;
    private ToolStripMenuItem? _pauseMenuItem;
    private NpsConfig _config;

    public RouterApplicationContext()
    {
        _config = ConfigManager.LoadConfig();

        if (!_config.RouterEnabled)
        {
            Log.Warning("Router is disabled in config");
            MessageBox.Show("Window Router is disabled in configuration.", "Window Router",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Application.Exit();
            return;
        }

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "KioskMax — Window Router",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(new ToolStripMenuItem("Window Router ativo") { Enabled = false });

        contextMenu.Items.Add(new ToolStripSeparator());

        _pauseMenuItem = new ToolStripMenuItem("Pausar roteamento", null, (_, _) => { if (SenhaLocal.Solicitar(null, "pausar ou retomar o roteador")) ToggleRoutingPaused(); });
        contextMenu.Items.Add(_pauseMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add(new ToolStripMenuItem(
            "Atalho: Ctrl+Alt+tecla (RouterPauseHotKeyVk no JSON)")
        {
            Enabled = false
        });

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Sair", null, Exit_Click);
        _trayIcon.ContextMenuStrip = contextMenu;

        _hookManager = new WindowHookManager(_config);
        _hookManager.Start();

        TryRegisterPauseHotkey();
        UpdatePauseUiState();

        Log.Information("Window Router iniciado.");
    }

    private void TryRegisterPauseHotkey()
    {
        try
        {
            _config = ConfigManager.LoadConfig();

            UnregisterPauseHotkeyInternal();

            if (!_config.RouterPauseHotKeyEnabled)
            {
                Log.Information("Atalho de pausa do Router desativado na config");
                return;
            }

            var vk = _config.RouterPauseHotKeyVk;
            if (vk < 1 || vk > 254)
            {
                Log.Warning("RouterPauseHotKeyVk invalido ({VK}); RegisterHotKey ignorado", vk);
                return;
            }

            _hotkeyWindow = new RouterHotkeyWindow(this);

            var ok = RegisterHotKey(_hotkeyWindow.Handle, RouterPauseHotkeyId, ModControl | ModAlt, vk);
            if (ok)
            {
                Log.Information("Atalho de pausa registrado: Ctrl+Alt + VK 0x{VK:X}", vk);
            }
            else
            {
                Log.Warning("RegisterHotKey falhou para VK 0x{VK:X} (pode estar em uso)", vk);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao registrar atalho de pausa");
        }
    }

    private void ToggleRoutingPaused()
    {
        if (_hookManager == null)
            return;

        _hookManager.RoutingPaused = !_hookManager.RoutingPaused;
        Log.Information("Window Router roteamento {State}", _hookManager.RoutingPaused ? "PAUSADO" : "ATIVO");
        UpdatePauseUiState();
    }

    private void UpdatePauseUiState()
    {
        if (_trayIcon == null || _pauseMenuItem == null || _hookManager == null)
            return;

        _pauseMenuItem.Text = _hookManager.RoutingPaused ? "Retomar roteamento" : "Pausar roteamento";
        _trayIcon.Text = _hookManager.RoutingPaused
            ? "KioskMax — Window Router (pausado)"
            : "KioskMax — Window Router";
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(null, "fechar o roteador de janelas")) return;
        Log.Information("Saida solicitada");
        UnregisterPauseHotkeyInternal();
        _hookManager?.Stop();
        if (_trayIcon != null)
            _trayIcon.Visible = false;
        Application.Exit();
    }

    private void UnregisterPauseHotkeyInternal()
    {
        try
        {
            var w = _hotkeyWindow;
            if (w != null && w.Handle != IntPtr.Zero)
                UnregisterHotKey(w.Handle, RouterPauseHotkeyId);
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { _hotkeyWindow?.Dispose(); } catch { }
            _hotkeyWindow = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterPauseHotkeyInternal();
            _hookManager?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Recebe WM_HOTKEY e aciona pause no outer context.</summary>
    private sealed class RouterHotkeyWindow : NativeWindow, IDisposable
    {
        private readonly RouterApplicationContext _ctx;

        public RouterHotkeyWindow(RouterApplicationContext ctx)
        {
            _ctx = ctx;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == RouterPauseHotkeyId)
            {
                if (SenhaLocal.Solicitar(null, "pausar ou retomar o roteador"))
                    _ctx.ToggleRoutingPaused();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { DestroyHandle(); } catch { }
        }
    }
}
