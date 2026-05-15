using System.Diagnostics;

namespace NpsShared.Utilities;

/// <summary>
/// Kills older instances so the current launch is the only one running (per user request).
/// </summary>
public static class KioskProcessTakeover
{
    /// <summary>Kill other processes that share the same executable path as the current process.</summary>
    public static void KillOtherInstancesSameExe(int currentProcessId)
    {
        string? exePath = null;
        try
        {
            using var cur = Process.GetCurrentProcess();
            exePath = cur.MainModule?.FileName;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(exePath))
            return;

        var baseName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(baseName))
            return;

        foreach (var p in Process.GetProcessesByName(baseName))
        {
            if (p.Id == currentProcessId)
            {
                p.Dispose();
                continue;
            }

            try
            {
                var other = p.MainModule?.FileName;
                if (string.Equals(other, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(2500);
                }
            }
            catch
            {
                // ignore access / exit errors
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Kill every process with the given image name (no extension), optionally keeping one PID.</summary>
    public static void KillAllWithImageName(string imageNameWithoutExtension, int? exceptProcessId = null)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(imageNameWithoutExtension))
            {
                if (exceptProcessId.HasValue && p.Id == exceptProcessId.Value)
                {
                    p.Dispose();
                    continue;
                }

                try
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
