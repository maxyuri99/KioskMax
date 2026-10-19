using Serilog;
using NpsShared.Configuration;
using NpsShared.Utilities;

namespace KioskController;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Initialize logging
        var logPath = Path.Combine(ConfigManager.GetLogDirectory(), "KioskController-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(NivelLog.Ler())
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Opportunistic log cleanup (older than 30 days)
        try { ConfigManager.CleanupOldLogs(30); } catch { }
        Log.Information("=== KioskController Starting ===");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "Exceção não tratada na interface");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Exceção não tratada (encerrando: {Encerrando})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Exceção de tarefa não observada");
            e.SetObserved();
        };

        if (SinalTefOcupado.OutraInstanciaOcupada())
        {
            Log.Warning("Outra instância do KioskMax está com transação TEF em andamento; esta instância não vai encerrá-la");
            Log.CloseAndFlush();
            ApplicationConfiguration.Initialize();
            MessageBox.Show("O KioskMax já está aberto com uma transação TEF em andamento.\n\nAguarde a transação terminar no pinpad e abra o KioskMax de novo se precisar.",
                "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var pid = Environment.ProcessId;
        KioskProcessTakeover.KillOtherInstancesSameExe(pid);
        KioskProcessTakeover.KillAllWithImageName("WindowRouter");
        KioskProcessTakeover.KillAllWithImageName("KioskBrowser");
        KioskProcessTakeover.KillAllWithImageName("NpsDisplay");
        Thread.Sleep(400);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application crashed");
        }
        finally
        {
            Log.Information("=== KioskController Stopping ===");
            Log.CloseAndFlush();
        }
    }
}

