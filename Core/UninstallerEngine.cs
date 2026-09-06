using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>Programa instalado, lido do registry Uninstall.</summary>
public class InstalledProgram
{
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string InstallDate { get; set; } = "";      // yyyyMMdd (quando existe)
    public long EstimatedSizeKb { get; set; }
    public string DisplayIcon { get; set; } = "";       // "C:\...\app.exe,0" ou .ico

    /// <summary>Hive + subcaminho da chave Uninstall de origem (para remoção forçada / abrir no regedit).</summary>
    public string RegistryHive { get; set; } = "";      // "HKLM" | "HKCU"
    public string RegistryPath { get; set; } = "";

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString);
    public bool IsMsi => UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
    public bool SupportsQuiet => IsMsi || !string.IsNullOrWhiteSpace(QuietUninstallString);
}

public enum LeftoverKind { Directory, File, Shortcut, RegistryKey, RegistryValue }

/// <summary>Um resto deixado no PC: pasta, ficheiro, atalho ou entrada de registry.</summary>
public class LeftoverItem
{
    public LeftoverKind Kind { get; set; }
    public string Path { get; set; } = "";              // caminho fs OU "HIVE\sub\path"
    public string ValueName { get; set; } = "";         // só para RegistryValue
    public long SizeBytes { get; set; }                 // só fs
    public string Display => Kind == LeftoverKind.RegistryValue ? $"{Path} → {ValueName}" : Path;
}

