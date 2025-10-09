using System.Diagnostics;
using System.Drawing;

namespace NpsShared.Utilities;

public static class IconLoader
{
    public static Icon TryLoadIcon(string[] candidateFiles)
    {
        try
        {
            var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
            var roots = new List<string>();

            void AddAncestors(string start)
            {
                try
                {
                    var d = new DirectoryInfo(start);
                    for (int i = 0; i < 8 && d != null; i++)
                    {
                        roots.Add(d.FullName);
                        d = d.Parent;
                    }
                }
                catch { }
            }

            AddAncestors(runtimeBase);
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) AddAncestors(dir);
            }

            foreach (var root in roots.Distinct())
            {
                var iconDir = Path.Combine(root, "icones");
                if (!Directory.Exists(iconDir)) continue;

                foreach (var file in candidateFiles)
                {
                    var path = Path.Combine(iconDir, file);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        using var bmp = new Bitmap(path);
                        var h = bmp.GetHicon();
                        return Icon.FromHandle(h);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return SystemIcons.Application;
    }
}


