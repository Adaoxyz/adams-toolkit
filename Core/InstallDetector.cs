using System.IO;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>Deteta se uma app está instalada: caminhos conhecidos + registry Uninstall.</summary>
public static class InstallDetector
{
    private static List<string>? _installedNamesCache;

    public static void InvalidateCache() => _installedNamesCache = null;

    public static bool IsInstalled(AppEntry app)
    {
        foreach (var raw in app.DetectPaths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(expanded) || Directory.Exists(expanded)) return true;
        }

        if (app.DetectRegistryNames.Count > 0)
        {
            var names = GetInstalledDisplayNames();
            foreach (var needle in app.DetectRegistryNames)
                if (names.Any(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    return true;
        }

        return false;
    }

    private static List<string> GetInstalledDisplayNames()
    {
        if (_installedNamesCache != null) return _installedNamesCache;

        var names = new List<string>();
        var roots = new (RegistryKey hive, string path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        if (k?.GetValue("DisplayName") is string dn && dn.Length > 0)
                            names.Add(dn);
                    }
                    catch { }
                }
            }
            catch { }
        }

        _installedNamesCache = names;
        return names;
    }
}
