using System.Text.Json;
using NpsShared.Models;
using Serilog;

namespace NpsShared.Configuration;

/// <summary>
/// Manages configuration loading and saving
/// </summary>
public class ConfigManager
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UpMais",
        "NpsKiosk"
    );

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
    private static readonly string LogDirectory = Path.Combine(ConfigDirectory, "logs");

    private static readonly object ConfigLock = new();

    public static string? UltimoErroGravacao { get; private set; }

    public static string GetLogDirectory() => LogDirectory;

    public static string GetConfigDirectory() => ConfigDirectory;

    public static NpsConfig Update(Action<NpsConfig> mutate)
    {
        lock (ConfigLock)
        {
            var config = LoadConfig();
            mutate(config);
            SaveConfig(config);
            return config;
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
    /// Loads configuration from file, or creates default if not exists
    /// </summary>
    public static NpsConfig LoadConfig()
    {
        try
        {
            EnsureDirectoriesExist();

            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<NpsConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                if (config != null)
                {
                    if (string.IsNullOrWhiteSpace(config.KioskIdentificador))
                    {
                        config.KioskIdentificador = Guid.NewGuid().ToString("N");
                        SaveConfig(config);
                    }
                    return config;
                }
            }

            // Create default config
            var defaultConfig = new NpsConfig { KioskIdentificador = Guid.NewGuid().ToString("N") };
            SaveConfig(defaultConfig);
            Log.Information("Default configuration created at {Path}", ConfigFilePath);
            return defaultConfig;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load configuration, using defaults");
            return new NpsConfig();
        }
    }

    /// <summary>
    /// Saves configuration to file
    /// </summary>
    public static void SaveConfig(NpsConfig config)
    {
        try
        {
            EnsureDirectoriesExist();

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigFilePath, true);
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
}

