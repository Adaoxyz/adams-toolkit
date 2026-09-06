using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>Um tweak de rede/latência: aplica, reverte, sabe se está ativo.</summary>
public class NetTweak
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Aggressive { get; init; }
    /// <summary>Em fase de teste — mostra badge BETA e não vem pré-selecionado.</summary>
    public bool Beta { get; init; }
    /// <summary>Só dura enquanto a app está aberta (não persiste, não precisa de backup).</summary>
    public bool SessionOnly { get; init; }
    public Func<Task<string?>> Apply { get; init; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> Revert { get; init; } = () => Task.FromResult<string?>(null);
}

public record PingResult(int Sent, int Received, long Min, double Avg, long Max, double Jitter)
{
    public bool Ok => Received > 0;
    public string Summary => !Ok
        ? "sem resposta (firewall a bloquear ICMP?)"
        : $"{Avg:0} ms média • {Min}–{Max} ms • jitter {Jitter:0.#} ms" +
          (Sent > Received ? $" • {Sent - Received} perdidos" : "");
}

/// <summary>
/// "Boost" de rede honesto: não muda a rota até ao servidor (física), mas elimina
/// picos/jitter causados por configuração do Windows. Tudo o que altera fica
/// guardado em backup JSON e é 100% reversível com "Reverter tudo".
/// </summary>
public static class NetworkOptimizer
{
    // IP público do servidor WestRP (Hetzner) — alvo do teste de ping
    public const string ServerHost = "62.238.22.77";

    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "netopt.json");

    private class OptState
    {
        public Dictionary<string, Dictionary<string, string>> Backup { get; set; } = new();
        public HashSet<string> Applied { get; set; } = new();
        /// <summary>Instante de arranque do Windows quando o boost foi ativado — se mudar, o PC reiniciou.</summary>
        public long BootStamp { get; set; }
    }

    private static OptState _state = Load();

    private static OptState Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<OptState>(File.ReadAllText(StateFile)) ?? new OptState();
        }
        catch { }
        return new OptState();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_state));
        }
        catch { }
    }

    public static bool IsApplied(string id) => _state.Applied.Contains(id);
    public static bool AnyApplied => _state.Applied.Count > 0;

    // arranque do Windows em ticks Unix/min — arredondado ao minuto para ignorar drift do relógio
    private static long CurrentBootStamp()
    {
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return boot.ToUnixTimeSeconds() / 60;
    }

    /// <summary>
    /// Tarefa de arranque da app: se o boost ficou ativo de um boot anterior do Windows,
    /// reverte tudo automaticamente — o boost é por sessão, o jogador reativa quando quer.
    /// Devolve true se reverteu.
    /// </summary>
    public static Task<bool> StartupCheck { get; private set; } = Task.FromResult(false);

    public static void BeginStartupCheck() => StartupCheck = Task.Run(async () =>
    {
        if (!AnyApplied) return false;
        if (_state.BootStamp == CurrentBootStamp()) return false; // mesmo boot → boost continua válido
        await RevertAllAsync();
        return true;
    });

    // guarda o valor original UMA vez (o 1º apply é que vê o estado verdadeiro do PC)
    private static void BackupValue(string tweakId, string key, string value)
    {
        if (!_state.Backup.TryGetValue(tweakId, out var d))
            _state.Backup[tweakId] = d = new();
        d.TryAdd(key, value);
    }
    private static Dictionary<string, string> GetBackup(string tweakId) =>
        _state.Backup.TryGetValue(tweakId, out var d) ? d : new();

    // ---------- processos auxiliares ----------

    private static async Task<(int code, string output)> RunAsync(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static Task<(int code, string output)> RunPsAsync(string script) =>
        RunAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");

    private const string NoPerm = "sem permissão — reinicia a app como administrador";

    // Windows PT diz "Acesso negado", não "Access denied" — verificar ambos
    private static bool IsAccessDenied(string s) =>
        s.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("negado", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("denied", StringComparison.OrdinalIgnoreCase);

    // ---------- catálogo de tweaks ----------

    public static readonly IReadOnlyList<NetTweak> Tweaks = new List<NetTweak>
    {
        // ===== SEGUROS =====
        new()
        {
            Id = "powerplan", Name = "Energia em alto desempenho",
            Description = "Ativa o plano Alto Desempenho e põe o Wi-Fi em desempenho máximo. CPU/rede a adormecer é a causa nº1 de picos de lag. Se já usas um plano de desempenho próprio (Ultimate, Bitsum, otimizador), mantém o teu e só otimiza o Wi-Fi.",
            Apply = async () =>
            {
                var (c0, cur) = await RunAsync("powercfg", "/getactivescheme");
                var m = Regex.Match(cur, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
                if (c0 == 0 && m.Success) BackupValue("powerplan", "scheme", m.Value);

                // Só trocamos de plano se o ativo for um plano stock de poupança
                // (Balanced/Power Saver). Qualquer outro (Ultimate, Bitsum, plano
                // custom de otimizador) assume-se intencional e melhor-ou-igual —
                // trocar seria downgrade. Wi-Fi max-perf aplica-se na mesma abaixo.
                var active = m.Success ? m.Value.ToLowerInvariant() : "";
                var stockSavers = new[]
                {
                    "381b4222-f694-41f0-9685-ff5bb260df2e", // Balanced
                    "a1841308-3541-4fab-bc81-f71556f20b4a", // Power saver
                };
                if (active == "" || Array.IndexOf(stockSavers, active) >= 0)
                {
                    var (c1, _) = await RunAsync("powercfg", $"/setactive {HighPerfGuid}");
                    if (c1 != 0)
                    {
                        // alguns OEM removem o plano — duplica a partir do template
                        var (c2, dup) = await RunAsync("powercfg", $"-duplicatescheme {HighPerfGuid}");
                        var m2 = Regex.Match(dup, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
                        if (c2 != 0 || !m2.Success) return "não consegui ativar o plano Alto Desempenho";
                        await RunAsync("powercfg", $"/setactive {m2.Value}");
                    }
                }
                // Wi-Fi: Power Saving Mode → Maximum Performance (no plano agora ativo)
                await RunAsync("powercfg", "/setacvalueindex scheme_current 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0");
                await RunAsync("powercfg", "/setdcvalueindex scheme_current 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0");
                await RunAsync("powercfg", "/setactive scheme_current");
                return null;
            },
            Revert = async () =>
            {
                var old = GetBackup("powerplan").GetValueOrDefault("scheme");
                if (old == null) return null;
                var (c, _) = await RunAsync("powercfg", $"/setactive {old}");
                return c == 0 ? null : "não consegui restaurar o plano anterior";
            },
        },
        new()
        {
            Id = "throttling", Name = "Sem throttling de rede do Windows",
            Description = "O Windows limita pacotes de rede quando há multimédia a tocar (NetworkThrottlingIndex). Desliga o limite e prioriza jogos (SystemResponsiveness).",
            Apply = () => Task.FromResult(SetMultimediaProfile()),
            Revert = () => Task.FromResult(RestoreMultimediaProfile()),
        },
        new()
        {
            Id = "nicpower", Name = "Placa de rede sempre acordada",
            Description = "Impede o Windows de desligar a placa de rede/Wi-Fi para poupar energia — a causa clássica dos picos de 200+ ms em portáteis.",
            Apply = async () =>
            {
                var (code, output) = await RunPsAsync(
                    "$ids=(Get-NetAdapter -Physical -ErrorAction SilentlyContinue).PnPDeviceID; " +
                    "$l=Get-CimInstance -Namespace root/wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue; " +
                    "foreach($p in $l){ foreach($id in $ids){ if($p.InstanceName -like ($id+'*')){ " +
                    "Write-Output ($p.InstanceName+'='+$p.Enable); $p.Enable=$false; Set-CimInstance -CimInstance $p } } }");
                if (code != 0) return IsAccessDenied(output) ? NoPerm : $"falhou: {Trim(output)}";
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = line.LastIndexOf('=');
                    if (i > 0) BackupValue("nicpower", line[..i].Trim(), line[(i + 1)..].Trim());
                }
                return null;
            },
            Revert = async () =>
            {
                foreach (var (inst, val) in GetBackup("nicpower"))
                {
                    if (!val.Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
                    await RunPsAsync(
                        "$l=Get-CimInstance -Namespace root/wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue; " +
                        $"foreach($p in $l){{ if($p.InstanceName -eq '{inst}'){{ $p.Enable=$true; Set-CimInstance -CimInstance $p }} }}");
                }
                return null;
            },
        },

        new()
        {
            Id = "qos", Name = "Prioridade QoS para o WestRP", Beta = true,
            Description = "Marca os pacotes para o servidor WestRP como prioritários (DSCP 46). Se o teu router respeitar QoS, o jogo passa à frente de downloads/streaming de quem partilha a net contigo. Em routers que ignoram a marca não muda nada — testa e diz-nos.",
            Apply = async () =>
            {
                var err = SetQosPolicy(on: true);
                if (err != null) return err;
                await RunAsync("gpupdate", "/target:computer /force"); // política QoS só entra no refresh
                return null;
            },
            Revert = async () =>
            {
                var err = SetQosPolicy(on: false);
                if (err != null) return err;
                await RunAsync("gpupdate", "/target:computer /force");
                return null;
            },
        },

        // ===== AGRESSIVOS =====
        new()
        {
            Id = "intmod", Name = "Interrupt moderation desligado", Aggressive = true,
            Description = "A placa deixa de agrupar pacotes antes de avisar o CPU (menos 0,5–2 ms, mais uso de CPU). ⚠ Aplicar reinicia a placa — a net cai 1–2 segundos.",
            Apply = async () =>
            {
                // Keyword de registry padronizado (*InterruptModeration) em vez de
                // DisplayName — o nome visível varia por driver/idioma e fazia isto
                // falhar com erro vazio em muitas placas. Script único com try/catch:
                // o erro real (se houver) volta sempre numa linha ERR:.
                var (code, output) = await RunPsAsync(
                    "$ErrorActionPreference='Stop'; try { " +
                    "$props = Get-NetAdapterAdvancedProperty -RegistryKeyword '*InterruptModeration' -ErrorAction SilentlyContinue; " +
                    "if (-not $props) { Write-Output 'NOPROP'; exit 0 }; " +
                    "foreach ($p in $props) { Write-Output ('BK:'+$p.Name+'='+($p.RegistryValue -join ',')) }; " +
                    "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | " +
                    "Set-NetAdapterAdvancedProperty -RegistryKeyword '*InterruptModeration' -RegistryValue 0; " +
                    "exit 0 } catch { Write-Output ('ERR:'+$_.Exception.Message); exit 1 }");

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                var err = lines.FirstOrDefault(l => l.StartsWith("ERR:"));
                if (err != null) return IsAccessDenied(err) ? NoPerm : $"falhou: {err[4..].Trim()}";
                if (lines.Contains("NOPROP")) return "o driver desta placa não expõe esta opção";
                if (code != 0) return "o PowerShell não respondeu — tenta com a app como administrador";

                foreach (var l in lines.Where(l => l.StartsWith("BK:")))
                {
                    var i = l.IndexOf('=');
                    if (i > 3) BackupValue("intmod", l[3..i].Trim(), l[(i + 1)..].Trim());
                }
                return GetBackup("intmod").Count == 0 ? "o driver desta placa não expõe esta opção" : null;
            },
            Revert = async () =>
            {
                foreach (var (name, val) in GetBackup("intmod"))
                {
                    // backups novos guardam RegistryValue ("0"/"1"); antigos (< v1.42)
                    // guardavam DisplayValue ("Enabled"/"Disabled") — suportar ambos
                    var regVal = val.Split(',')[0].Trim();
                    await RunPsAsync(int.TryParse(regVal, out _)
                        ? $"Set-NetAdapterAdvancedProperty -Name '{name}' -RegistryKeyword '*InterruptModeration' -RegistryValue '{regVal}' -ErrorAction SilentlyContinue"
                        : $"Set-NetAdapterAdvancedProperty -Name '{name}' -DisplayName 'Interrupt Moderation' -DisplayValue '{val}' -ErrorAction SilentlyContinue");
                }
                return null;
            },
        },
        new()
        {
            Id = "nagle", Name = "Nagle desligado (TCP)", Aggressive = true,
            Description = "TcpAckFrequency/TCPNoDelay em todas as interfaces. O FiveM joga por UDP — isto só afeta serviços TCP (chat externo, etc.). Mais placebo que ganho.",
            Apply = () => Task.FromResult(SetNagle(off: true)),
            Revert = () => Task.FromResult(SetNagle(off: false)),
        },
        new()
        {
            Id = "delivery", Name = "Windows Update sem P2P", Aggressive = true,
            Description = "Impede o PC de enviar updates a outros PCs pela internet (Delivery Optimization) — largura de banda que rouba durante o jogo.",
            Apply = () => Task.FromResult(SetDeliveryOptimization(off: true)),
            Revert = () => Task.FromResult(SetDeliveryOptimization(off: false)),
        },
        new()
        {
            Id = "timer", Name = "Timer de 1 ms (enquanto a app está aberta)", Aggressive = true, SessionOnly = true,
            Description = "Força a resolução do timer do Windows a 1 ms — frames mais regulares em alguns PCs. Dura só enquanto o Adams Toolkit estiver aberto.",
            Apply = () => { TimeBeginPeriod(1); _timerActive = true; return Task.FromResult<string?>(null); },
            Revert = () => { if (_timerActive) { TimeEndPeriod(1); _timerActive = false; } return Task.FromResult<string?>(null); },
        },
    };

    // ---------- apply/revert ----------

    public static async Task<string?> ApplyAsync(NetTweak t)
    {
        var err = await t.Apply();
        if (err == null && !t.SessionOnly)
        {
            _state.Applied.Add(t.Id);
            _state.BootStamp = CurrentBootStamp();
            Save();
        }
        return err;
    }

    public static async Task<string?> RevertAsync(NetTweak t)
    {
        var err = await t.Revert();
        if (err == null) { _state.Applied.Remove(t.Id); _state.Backup.Remove(t.Id); Save(); }
        return err;
    }

    /// <summary>Reverte tudo o que alguma vez foi aplicado (pela ordem inversa).</summary>
    public static async Task<List<(string name, string error)>> RevertAllAsync()
    {
        var errors = new List<(string, string)>();
        foreach (var t in Tweaks.Reverse())
        {
            if (!t.SessionOnly && !IsApplied(t.Id)) continue;
            var err = await RevertAsync(t);
            if (err != null) errors.Add((t.Name, err));
        }
        return errors;
    }

    // ---------- tweaks em registry (síncronos, HKLM precisa de admin) ----------

    private static string? SetMultimediaProfile()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (k == null) return "chave SystemProfile não existe";
            BackupValue("throttling", "NetworkThrottlingIndex", k.GetValue("NetworkThrottlingIndex")?.ToString() ?? "<ausente>");
            BackupValue("throttling", "SystemResponsiveness", k.GetValue("SystemResponsiveness")?.ToString() ?? "<ausente>");
            k.SetValue("NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            k.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? RestoreMultimediaProfile()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (k == null) return null;
            foreach (var (name, val) in GetBackup("throttling"))
            {
                if (val == "<ausente>") k.DeleteValue(name, throwOnMissingValue: false);
                else if (int.TryParse(val, out var iv)) k.SetValue(name, iv, RegistryValueKind.DWord);
                else if (long.TryParse(val, out var lv)) k.SetValue(name, unchecked((int)lv), RegistryValueKind.DWord);
            }
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? SetNagle(bool off)
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        try
        {
            using var ifs = Registry.LocalMachine.OpenSubKey(root, writable: false);
            if (ifs == null) return "chave Interfaces não existe";
            foreach (var sub in ifs.GetSubKeyNames())
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"{root}\{sub}", writable: true);
                if (k == null) continue;
                if (off)
                {
                    BackupValue("nagle", $"{sub}|TcpAckFrequency", k.GetValue("TcpAckFrequency")?.ToString() ?? "<ausente>");
                    BackupValue("nagle", $"{sub}|TCPNoDelay", k.GetValue("TCPNoDelay")?.ToString() ?? "<ausente>");
                    k.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    k.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                }
            }
            if (!off)
            {
                foreach (var (key, val) in GetBackup("nagle"))
                {
                    var parts = key.Split('|');
                    if (parts.Length != 2) continue;
                    using var k = Registry.LocalMachine.OpenSubKey($@"{root}\{parts[0]}", writable: true);
                    if (k == null) continue;
                    if (val == "<ausente>") k.DeleteValue(parts[1], throwOnMissingValue: false);
                    else if (int.TryParse(val, out var iv)) k.SetValue(parts[1], iv, RegistryValueKind.DWord);
                }
            }
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    // Política QoS de máquina (mesmo formato que o gpedit exporta): marca DSCP 46 (EF)
    // em todo o tráfego para o IP do servidor. "Do not use NLA" faz o Windows aplicar
    // a marca também fora de redes de domínio (casa de toda a gente).
    private const string QosPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\QoS\WestRP FiveM";
    private const string QosTcpipPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";

    private static string? SetQosPolicy(bool on)
    {
        try
        {
            if (on)
            {
                using (var k = Registry.LocalMachine.CreateSubKey(QosPolicyPath))
                {
                    k.SetValue("Version", "1.0");
                    k.SetValue("Application Name", "*");
                    k.SetValue("Protocol", "*");
                    k.SetValue("Local Port", "*");
                    k.SetValue("Local IP", "*");
                    k.SetValue("Local IP Prefix Length", "*");
                    k.SetValue("Remote Port", "*");
                    k.SetValue("Remote IP", ServerHost);
                    k.SetValue("Remote IP Prefix Length", "32");
                    k.SetValue("DSCP Value", "46");
                    k.SetValue("Throttle Rate", "-1");
                }
                using (var k = Registry.LocalMachine.CreateSubKey(QosTcpipPath))
                {
                    BackupValue("qos", "Do not use NLA", k.GetValue("Do not use NLA")?.ToString() ?? "<ausente>");
                    k.SetValue("Do not use NLA", "1");
                }
            }
            else
            {
                Registry.LocalMachine.DeleteSubKey(QosPolicyPath, throwOnMissingSubKey: false);
                using var k = Registry.LocalMachine.OpenSubKey(QosTcpipPath, writable: true);
                if (k != null)
                {
                    var old = GetBackup("qos").GetValueOrDefault("Do not use NLA", "<ausente>");
                    if (old == "<ausente>") k.DeleteValue("Do not use NLA", throwOnMissingValue: false);
                    else k.SetValue("Do not use NLA", old);
                }
            }
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? SetDeliveryOptimization(bool off)
    {
        const string path = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
        try
        {
            if (off)
            {
                using var k = Registry.LocalMachine.CreateSubKey(path);
                BackupValue("delivery", "DODownloadMode", k.GetValue("DODownloadMode")?.ToString() ?? "<ausente>");
                k.SetValue("DODownloadMode", 0, RegistryValueKind.DWord);
            }
            else
            {
                using var k = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (k == null) return null;
                var old = GetBackup("delivery").GetValueOrDefault("DODownloadMode", "<ausente>");
                if (old == "<ausente>") k.DeleteValue("DODownloadMode", throwOnMissingValue: false);
                else if (int.TryParse(old, out var iv)) k.SetValue("DODownloadMode", iv, RegistryValueKind.DWord);
            }
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------- timer 1 ms ----------

    private static bool _timerActive;
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

    private static string Trim(string s) => s.Trim().Split('\n')[0].Trim();

    // ---------- teste de ping ----------

    public static async Task<PingResult> PingServerAsync(int count = 8)
    {
        var times = new List<long>();
        var sent = 0;
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(ServerHost, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch { }
            await Task.Delay(120);
        }
        if (times.Count == 0) return new PingResult(sent, 0, 0, 0, 0, 0);

        double jitter = 0;
        for (var i = 1; i < times.Count; i++) jitter += Math.Abs(times[i] - times[i - 1]);
        jitter = times.Count > 1 ? jitter / (times.Count - 1) : 0;

        return new PingResult(sent, times.Count, times.Min(), times.Average(), times.Max(), jitter);
    }
}
