using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using NpsShared.Configuration;

namespace NpsWatchdog;

public class Program
{
    public static void Main(string[] args)
    {
        // Initialize logging
        var logPath = Path.Combine(ConfigManager.GetLogDirectory(), "NpsWatchdog-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== NpsWatchdog Starting ===");

        try
        {
            CreateHostBuilder(args).Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service crashed");
        }
        finally
        {
            Log.Information("=== NpsWatchdog Stopping ===");
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "NpsWatchdog";
            })
            .UseSerilog()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<WatchdogWorker>();
            });
}

