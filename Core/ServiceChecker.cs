using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

public enum SvcState { Running, Stopped, Disabled, Missing }

/// <summary>
/// Estado dos serviços Windows que os checks do servidor exigem ativos
/// (PcaSvc, DPS, DiagTrack, …).
///
/// Ativar = tipo de arranque AUTOMÁTICO (persiste ao reiniciar) + iniciar já.
/// Antes só se fazia "sc start" quando o serviço estava parado mas não
/// desativado — arrancava naquela sessão e voltava a ficar parado no reboot
/// seguinte (arranque Manual/trigger). Agora escreve-se sempre Start=2.
///
/// Como alguns PCs têm scripts de "debloat" / otimizadores que voltam a
/// desligar estes serviços a cada arranque, há ainda a proteção opcional
/// (<see cref="ApplyAsync"/> com guard): tarefa agendada a correr como SYSTEM
/// no arranque do Windows, que reaplica tudo sem UAC nem janelas.
///
/// A app corre asInvoker: escrever em HKLM exige admin, por isso o trabalho
/// real é feito por uma instância elevada da própria app (--fix-services),
/// lançada com UAC uma única vez.
/// </summary>
public static class ServiceChecker
{
    // Key = nome sc; CDPUserSvc é serviço por-utilizador — a instância real
    // chama-se CDPUserSvc_xxxxx (o Query resolve), mas o tipo de arranque
    // persistente vive no template (a instância é recriada em cada logon).
    public static readonly (string Key, string Display, string Detail)[] Monitored =
    {
        ("PcaSvc",     "PcaSvc",     "Assistente de Compatibilidade de Programas"),
        ("DPS",        "DPS",        "Serviço de Política de Diagnóstico"),
        ("DiagTrack",  "DiagTrack",  "Telemetria e experiências do utilizador"),
        ("SysMain",    "SysMain",    "Pré-carregamento de apps (Superfetch)"),
        ("EventLog",   "EventLog",   "Registo de Eventos do Windows"),
        ("SgrmBroker", "SgrmBroker", "System Guard Runtime Monitor"),
        ("CDPUserSvc", "CDPUserSvc", "Connected Devices Platform"),
    };

    private const string TaskName = "AdamsToolkit Servicos";
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    private static string GuardPrefPath => Path.Combine(ConfigService.DataDir, "services-guard.txt");

