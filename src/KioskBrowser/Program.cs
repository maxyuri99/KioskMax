using Serilog;
using NpsShared.Configuration;
using NpsShared.Utilities;

namespace NpsDisplay;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Initialize logging
        var logPath = Path.Combine(ConfigManager.GetLogDirectory(), "KioskBrowser-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024, // ~10 MB por arquivo
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try { ConfigManager.CleanupOldLogs(30); } catch { }
        Log.Information("=== KioskBrowser Starting ===");

        KioskProcessTakeover.KillOtherInstancesSameExe(Environment.ProcessId);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application crashed");
            MessageBox.Show($"Fatal error: {ex.Message}", "KioskBrowser Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Log.Information("=== KioskBrowser Stopping ===");
            Log.CloseAndFlush();
        }
    }
}

