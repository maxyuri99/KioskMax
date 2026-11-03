using System.Text.Json;
using NpsShared.Models;
using Serilog;

namespace NpsShared.Configuration;

/// <summary>
/// Manages configuration loading and saving.
/// Only the process marked as writer (the KioskController agent) writes config.json; the others only read it.
/// A failed read never results in the default configuration being written over the file.
/// </summary>
public class ConfigManager
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UpMais",
        "NpsKiosk"
    );

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
    private static readonly string BackupFilePath = ConfigFilePath + ".bak";
    private static readonly string LogDirectory = Path.Combine(ConfigDirectory, "logs");

    private const string NomeTravaArquivo = @"Local\UpMais_NpsKiosk_Config";
    private static readonly TimeSpan PrazoTravaArquivo = TimeSpan.FromSeconds(3);
    private const int TentativasLeitura = 4;

    private static readonly object ConfigLock = new();
    private static readonly JsonSerializerOptions OpcoesLeitura = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OpcoesGravacao = new() { WriteIndented = true };

    private static string? _ultimaLidaJson;

    public static string? UltimoErroGravacao { get; private set; }

    public static string? UltimoErroLeitura { get; private set; }

    /// <summary>
    /// True only in the process that owns config.json (KioskController). Other processes never write it.
    /// </summary>
    public static bool Gravador { get; set; }

    public static string GetLogDirectory() => LogDirectory;

    public static string GetConfigDirectory() => ConfigDirectory;

    public static NpsConfig Update(Action<NpsConfig> mutate)
    {
        lock (ConfigLock)
        {
            using var trava = TravaArquivo.Obter();
            var lida = Ler(out var confiavel);
            mutate(lida);

            if (!Gravador)
            {
                Log.Warning("Configuração alterada em memória por um processo que não grava o config.json");
                return lida;
            }

            if (!confiavel)
            {
                UltimoErroGravacao = $"Configuração não gravada: falha ao ler o config.json ({UltimoErroLeitura})";
                Log.Error("Configuração não gravada para não sobrescrever o config.json que não pôde ser lido");
                return lida;
            }

            if (!trava.Obtida)
            {
                UltimoErroGravacao = "Configuração não gravada: o config.json está em uso por outro processo";
                Log.Error("Configuração não gravada: trava do config.json não obtida em {Segundos}s", PrazoTravaArquivo.TotalSeconds);
                return lida;
            }

            Gravar(lida);
            return lida;
        }
    }

    /// <summary>
    /// Deletes old log files beyond a maximum retention window in days.
    /// This is a second safety in addition to Serilog's retainedFileCountLimit.
    /// </summary>
    public static void CleanupOldLogs(int maxAgeDays = 30)
    {
        try
        {
            EnsureDirectoriesExist();
            var cutoff = DateTime.UtcNow.AddDays(-Math.Abs(maxAgeDays));
            if (!Directory.Exists(LogDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    var last = info.LastWriteTimeUtc;
                    if (last < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Loads configuration from file. Only the writer creates the file (or fills the kiosk identifier) when missing.
    /// When the file cannot be read, returns the last configuration read successfully, never writing over the file.
    /// </summary>
    public static NpsConfig LoadConfig()
    {
        lock (ConfigLock)
        {
            using var trava = TravaArquivo.Obter();
            var config = Ler(out var confiavel);
            if (!confiavel || !Gravador || !trava.Obtida) return config;

            if (!File.Exists(ConfigFilePath) || string.IsNullOrWhiteSpace(config.KioskIdentificador))
            {
                if (string.IsNullOrWhiteSpace(config.KioskIdentificador))
                    config.KioskIdentificador = Guid.NewGuid().ToString("N");
                Gravar(config);
                Log.Information("Configuration created at {Path}", ConfigFilePath);
            }

            return config;
        }
    }

    /// <summary>
    /// Saves configuration to file (only in the writer process).
    /// </summary>
    public static void SaveConfig(NpsConfig config)
    {
        lock (ConfigLock)
        {
            if (!Gravador)
            {
                Log.Warning("SaveConfig ignorado: este processo não grava o config.json");
                return;
            }

            using var trava = TravaArquivo.Obter();
            if (!trava.Obtida)
            {
                UltimoErroGravacao = "Configuração não gravada: o config.json está em uso por outro processo";
                Log.Error("Configuração não gravada: trava do config.json não obtida");
                return;
            }

            Gravar(config);
        }
    }

    internal enum ResultadoLeitura
    {
        Lida,
        Inexistente,
        Corrompida,
        Inacessivel
    }

    internal static ResultadoLeitura LerArquivo(string caminho, int tentativas, out NpsConfig? config, out string erro)
    {
        config = null;
        erro = string.Empty;

        for (var tentativa = 1; tentativa <= tentativas; tentativa++)
        {
            try
            {
                if (!File.Exists(caminho)) return ResultadoLeitura.Inexistente;

                string json;
                using (var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var leitor = new StreamReader(fluxo))
                {
                    json = leitor.ReadToEnd();
                }

                config = JsonSerializer.Deserialize<NpsConfig>(json, OpcoesLeitura);
                if (config != null) return ResultadoLeitura.Lida;
                erro = "config.json vazio";
                return ResultadoLeitura.Corrompida;
            }
            catch (JsonException ex)
            {
                erro = ex.Message;
                if (tentativa == tentativas) return ResultadoLeitura.Corrompida;
            }
            catch (FileNotFoundException)
            {
                return ResultadoLeitura.Inexistente;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                erro = ex.Message;
                if (tentativa == tentativas) return ResultadoLeitura.Inacessivel;
            }

            Thread.Sleep(100 * tentativa);
        }

        return ResultadoLeitura.Inacessivel;
    }

    private static NpsConfig Ler(out bool confiavel)
    {
        confiavel = true;
        try
        {
            EnsureDirectoriesExist();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao criar a pasta de configuração");
        }

        var resultado = LerArquivo(ConfigFilePath, TentativasLeitura, out var config, out var erro);
        if (resultado == ResultadoLeitura.Lida)
        {
            Lembrar(config!);
            UltimoErroLeitura = null;
            return config!;
        }

        if (resultado == ResultadoLeitura.Inexistente)
        {
            UltimoErroLeitura = null;
            return new NpsConfig();
        }

        if (resultado == ResultadoLeitura.Corrompida
            && LerArquivo(BackupFilePath, 2, out var copia, out _) == ResultadoLeitura.Lida)
        {
            Log.Error("config.json corrompido ({Erro}); usando a cópia de segurança", erro);
            UltimoErroLeitura = null;
            Lembrar(copia!);
            if (Gravador) GuardarCorrompido();
            return copia!;
        }

        confiavel = false;
        UltimoErroLeitura = erro;
        Log.Error("Falha ao ler o config.json ({Resultado}): {Erro}; usando a última configuração lida", resultado, erro);
        return UltimaLida() ?? new NpsConfig();
    }

    private static void Lembrar(NpsConfig config)
    {
        try { _ultimaLidaJson = JsonSerializer.Serialize(config); } catch { }
    }

    private static NpsConfig? UltimaLida()
    {
        if (_ultimaLidaJson == null) return null;
        try { return JsonSerializer.Deserialize<NpsConfig>(_ultimaLidaJson); } catch { return null; }
    }

    private static void GuardarCorrompido()
    {
        try
        {
            File.Copy(ConfigFilePath, $"{ConfigFilePath}.corrompido-{DateTime.Now:yyyyMMddHHmmss}", true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao guardar o config.json corrompido");
        }
    }

    private static void Gravar(NpsConfig config)
    {
        try
        {
            EnsureDirectoriesExist();

            var json = JsonSerializer.Serialize(config, OpcoesGravacao);

            var tempPath = ConfigFilePath + ".tmp";
            using (var fluxo = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var escritor = new StreamWriter(fluxo))
            {
                escritor.Write(json);
                escritor.Flush();
                fluxo.Flush(true);
            }

            if (File.Exists(ConfigFilePath))
                File.Replace(tempPath, ConfigFilePath, BackupFilePath, true);
            else
                File.Move(tempPath, ConfigFilePath, true);

            Lembrar(config);
            UltimoErroGravacao = null;
            Log.Debug("Configuration saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            UltimoErroGravacao = ex.Message;
            Log.Error(ex, "Failed to save configuration");
        }
    }

    /// <summary>
    /// Gets the configuration file path
    /// </summary>
    public static string GetConfigPath() => ConfigFilePath;

    /// <summary>
    /// Ensures required directories exist
    /// </summary>
    private static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Opens the configuration directory in Explorer
    /// </summary>
    public static void OpenConfigDirectory()
    {
        try
        {
            EnsureDirectoriesExist();
            System.Diagnostics.Process.Start("explorer.exe", ConfigDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open configuration directory");
        }
    }

    /// <summary>
    /// Opens the log directory in Explorer
    /// </summary>
    public static void OpenLogDirectory()
    {
        try
        {
            EnsureDirectoriesExist();
            System.Diagnostics.Process.Start("explorer.exe", LogDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open log directory");
        }
    }

    /// <summary>
    /// Named mutex shared by KioskController and KioskBrowser in the same session around config.json access.
    /// </summary>
    private sealed class TravaArquivo : IDisposable
    {
        private readonly Mutex? _mutex;

        public bool Obtida { get; }

        private TravaArquivo(Mutex? mutex, bool obtida)
        {
            _mutex = mutex;
            Obtida = obtida;
        }

        public static TravaArquivo Obter()
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, NomeTravaArquivo);
                bool obtida;
                try
                {
                    obtida = mutex.WaitOne(PrazoTravaArquivo);
                }
                catch (AbandonedMutexException)
                {
                    obtida = true;
                }

                if (!obtida)
                {
                    mutex.Dispose();
                    return new TravaArquivo(null, false);
                }

                return new TravaArquivo(mutex, true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao obter a trava do config.json");
                mutex?.Dispose();
                return new TravaArquivo(null, false);
            }
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }
}