/// <summary>
/// Motor do desinstalador estilo Geek Uninstaller: enumera programas, corre o
/// desinstalador oficial e caça restos (pastas, atalhos, chaves de registry).
/// A app corre asInvoker — apagar em HKLM/Program Files pode exigir admin;
/// cada item reporta o erro individualmente em vez de falhar tudo.
/// </summary>
public static class UninstallerEngine
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Relança a app com pedido de elevação UAC. Devolve false se o utilizador cancelar.</summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // UAC cancelado
    }

    // ---------- enumeração ----------

    public static List<InstalledProgram> Enumerate()
    {
        var list = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roots = new (RegistryKey hive, string hiveName, string path)[]
        {
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser,  "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, hiveName, path) in roots)
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
                        if (k == null) continue;
                        if (k.GetValue("DisplayName") is not string dn || dn.Trim().Length == 0) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") is string pk && pk.Length > 0) continue; // updates
                        if (Regex.IsMatch(dn, @"^(KB\d{6,}|Update for|Security Update|Hotfix)", RegexOptions.IgnoreCase)) continue;

                        var p = new InstalledProgram
                        {
                            DisplayName = dn.Trim(),
                            Version = k.GetValue("DisplayVersion") as string ?? "",
                            Publisher = k.GetValue("Publisher") as string ?? "",
                            InstallLocation = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'),
                            UninstallString = k.GetValue("UninstallString") as string ?? "",
                            QuietUninstallString = k.GetValue("QuietUninstallString") as string ?? "",
                            InstallDate = k.GetValue("InstallDate") as string ?? "",
                            EstimatedSizeKb = k.GetValue("EstimatedSize") is int es ? es : 0,
                            DisplayIcon = k.GetValue("DisplayIcon") as string ?? "",
                            RegistryHive = hiveName,
                            RegistryPath = $@"{path}\{sub}",
                        };

                        if (!seen.Add($"{p.DisplayName}|{p.Version}")) continue; // dedup 64/32 bits
                        list.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
        }

        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // ---------- desinstalar ----------

    /// <summary>Corre o desinstalador oficial e espera que termine. Devolve exit code (ou -1).</summary>
    public static async Task<int> UninstallAsync(InstalledProgram p, bool quiet)
    {
        var cmd = quiet && !string.IsNullOrWhiteSpace(p.QuietUninstallString)
            ? p.QuietUninstallString
            : p.UninstallString;
        if (string.IsNullOrWhiteSpace(cmd)) return -1;

        // msiexec: normaliza /I → /X e acrescenta flags silenciosas quando pedido
        if (p.IsMsi)
        {
            cmd = Regex.Replace(cmd, @"/I\s*{", "/X{", RegexOptions.IgnoreCase);
            if (quiet && !cmd.Contains("/q", StringComparison.OrdinalIgnoreCase))
                cmd += " /qn /norestart";
        }

        var (exe, args) = SplitCommand(cmd);
        if (exe.Length == 0) return -1;

        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        await proc.WaitForExitAsync();
        var code = proc.ExitCode;
        if (!p.IsMsi) await WaitForWizardAsync(p);
        return code;
    }

    /// <summary>Chave Uninstall ainda existe? (false = desinstalado a sério)</summary>
    public static bool UninstallKeyExists(InstalledProgram p)
    {
        try
        {
            var hive = p.RegistryHive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using var k = hive.OpenSubKey(p.RegistryPath);
            return k != null;
        }
        catch { return true; }
    }

    /// <summary>
    /// Desinstaladores NSIS/Inno copiam-se para %TEMP% e o processo original sai
    /// logo — sem esta espera a app "terminava" em 1s, fazia o scan de restos com
    /// o programa ainda instalado e a lista não mudava. Espera até a chave
    /// Uninstall desaparecer enquanto houver um wizard de desinstalação vivo.
    /// </summary>
    private static async Task WaitForWizardAsync(InstalledProgram p)
    {
        bool KeyGone()
        {
            try
            {
                var hive = p.RegistryHive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using var k = hive.OpenSubKey(p.RegistryPath);
                return k == null;
            }
            catch { return false; }
        }
        static bool WizardAlive()
        {
            try
            {
                foreach (var pr in Process.GetProcesses())
                {
                    var n = pr.ProcessName;
                    if (n.StartsWith("Au_", StringComparison.OrdinalIgnoreCase) ||      // NSIS (%TEMP%\~nsu.tmp\Au_.exe)
                        n.StartsWith("_iu", StringComparison.OrdinalIgnoreCase) ||      // Inno (%TEMP%\_iu14D2N.tmp)
                        n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||    // Inno unins000
                        n.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        var deadline = Stopwatch.StartNew();
        var idle = 0;
        while (deadline.Elapsed < TimeSpan.FromMinutes(15))
        {
            if (KeyGone()) return;                    // desinstalado a sério
            idle = WizardAlive() ? 0 : idle + 1;
            if (idle >= 5) return;                    // ~10s sem wizard e a chave continua lá → cancelado
            await Task.Delay(2000);
        }
    }

    /// <summary>Separa "C:\x y\unins.exe" /SILENT em (exe, args) — lida com aspas e caminhos com espaços.</summary>
    public static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return ("", "");
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
            return (cmd.Trim('"'), "");
        }
        // sem aspas: tenta o caminho mais longo que exista; senão corta no 1º espaço após ".exe"
        var m = Regex.Match(cmd, @"^(.+?\.exe)\b", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, cmd[m.Length..].Trim());
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    // ---------- caça aos restos ----------

    // nunca marcar/apagar estas pastas em si (só conteúdo lá dentro que faça match)
    private static readonly string[] StopWords =
    {
        "microsoft", "windows", "common", "commonfiles", "system", "program",
        "programs", "programfiles", "application", "applications", "temp",
        "data", "setup", "install", "installer", "software", "update", "launcher",
    };

    private static IEnumerable<string> ScanRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        return roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Norm(string s) =>
        Regex.Replace(s, @"[\s\-_\.]+", "").ToLowerInvariant();

    /// <summary>Limpa nome de marketing: remove (x64), versões, arquitetura.</summary>
    private static string CleanName(string s)
    {
        s = Regex.Replace(s, @"\(.*?\)", " ");
        s = Regex.Replace(s, @"(?i)\b(x64|x86|64-bit|32-bit|win64|win32|version|edition|edição)\b", " ");
        s = Regex.Replace(s, @"[\d\.\-]+\s*$", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static List<string> BuildKeywords(InstalledProgram p)
    {
        var kws = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var c = CleanName(raw);
            var n = Norm(c);
            if (n.Length < 4 || StopWords.Contains(n)) return;
            if (!kws.Any(k => Norm(k) == n)) kws.Add(c);
        }
        Add(p.DisplayName);
        if (p.InstallLocation.Length > 0)
        {
            try { Add(new DirectoryInfo(p.InstallLocation.TrimEnd('\\', '/')).Name); } catch { }
        }
        return kws;
    }

    private static bool Matches(string candidate, List<string> keywords)
    {
        var nc = Norm(candidate);
        if (nc.Length < 4 || StopWords.Contains(nc)) return false;
        foreach (var kw in keywords)
        {
            var nk = Norm(kw);
            if (nc.Contains(nk)) return true;                    // "MozillaFirefox" contém "firefox"? não — outro sentido:
            if (nc.Length >= 6 && nk.Contains(nc)) return true;  // pasta "Firefox" ⊂ kw "Mozilla Firefox"
        }
        return false;
    }

    /// <summary>
    /// Procura tudo o que o programa deixou: pasta de instalação, pastas com o nome
    /// em Program Files/AppData/ProgramData, atalhos, chaves Software, autoruns e
    /// a própria chave Uninstall órfã.
    /// </summary>
    public static List<LeftoverItem> ScanLeftovers(InstalledProgram p)
    {
        var found = new List<LeftoverItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kws = BuildKeywords(p);
        if (kws.Count == 0 && p.InstallLocation.Length == 0) return found;

        void AddDir(string dir)
        {
            if (!Directory.Exists(dir) || !IsPathSafe(dir) || !seen.Add("D:" + dir)) return;
            found.Add(new LeftoverItem { Kind = LeftoverKind.Directory, Path = dir, SizeBytes = DirSize(dir) });
        }
        void AddFile(string file, LeftoverKind kind)
        {
            if (!File.Exists(file) || !IsPathSafe(file) || !seen.Add("F:" + file)) return;
            long sz = 0; try { sz = new FileInfo(file).Length; } catch { }
            found.Add(new LeftoverItem { Kind = kind, Path = file, SizeBytes = sz });
        }

        // 1) pasta de instalação declarada
        if (p.InstallLocation.Length > 3) AddDir(p.InstallLocation.TrimEnd('\\', '/'));

        // 2) pastas com o nome nos sítios habituais (1º nível + dentro da pasta do publisher)
        var pubKws = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Publisher))
        {
            var pn = Norm(CleanName(p.Publisher.Split(',')[0]));
            if (pn.Length >= 4 && !StopWords.Contains(pn)) pubKws.Add(pn);
        }
        foreach (var root in ScanRoots())
        {
            IEnumerable<string> level1;
            try { level1 = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var dir in level1)
            {
                var name = Path.GetFileName(dir);
                if (Matches(name, kws)) { AddDir(dir); continue; }
                // pasta do publisher (ex.: Program Files\Mozilla) → procura lá dentro
                if (pubKws.Any(pk => Norm(name).Contains(pk)))
                {
                    IEnumerable<string> level2;
                    try { level2 = Directory.EnumerateDirectories(dir); } catch { continue; }
                    foreach (var sub in level2)
                        if (Matches(Path.GetFileName(sub), kws)) AddDir(sub);
                }
            }
        }

        // 3) atalhos: Start Menu (user + comum, recursivo) e Desktop
        var lnkRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (var root in lnkRoots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); } catch { continue; }
            foreach (var lnk in links)
                if (Matches(Path.GetFileNameWithoutExtension(lnk), kws))
                    AddFile(lnk, LeftoverKind.Shortcut);
            // pasta própria no Start Menu (ex.: Programs\Notepad++)
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories); } catch { continue; }
            foreach (var d in dirs)
                if (Matches(Path.GetFileName(d), kws)) AddDir(d);
        }

        // 4) registry: chaves Software\<Nome> e Software\<Publisher>\<Nome>
        var regRoots = new (RegistryKey hive, string hiveName, string path)[]
        {
            (Registry.CurrentUser,  "HKCU", @"SOFTWARE"),
            (Registry.LocalMachine, "HKLM", @"SOFTWARE"),
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node"),
        };
        foreach (var (hive, hiveName, path) in regRoots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    var full = $@"{hiveName}\{path}\{sub}";
                    if (Matches(sub, kws))
                    {
                        if (seen.Add("R:" + full))
                            found.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = full });
                        continue;
                    }
                    if (pubKws.Any(pk => Norm(sub).Contains(pk)))
                    {
                        try
                        {
                            using var pubKey = key.OpenSubKey(sub);
                            if (pubKey == null) continue;
                            foreach (var s2 in pubKey.GetSubKeyNames())
                                if (Matches(s2, kws) && seen.Add($"R:{full}\\{s2}"))
                                    found.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = $@"{full}\{s2}" });
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // 5) autoruns (Run) que apontam para a pasta de instalação ou nome
        var runRoots = new (RegistryKey hive, string hiveName)[]
        {
            (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM"),
        };
        foreach (var (hive, hiveName) in runRoots)
        {
            const string runPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            try
            {
                using var key = hive.OpenSubKey(runPath);
                if (key == null) continue;
                foreach (var vn in key.GetValueNames())
                {
                    var data = key.GetValue(vn) as string ?? "";
                    var hit = Matches(vn, kws) ||
                              (p.InstallLocation.Length > 3 &&
                               data.Contains(p.InstallLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                    if (hit && seen.Add($"V:{hiveName}\\{runPath}\\{vn}"))
                        found.Add(new LeftoverItem
                        {
                            Kind = LeftoverKind.RegistryValue,
                            Path = $@"{hiveName}\{runPath}",
                            ValueName = vn,
                        });
                }
            }
            catch { }
        }

        // 6) chave Uninstall órfã (desinstalador já não existe no disco)
        try
        {
            var hive = p.RegistryHive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using var k = hive.OpenSubKey(p.RegistryPath);
            if (k != null)
            {
                var (exe, _) = SplitCommand(p.UninstallString);
                if (!p.HasUninstaller || (exe.Length > 0 && !File.Exists(exe)))
                    if (seen.Add($"R:{p.RegistryHive}\\{p.RegistryPath}"))
                        found.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = $@"{p.RegistryHive}\{p.RegistryPath}" });
            }
        }
        catch { }

        return found;
    }

    /// <summary>Inclui na lista a chave Uninstall + pasta de instalação — para programas sem desinstalador.</summary>
    public static List<LeftoverItem> ScanForForcedRemoval(InstalledProgram p)
    {
        var items = ScanLeftovers(p);
        var key = $@"{p.RegistryHive}\{p.RegistryPath}";
        if (!items.Any(i => i.Kind == LeftoverKind.RegistryKey && i.Path.Equals(key, StringComparison.OrdinalIgnoreCase)))
            items.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = key });
        return items;
    }

    // ---------- apagar ----------

    /// <summary>Apaga um resto. Devolve (ok, mensagem de erro quando falha).</summary>
    public static (bool ok, string error) DeleteLeftover(LeftoverItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case LeftoverKind.Directory:
                    if (!IsPathSafe(item.Path)) return (false, "caminho protegido");
                    if (Directory.Exists(item.Path))
                    {
                        ClearReadOnly(item.Path);
                        Directory.Delete(item.Path, recursive: true);
                    }
                    return (true, "");

                case LeftoverKind.File:
                case LeftoverKind.Shortcut:
                    if (!IsPathSafe(item.Path)) return (false, "caminho protegido");
                    if (File.Exists(item.Path))
                    {
                        try { File.SetAttributes(item.Path, FileAttributes.Normal); } catch { }
                        File.Delete(item.Path);
                    }
                    return (true, "");

                case LeftoverKind.RegistryKey:
                {
                    var (hive, sub) = SplitRegPath(item.Path);
                    if (hive == null || !IsRegPathSafe(sub)) return (false, "chave protegida");
                    hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                    return (true, "");
                }

                case LeftoverKind.RegistryValue:
                {
                    var (hive, sub) = SplitRegPath(item.Path);
                    if (hive == null) return (false, "chave protegida");
                    using var k = hive.OpenSubKey(sub, writable: true);
                    k?.DeleteValue(item.ValueName, throwOnMissingValue: false);
                    return (true, "");
                }
            }
            return (false, "tipo desconhecido");
        }
        catch (UnauthorizedAccessException) { return (false, "sem permissão — reinicia a app como administrador"); }
        catch (System.Security.SecurityException) { return (false, "sem permissão — reinicia a app como administrador"); }
        catch (IOException ex) { return (false, ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static void ClearReadOnly(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        catch { }
    }

    private static (RegistryKey? hive, string sub) SplitRegPath(string full)
    {
        var idx = full.IndexOf('\\');
        if (idx < 0) return (null, "");
        var hive = full[..idx] switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            _ => null,
        };
        return (hive, full[(idx + 1)..]);
    }

    /// <summary>Só permite apagar chaves dentro de SOFTWARE, e nunca as raízes/sistemas.</summary>
    private static bool IsRegPathSafe(string sub)
    {
        var n = sub.TrimEnd('\\').ToLowerInvariant();
        if (!n.StartsWith("software")) return false;
        // profundidade mínima: SOFTWARE\Algo (ou SOFTWARE\WOW6432Node\Algo)
        var parts = n.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var depth = parts.Length - (parts.Length > 1 && parts[1] == "wow6432node" ? 1 : 0);
        if (depth < 2) return false;
        // nunca apagar árvores do sistema (exceto subchaves de Uninstall, que são de programas)
        if (n.Contains(@"\microsoft\") && !n.Contains(@"currentversion\uninstall\")) return false;
        if (n.EndsWith(@"\microsoft") || n.EndsWith(@"\wow6432node") || n.EndsWith(@"\classes") || n.Contains(@"\classes\")) return false;
        return true;
    }

    /// <summary>Só permite apagar dentro das raízes de scan, nunca a raiz em si nem nada do Windows.</summary>
    public static bool IsPathSafe(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return false; }
        if (full.Length <= 3) return false; // "C:\"

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windir.Length > 0 && full.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) return false;
        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (full.Equals(userRoot, StringComparison.OrdinalIgnoreCase)) return false;

        var allowedRoots = ScanRoots().Concat(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        }).Where(r => r.Length > 0);

        foreach (var root in allowedRoots)
        {
            var r = root.TrimEnd('\\', '/');
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase)) return false;       // nunca a raiz
            if (full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        // pasta de instalação pode estar fora das raízes (ex.: C:\Games\X) — permite se
        // for uma pasta "normal" com pelo menos 2 níveis de profundidade
        var depth = full.Count(c => c == '\\');
        return depth >= 2;
    }

    public static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    // ---------- ícones (API do shell do Windows — os mesmos ícones do Explorer) ----------

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[]? small, uint count);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static System.Windows.Media.Imaging.BitmapSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(hIcon); }
    }

    /// <summary>Ícone via shell (o que o Windows mostra para este ficheiro no Explorer).</summary>
    private static System.Windows.Media.Imaging.BitmapSource? ShellIcon(string path, bool generic = false)
    {
        try
        {
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON | (generic ? SHGFI_USEFILEATTRIBUTES : 0);
            SHGetFileInfoW(path, generic ? FILE_ATTRIBUTE_NORMAL : 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags);
            return FromHIcon(info.hIcon);
        }
        catch { return null; }
    }

    /// <summary>
    /// Ícone do programa tal como o Windows o mostra: DisplayIcon do registry
    /// (com suporte a "caminho,índice" em exe/dll) → exe do desinstalador →
    /// 1º exe da pasta → ícone genérico de programa do Windows. Nunca devolve null
    /// em Windows saudável; resultado frozen (usável de qualquer thread).
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? ExtractIcon(InstalledProgram p)
    {
        // 1) DisplayIcon: a fonte oficial — é o que "Adicionar/Remover Programas" usa
        if (p.DisplayIcon.Length > 0)
        {
            var di = p.DisplayIcon.Trim().Trim('"');
            var index = 0;
            var comma = di.LastIndexOf(',');
            if (comma > 3 && int.TryParse(di[(comma + 1)..], out var idx)) { index = idx; di = di[..comma].Trim().Trim('"'); }
            di = Environment.ExpandEnvironmentVariables(di);

            if (File.Exists(di))
            {
                if (di.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var ico = new System.Windows.Media.Imaging.BitmapImage();
                        ico.BeginInit();
                        ico.UriSource = new Uri(di);
                        ico.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        ico.DecodePixelWidth = 48;
                        ico.EndInit();
                        ico.Freeze();
                        return ico;
                    }
                    catch { }
                }
                else
                {
                    // exe/dll com índice de recurso — extração exata
                    try
                    {
                        var large = new IntPtr[1];
                        if (ExtractIconExW(di, index, large, null, 1) > 0)
                        {
                            var img = FromHIcon(large[0]);
                            if (img != null) return img;
                        }
                    }
                    catch { }
                    var shell = ShellIcon(di);
                    if (shell != null) return shell;
                }
            }
        }

        // 2) exe do desinstalador (tem quase sempre o ícone da app embutido)
        if (p.HasUninstaller && !p.IsMsi)
        {
            var (exe, _) = SplitCommand(p.UninstallString);
            if (exe.Length > 0 && File.Exists(exe))
            {
                var img = ShellIcon(exe);
                if (img != null) return img;
            }
        }

        // 3) 1º exe da pasta de instalação
        if (p.InstallLocation.Length > 3 && Directory.Exists(p.InstallLocation))
        {
            try
            {
                var exe = Directory.EnumerateFiles(p.InstallLocation, "*.exe").FirstOrDefault();
                if (exe != null)
                {
                    var img = ShellIcon(exe);
                    if (img != null) return img;
                }
            }
            catch { }
        }

        // 4) ícone genérico de programa do Windows (o mesmo que o Explorer usa p/ exe sem ícone)
        return ShellIcon("programa.exe", generic: true);
    }

    /// <summary>Abre o regedit já posicionado na chave (via LastKey).</summary>
    public static void OpenInRegedit(string fullPath)
    {
        try
        {
            var normalized = fullPath
                .Replace("HKLM", "HKEY_LOCAL_MACHINE")
                .Replace("HKCU", "HKEY_CURRENT_USER");
            using var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Applets\Regedit");
            k.SetValue("LastKey", normalized);
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch { }
    }
}
