using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using Serilog;

namespace NpsWatchdog;

/// <summary>
/// Background service that monitors NpsDisplay and restarts it if necessary
/// </summary>
public class WatchdogWorker : BackgroundService
{
    private const int CheckIntervalSeconds = 30;
    private const int MaxConsecutiveFailures = 3;
    private const int RestartDelaySeconds = 5;

    private int _consecutiveFailures = 0;
    private DateTime _lastRestartAttempt = DateTime.MinValue;
    private NpsConfig _config;

    public WatchdogWorker()
    {
        _config = ConfigManager.LoadConfig();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("Watchdog service started");

        // Wait a bit for system to fully start
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRecoverDisplay(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in watchdog loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        Log.Information("Watchdog service stopped");
    }

    private async Task CheckAndRecoverDisplay(CancellationToken cancellationToken)
    {
        // Reload config periodically
        _config = ConfigManager.LoadConfig();

        if (!_config.AutoRecover)
        {
            Log.Debug("Auto-recovery disabled in config");
            return;
        }

        // Check if display is responding
        var isRunning = await IpcClient.IsDisplayRunningAsync();

        if (isRunning)
        {
            _consecutiveFailures = 0;
            Log.Debug("Display is healthy");
            return;
        }

        // Display is not responding
        _consecutiveFailures++;
        Log.Warning("Display not responding (attempt {Count}/{Max})", 
            _consecutiveFailures, MaxConsecutiveFailures);

        // Only restart after multiple consecutive failures to avoid false positives
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            // Don't restart too frequently
            var timeSinceLastRestart = DateTime.Now - _lastRestartAttempt;
            if (timeSinceLastRestart < TimeSpan.FromMinutes(1))
            {
                Log.Warning("Restart throttled, too soon since last attempt");
                return;
            }

            await RestartDisplay(cancellationToken);
            _lastRestartAttempt = DateTime.Now;
            _consecutiveFailures = 0;
        }
    }

    private async Task RestartDisplay(CancellationToken cancellationToken)
    {
        try
        {
            Log.Warning("Attempting to restart Display");

            // First, try to kill any existing processes
            await KillExistingDisplayProcesses();

            // Wait a bit
            await Task.Delay(TimeSpan.FromSeconds(RestartDelaySeconds), cancellationToken);

            // Start new process
            var displayExePath = GetDisplayExecutablePath();

            if (!File.Exists(displayExePath))
            {
                Log.Error("Display executable not found: {Path}", displayExePath);
                return;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = displayExePath,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                Log.Information("Display restarted successfully: PID {PID}", process.Id);
            }
            else
            {
                Log.Error("Failed to start Display process");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart Display");
        }
    }

    private async Task KillExistingDisplayProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("NpsDisplay");
            
            foreach (var process in processes)
            {
                try
                {
                    Log.Information("Killing hung Display process: PID {PID}", process.Id);
                    process.Kill();
                    await process.WaitForExitAsync();
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to kill process {PID}", process.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error killing existing processes");
        }
    }

    private string GetDisplayExecutablePath()
    {
        // Common installation paths
        var paths = new[]
        {
            @"C:\Program Files\UpMais\NpsKiosk\Display\NpsDisplay.exe",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NpsDisplay.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "NpsDisplay", "NpsDisplay.exe")
        };

        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        // Default to first path
        return paths[0];
    }
}

