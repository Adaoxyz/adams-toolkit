using System.IO;

namespace AdamsToolkit.Core;

/// <summary>Pastas do Editor de Clips (ex-Adams Clips, integrado no Toolkit em v1.71).</summary>
public static class EditorPaths
{
    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdamsToolkit", "editor");

    /// <summary>Onde os clips ficam por defeito (a mesma pasta que o Adams Clips usava).</summary>
    public static string OutputDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Adams Clips");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    public static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "editor-crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
