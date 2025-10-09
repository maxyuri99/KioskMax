using Serilog;
using NpsShared.Configuration;

namespace WindowRouter;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Initialize logging
        var logPath = Path.Combine(ConfigManager.GetLogDirectory(), "WindowRouter-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try { ConfigManager.CleanupOldLogs(30); } catch { }
        Log.Information("=== WindowRouter Starting ===");

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new RouterApplicationContext());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application crashed");
        }
        finally
        {
            Log.Information("=== WindowRouter Stopping ===");
            Log.CloseAndFlush();
        }
    }
}

