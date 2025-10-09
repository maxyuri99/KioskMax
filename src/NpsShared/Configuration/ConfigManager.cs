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

    public static string GetLogDirectory() => LogDirectory;

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
                    Log.Information("Configuration loaded from {Path}", ConfigFilePath);
                    return config;
                }
            }

            // Create default config
            var defaultConfig = new NpsConfig();
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

            File.WriteAllText(ConfigFilePath, json);
            Log.Information("Configuration saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
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