    /// <summary>Estado + nome real (instância por-utilizador quando aplicável).</summary>
    public static (SvcState State, string StartName) Query(string key)
    {
        ServiceController[] all;
        try { all = ServiceController.GetServices(); }
        catch { return (SvcState.Missing, key); }

        try
        {
            var sc = all.FirstOrDefault(s => s.ServiceName.Equals(key, StringComparison.OrdinalIgnoreCase))
                  ?? all.FirstOrDefault(s => s.ServiceName.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase));
            if (sc == null) return (SvcState.Missing, key);

            try
            {
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                    return (SvcState.Running, sc.ServiceName);
                return sc.StartType == ServiceStartMode.Disabled
                    ? (SvcState.Disabled, sc.ServiceName)
                    : (SvcState.Stopped, sc.ServiceName);
            }
            catch { return (SvcState.Stopped, sc.ServiceName); }
        }
        finally { foreach (var s in all) s.Dispose(); }
    }

    /// <summary>
    /// True quando o serviço arranca sozinho com o Windows (Start = 2 automático
    /// ou 1/0 = driver/boot). Manual (3) e Desativado (4) não persistem.
    /// </summary>
    public static bool StartsWithWindows(string key)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + key);
            return k?.GetValue("Start") is int start && start <= 2;
        }
        catch { return false; }
    }

    // ---------- ativação (elevada) ----------

    /// <summary>
    /// Põe todos os serviços monitorizados em arranque automático e inicia-os.
    /// Um único UAC. <paramref name="installGuard"/> instala/mantém também a
    /// tarefa SYSTEM que reaplica isto a cada arranque do Windows.
    /// Devolve false se o UAC for recusado.
    /// </summary>
    public static Task<bool> ApplyAsync(bool installGuard) =>
        RunElevatedAsync(installGuard ? "--fix-services --guard" : "--fix-services");

    /// <summary>Remove a tarefa de arranque (deixa os serviços como estão).</summary>
    public static Task<bool> RemoveGuardAsync() =>
        RunElevatedAsync("--fix-services --remove-guard");

    private static async Task<bool> RunElevatedAsync(string args)
    {
        // Já elevado (utilizador reiniciou a app como admin): faz no próprio
        // processo, sem UAC nem janela extra.
        if (UninstallerEngine.IsAdmin())
        {
            await Task.Run(() => RunHeadless(args.Split(' ')));
            return true;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (p == null) return false;
            await p.WaitForExitAsync();
            return true;
        }
        catch { return false; } // UAC recusado
    }

    // ---------- proteção no arranque ----------

    /// <summary>Tarefa de arranque instalada? (schtasks manda; pref é só fallback)</summary>
    public static bool GuardInstalled
    {
        get
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe")
                {
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null) return GuardPref;
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return GuardPref; }
                return p.ExitCode == 0;
            }
            catch { return GuardPref; }
        }
    }

    private static bool GuardPref
    {
        get { try { return File.ReadAllText(GuardPrefPath).Trim() == "on"; } catch { return false; } }
    }

    // ---------- modo headless (--fix-services), já elevado ----------

    /// <summary>
    /// Corre elevado (UAC direto ou tarefa SYSTEM): repõe arranque automático +
    /// inicia os serviços. Nunca mostra UI — falha em silêncio serviço a serviço.
    /// </summary>
    public static void RunHeadless(string[] args)
    {
        if (args.Contains("--remove-guard")) { SetGuard(false); return; }

        foreach (var (key, _, _) in Monitored)
        {
            var (state, startName) = Query(key);
            if (state == SvcState.Missing) continue;

            SetAutoStart(key);                       // persiste ao reiniciar
            if (state != SvcState.Running) Start(startName);
        }

        if (args.Contains("--guard")) SetGuard(true);
    }

    /// <summary>
    /// `sc.exe config &lt;nome&gt; start= auto`. ESTE e' o caminho certo para pôr um
    /// serviço em automático: passa pelo SCM (ChangeServiceConfig), que fica logo
    /// a saber. Escrever Start=2 no registo NÃO chega — o SCM tem a configuração
    /// em cache desde o arranque, por isso o `ServiceController.Start()` logo a
    /// seguir ainda rebentava com "The service cannot be started because it is
    /// disabled". Era esta a razão de o «Reparar» não reparar serviços desativados.
    /// </summary>
    internal static bool ScConfigAuto(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // "start=" e "auto" separados = o clássico `start= auto` (o espaço a seguir ao = é obrigatório no sc)
            foreach (var a in new[] { "config", name, "start=", "auto" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Automático (Start=2). Primeiro pelo SCM (sc config), que é o que faz efeito
    // já; o registo fica como rede de segurança para serviços que o sc recuse.
    // No serviço por-utilizador tem de ser no template (a instância _xxxxx é
    // recriada em cada logon a partir dele).
    private static void SetAutoStart(string key)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + key);
            if (k?.GetValue("Start") is int start && start <= 2) return; // já arranca sozinho
        }
        catch { }

        if (ScConfigAuto(key)) return;

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + key, writable: true);
            k?.SetValue("Start", 2, RegistryValueKind.DWord);
        }
        catch { } // política de grupo / serviço protegido
    }

    private static void Start(string name) => StartWithReason(name);

    /// <summary>Arranca o serviço. Devolve null se ficou a correr, ou o motivo da falha.</summary>
    internal static string? StartWithReason(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return null;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            // depende de outro serviço / protegido pelo Windows / bloqueado por política
            return ex.Message.Replace(Environment.NewLine, " ").Trim();
        }
    }

    // Tarefa como SYSTEM no arranque do Windows (1 min de atraso, para o
    // arranque não competir com o resto). ArgumentList evita o inferno das
    // aspas do /TR. Instalar/remover exige admin — já estamos elevados aqui.
    private static void SetGuard(bool on)
    {
        var exe = Environment.ProcessPath;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (on)
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                foreach (var a in new[]
                {
                    "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
                    "/SC", "ONSTART", "/DELAY", "0001:00",
                    "/TN", TaskName, "/TR", $"\"{exe}\" --fix-services",
                }) psi.ArgumentList.Add(a);
            }
            else
            {
                foreach (var a in new[] { "/Delete", "/F", "/TN", TaskName }) psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p != null && !p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
        }
        catch { }

        try { File.WriteAllText(GuardPrefPath, on ? "on" : "off"); } catch { }
    }
}
