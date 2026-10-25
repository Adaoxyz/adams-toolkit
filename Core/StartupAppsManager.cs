using System.IO;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// Gestor de programas que arrancam com o Windows — mesmas fontes e semântica
/// do Gestor de Tarefas: chaves Run (HKCU/HKLM/Wow6432Node) e pastas Arranque,
/// com o estado ligado/desligado guardado em Explorer\StartupApproved (byte 0
/// par = ligado, ímpar = desligado). Desligar NÃO apaga a entrada do programa —
/// é reversível e o Gestor de Tarefas mostra o mesmo estado. Entradas de HKLM
/// precisam de admin para mudar; sem admin ficam só de leitura.
/// A própria app fica de fora (tem o toggle "Iniciar com o Windows" próprio).
/// </summary>
public static class StartupAppsManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public sealed class StartupApp
    {
        public string Name { get; init; } = "";
        public string Command { get; init; } = "";
        public string SourceLabel { get; init; } = "";
        public bool Enabled { get; set; }
        public bool Machine { get; init; }          // HKLM → mudar exige admin
        internal string ApprovedKeyPath { get; init; } = "";
    }

    public static List<StartupApp> List()
    {
        var list = new List<StartupApp>();

        CollectRun(Registry.CurrentUser, RunPath, ApprovedRun, "Utilizador", machine: false, list);
        CollectRun(Registry.LocalMachine, RunPath, ApprovedRun, "Sistema", machine: true, list);
        CollectRun(Registry.LocalMachine, Run32Path, ApprovedRun32, "Sistema", machine: true, list);

        CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Registry.CurrentUser, "Pasta Arranque", machine: false, list);
        CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Registry.LocalMachine, "Pasta Arranque (todos)", machine: true, list);

        return list
            .Where(a => !a.Name.Equals("AdamsToolkit", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectRun(RegistryKey hive, string runPath, string approvedPath,
        string source, bool machine, List<StartupApp> list)
    {
        try
        {
            using var run = hive.OpenSubKey(runPath);
            if (run == null) return;
            using var approved = hive.OpenSubKey(approvedPath);
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new StartupApp
                {
                    Name = name,
                    Command = run.GetValue(name)?.ToString() ?? "",
                    SourceLabel = source,
                    Machine = machine,
                    Enabled = IsApproved(approved, name),
                    ApprovedKeyPath = approvedPath,
                });
            }
        }
        catch (Exception ex) { Log.Swallow($"listar apps de arranque ({source})", ex); }
    }

    private static void CollectFolder(string folder, RegistryKey hive,
        string source, bool machine, List<StartupApp> list)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            using var approved = hive.OpenSubKey(ApprovedFolder);
            foreach (var file in Directory.GetFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupApp
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    SourceLabel = source,
                    Machine = machine,
                    Enabled = IsApproved(approved, fileName),
                    ApprovedKeyPath = ApprovedFolder,
                });
            }
        }
        catch (Exception ex) { Log.Swallow($"listar atalhos de arranque ({source})", ex); }
    }

    // Sem valor gravado = ligado; byte 0 par = ligado, ímpar = desligado.
    private static bool IsApproved(RegistryKey? approved, string valueName)
    {
        try
        {
            if (approved?.GetValue(valueName) is byte[] data && data.Length > 0)
                return (data[0] & 1) == 0;
        }
        catch { }
        return true;
    }

    /// <summary>Liga/desliga uma entrada. Devolve erro ou null se ok.</summary>
    public static string? SetEnabled(StartupApp app, bool enabled)
    {
        try
        {
            var hive = app.Machine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = hive.OpenSubKey(app.ApprovedKeyPath, writable: true)
                            ?? hive.CreateSubKey(app.ApprovedKeyPath);
            if (key == null) return "chave de registo inacessível";

            var valueName = app.ApprovedKeyPath == ApprovedFolder
                ? Path.GetFileName(app.Command) : app.Name;

            var data = new byte[12];
            if (enabled) data[0] = 0x02;
            else
            {
                // 0x03 + FILETIME de quando foi desligado (formato do Gestor de Tarefas)
                data[0] = 0x03;
                BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            }
            key.SetValue(valueName, data, RegistryValueKind.Binary);
            app.Enabled = enabled;
            return null;
        }
        catch (UnauthorizedAccessException) { return "precisa de admin"; }
        catch (Exception e) { return e.Message; }
    }
}
