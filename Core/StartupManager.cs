using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

// Arranque com o Windows: atalho .lnk na pasta Arranque do utilizador (por
// utilizador, sem admin). Opcional — preferência em startup.txt (default:
// ligado; sem ficheiro = ligado). Quando ligado, o atalho é re-escrito a cada
// arranque da app para o caminho acompanhar o exe (updates, mudança de pasta).
// Arranca com "--startup" → abre escondido na bandeja.
//
// NOTA (2026-09-01): antes disto a persistência era um valor em
// HKCU\...\CurrentVersion\Run. O Defender listava essa runkey como IoC no
// falso positivo Trojan:Win32/Bearfoos.A!ml — escrita na chave Run é dos
// sinais comportamentais mais pesados para o motor de ML. A pasta Arranque
// faz exatamente o mesmo, é o padrão normal de apps de utilizador, e não
// mexe no registo. O valor antigo na Run é apagado na migração.
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AdamsToolkit";
    private const string LinkName = "Adams Toolkit.lnk";

    private static string PrefPath => Path.Combine(ConfigService.DataDir, "startup.txt");

    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);

    public static bool IsEnabled
    {
        get
        {
            try { return File.ReadAllText(PrefPath).Trim() != "off"; }
            catch { return true; } // sem preferência gravada = ligado
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try { File.WriteAllText(PrefPath, enabled ? "on" : "off"); } catch { }
        Sync();
    }

    // Aplica a preferência. Chamado no boot da app e a cada mudança.
    public static void Sync()
    {
        RemoveLegacyRunKey(); // migração: a chave Run já não é usada, nunca

        try
        {
            if (!IsEnabled)
            {
                try { if (File.Exists(LinkPath)) File.Delete(LinkPath); } catch { }
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            if (!WriteShortcut(LinkPath, exe, "--startup"))
                WriteRunKeyFallback(exe); // COM indisponível/bloqueado → não perder a funcionalidade
        }
        catch { } // pasta Arranque bloqueada por política → app continua a funcionar normal
    }

    /// <summary>Apaga o valor deixado pelas versões &lt;= 1.88.1 na chave Run.</summary>
    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>
    /// Cria/atualiza o .lnk via WScript.Shell (COM late-bound — sem dependências
    /// extra). Devolve false se o COM não estiver disponível.
    /// </summary>
    private static bool WriteShortcut(string linkPath, string target, string args)
    {
        object? shell = null, link = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { linkPath });
            if (link == null) return false;

            var t = link.GetType();
            void Set(string prop, string value) =>
                t.InvokeMember(prop, BindingFlags.SetProperty, null, link, new object[] { value });

            Set("TargetPath", target);
            Set("Arguments", args);
            Set("WorkingDirectory", Path.GetDirectoryName(target) ?? "");
            Set("Description", "Adams Toolkit");
            Set("IconLocation", target + ",0");
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

            return File.Exists(linkPath);
        }
        catch { return false; }
        finally
        {
            if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Último recurso: o valor na chave Run, como nas versões antigas.</summary>
    private static void WriteRunKeyFallback(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            var cmd = $"\"{exe}\" --startup";
            if (key.GetValue(ValueName) as string != cmd) key.SetValue(ValueName, cmd);
        }
        catch { }
    }
}
