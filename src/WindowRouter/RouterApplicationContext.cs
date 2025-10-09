using System.Diagnostics;
using NpsShared.Configuration;
using NpsShared.Models;
using Serilog;

namespace WindowRouter;

/// <summary>
/// Application context that runs the window routing service
/// </summary>
public class RouterApplicationContext : ApplicationContext
{
    private readonly WindowHookManager? _hookManager;
    private readonly NotifyIcon? _trayIcon;
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

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "KioskMax - Window Router",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Window Router Active", null, (s, e) => { }).Enabled = false;
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, Exit_Click);
        _trayIcon.ContextMenuStrip = contextMenu;

        // Initialize window hook manager
        _hookManager = new WindowHookManager(_config);
        _hookManager.Start();

        Log.Information("Window Router started");
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        Log.Information("Exit requested");
        _hookManager?.Stop();
        if (_trayIcon != null)
            _trayIcon.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hookManager?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

