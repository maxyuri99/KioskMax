using System.Diagnostics;
using NpsShared.Configuration;
using NpsShared.Models;
using Serilog;

namespace NpsShared.Utilities;

/// <summary>Starts/stops the WindowRouter child process from Tray or Controller.</summary>
public static class WindowRouterLifecycle
{
    /// <summary>Kills every WindowRouter.exe instance (stops moving windows).</summary>
    public static void StopRoutingProcesses()
    {
        KioskProcessTakeover.KillAllWithImageName("WindowRouter");
    }

    /// <summary>
    /// If <see cref="NpsConfig.RouterEnabled"/> is true, kills stale WindowRouter instances and starts one new process.
    /// Reloads config from disk so checkbox/menu changes apply immediately.
    /// </summary>
    /// <returns>
    /// True if routing is disabled in config (nothing to start), or enabled and a process was started.
    /// False if routing is enabled but the executable was missing or Process.Start failed.
    /// </returns>
    public static bool TryLaunchRouterIfEnabled(out Process? startedProcess)
    {
        startedProcess = null;

        NpsConfig config;
        try
        {
            config = ConfigManager.LoadConfig();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WindowRouter launch skipped: failed to load config");
            startedProcess = null;
            return true;
        }

        if (!config.RouterEnabled)
            return true;

        KioskProcessTakeover.KillAllWithImageName("WindowRouter");
        Thread.Sleep(200);

        var exePath = ResolveWindowRouterExePath();
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Log.Warning("WindowRouter.exe not found (searched from {Base}); routing will stay off", AppDomain.CurrentDomain.BaseDirectory);
            return false;
        }

        try
        {
            startedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false
            });
            Log.Information("WindowRouter started: PID {Pid}", startedProcess?.Id);
            return startedProcess != null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start WindowRouter");
            return false;
        }
    }

    private static string ResolveWindowRouterExePath()
    {
        const string fileName = "WindowRouter.exe";
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
        string exePath;
        try
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
        }
        catch
        {
            exePath = runtimeBase;
        }

        var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

        var sameDir = Path.Combine(exeDir, fileName);
        if (File.Exists(sameDir))
            return sameDir;

        var rootsToSearch = new List<string>();
        void AddAncestors(string start)
        {
            try
            {
                var current = new DirectoryInfo(start);
                for (var i = 0; i < 8 && current != null; i++)
                {
                    rootsToSearch.Add(current.FullName);
                    current = current.Parent;
                }
            }
            catch
            {
                // ignore
            }
        }

        AddAncestors(exeDir);
        AddAncestors(runtimeBase);

        foreach (var root in rootsToSearch.Distinct())
        {
            var s1 = Path.Combine(root, fileName);
            if (File.Exists(s1))
                return s1;

            var s2 = Path.Combine(root, "WindowRouter", fileName);
            if (File.Exists(s2))
                return s2;

            var d1 = Path.Combine(root, "WindowRouter", "bin", "Debug", "net8.0-windows", fileName);
            if (File.Exists(d1))
                return d1;

            var d2 = Path.Combine(root, "src", "WindowRouter", "bin", "Debug", "net8.0-windows", fileName);
            if (File.Exists(d2))
                return d2;

            var d3Full = Path.GetFullPath(Path.Combine(root, "..", "src", "WindowRouter", "bin", "Debug", "net8.0-windows", fileName));
            if (File.Exists(d3Full))
                return d3Full;

            var p1 = Path.Combine(root, "build", "publish", "WindowRouter", fileName);
            if (File.Exists(p1))
                return p1;

            var p2 = Path.Combine(root, "Router", fileName);
            if (File.Exists(p2))
                return p2;

            var p3 = Path.Combine(root, "temp-run", "WindowRouter", fileName);
            if (File.Exists(p3))
                return p3;
        }

        foreach (var root in rootsToSearch.Distinct())
        {
            try
            {
                var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
            catch
            {
                // ignore
            }
        }

        return sameDir;
    }
}
