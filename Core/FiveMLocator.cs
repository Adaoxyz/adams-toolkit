using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// Localiza a instalação do FiveM no PC de cada pessoa (nem todos usam o caminho
/// default): 1) protocol handler fivem:// no registry, 2) chave de uninstall,
/// 3) %LOCALAPPDATA%\FiveM. Nunca assumir caminhos fixos fora daqui.
/// </summary>
public static class FiveMLocator
{
    /// <summary>Pasta que contém o FiveM.exe (ex: C:\Users\x\AppData\Local\FiveM), ou null.</summary>
    public static string? RootDir
    {
        get
        {
            // 1. protocol handler fivem:// — aponta para o FiveM.exe real desta máquina
            foreach (var keyPath in new[]
            {
                @"Software\Classes\FiveM.ProtocolHandler\shell\open\command",
                @"Software\Classes\fivem\shell\open\command",
            })
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(keyPath);
                    if (k?.GetValue("") is string cmd)
                    {
                        var exe = ParseExePath(cmd);
                        if (exe != null && File.Exists(exe))
                            return Path.GetDirectoryName(exe);
                    }
                }
                catch { }
            }

            // 2. entrada de uninstall
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FiveM");
                if (k?.GetValue("InstallLocation") is string loc &&
                    loc.Length > 0 && Directory.Exists(loc))
                    return loc.TrimEnd('\\');
            }
            catch { }

            // 3. caminho default
            var def = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\FiveM");
            return Directory.Exists(def) ? def : null;
        }
    }

    public static string? ExePath
    {
        get
        {
            var root = RootDir;
            if (root == null) return null;
            var exe = Path.Combine(root, "FiveM.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>Pasta "FiveM Application Data" (FiveM.app), ou null.</summary>
    public static string? AppDataDir
    {
        get
        {
            var root = RootDir;
            if (root == null) return null;
            var app = Path.Combine(root, "FiveM.app");
            if (Directory.Exists(app)) return app;
            // instalações onde o utilizador apontou diretamente para dentro do FiveM.app
            return File.Exists(Path.Combine(root, "CitizenFX.ini")) ? root : null;
        }
    }

    public static bool IsFiveMRunning()
    {
        try
        {
            return Process.GetProcesses().Any(p =>
            {
                try { return p.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }
        catch { return false; }
    }

    private static string? ParseExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var sp = command.IndexOf(' ');
        return sp > 0 ? command[..sp] : command;
    }

    // ===== limpeza de cache =====

    /// <summary>
    /// Apaga todas as pastas dentro de FiveM.app\data EXCETO game-storage
    /// (guarda os dados persistentes do jogo). Devolve (sucesso, mensagem).
    /// </summary>
    public static (bool ok, string message) ClearCache()
    {
        var app = AppDataDir;
        if (app == null)
            return (false, "Pasta do FiveM não encontrada neste PC.");

        var data = Path.Combine(app, "data");
        if (!Directory.Exists(data))
            return (false, "Pasta de cache (data) não existe — nada para limpar.");

        if (IsFiveMRunning())
            return (false, "O FiveM está aberto — fecha-o primeiro e tenta de novo.");

        long freed = 0;
        var removed = 0;
        var failed = 0;
        foreach (var dir in Directory.GetDirectories(data))
        {
            if (Path.GetFileName(dir).Equals("game-storage", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                freed += DirSize(dir);
                Directory.Delete(dir, true);
                removed++;
            }
            catch { failed++; }
        }

        if (removed == 0 && failed == 0)
            return (true, "Cache já estava limpa.");
        var msg = $"Cache limpa: {removed} pasta(s), {freed / 1048576.0:0} MB libertados. game-storage preservada.";
        if (failed > 0) msg += $" ({failed} pasta(s) bloqueadas ficaram por apagar.)";
        return (true, msg);
    }

    private static long DirSize(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return size;
    }
}
