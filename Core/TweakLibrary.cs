using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>Um ajuste da biblioteca: estado real, nível de risco, aplicar/reverter.</summary>
public class LibTweak
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Desempenho · Rede · Sistema · Entrada</summary>
    public string Category { get; init; } = "Desempenho";
    /// <summary>true = risco médio (pede confirmação, nunca vem nos recomendados sem aviso).</summary>
    public bool RiskMedium { get; init; }
    public bool Recommended { get; init; }
    public bool NeedsReboot { get; init; }
    /// <summary>Nota de duração ("volta ao normal ao reiniciar", "enquanto a app está aberta").</summary>
    public string? Lifetime { get; init; }
    public Func<bool> IsActive { get; init; } = () => false;
    public Func<Task<string?>> Apply { get; init; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> Revert { get; init; } = () => Task.FromResult<string?>(null);
}

/// <summary>
/// Biblioteca única de ajustes: os do Boost de FPS e do boost de rede (que continuam
/// a viver nos seus módulos, com as regras deles) + ajustes novos daqui.
///
/// REGRA (igual ao <see cref="GameBoost"/>): só definições de desempenho, todas
/// reversíveis. NUNCA apaga rasto que um PC check procura — Prefetch, event logs,
/// USN, cache DNS, ARP, histórico, temporários — nem desliga serviços que o
/// <see cref="ServiceChecker"/> exige (SysMain, Search, telemetria/diagnóstico…).
/// Por isso não há aqui "limpar cache DNS", "limpar ARP" nem "desligar telemetria",
/// que outras apps têm.
///
/// Os ajustes novos são persistentes (ficam até carregares em Reverter); o estado
/// mostrado é sempre lido do sistema, não de uma flag guardada.
/// </summary>
public static class TweakLibrary
{
    private static string StateFile => Path.Combine(ConfigService.DataDir, "tweaks.json");
    private const string Absent = "<ausente>";
    private const string NoPerm = "sem permissão — reinicia a app como administrador";

    // backup do valor original, por ajuste: "HIVE|caminho|valor" → "d:1" / "s:abc" / Absent
    private static Dictionary<string, Dictionary<string, string>> _backup = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex) { Log.Swallow("ler backup dos ajustes", ex); }
        return new();
    }

    private static void Save()
    {
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(_backup)); }
        catch (Exception ex) { Log.Error("gravar backup dos ajustes falhou (reverter pode não repor tudo)", ex); }
    }

    // ---------- registo ----------

    private static RegistryKey RootOf(string hive) => hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    private static object? Read(string hive, string path, string name)
    {
        try { using var k = RootOf(hive).OpenSubKey(path); return k?.GetValue(name); }
        catch { return null; }
    }

    private static bool Is(string hive, string path, string name, object expected) =>
        Read(hive, path, name)?.ToString() == expected.ToString();

    private static string? Write(string id, string hive, string path, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var k = RootOf(hive).CreateSubKey(path);
            if (k == null) return $"não consegui abrir {hive}\\{path}";
            var cur = k.GetValue(name);
            if (!_backup.TryGetValue(id, out var d)) _backup[id] = d = new();
            d.TryAdd($"{hive}|{path}|{name}",
                cur == null ? Absent : (k.GetValueKind(name) == RegistryValueKind.DWord ? $"d:{cur}" : $"s:{cur}"));
            k.SetValue(name, value, kind);
            Save();
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (System.Security.SecurityException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? Restore(string id)
    {
        if (!_backup.TryGetValue(id, out var d)) return null;
        foreach (var (key, val) in d)
        {
            var p = key.Split('|');
            if (p.Length != 3) continue;
            try
            {
                using var k = RootOf(p[0]).OpenSubKey(p[1], writable: true);
                if (k == null) continue;
                if (val == Absent) k.DeleteValue(p[2], throwOnMissingValue: false);
                else if (val.StartsWith("d:") && int.TryParse(val[2..], out var iv)) k.SetValue(p[2], iv, RegistryValueKind.DWord);
                else if (val.StartsWith("s:")) k.SetValue(p[2], val[2..], RegistryValueKind.String);
            }
            catch (UnauthorizedAccessException) { return NoPerm; }
            catch (Exception ex) { return ex.Message; }
        }
        _backup.Remove(id);
        Save();
        return null;
    }

    private static Task<string?> Done(string? err) => Task.FromResult(err);

    /// <summary>Vários valores de uma vez; pára no primeiro erro.</summary>
    private static string? WriteAll(string id, params (string hive, string path, string name, object value, RegistryValueKind kind)[] vals)
    {
        foreach (var v in vals)
        {
            var err = Write(id, v.hive, v.path, v.name, v.value, v.kind);
            if (err != null) return err;
        }
        return null;
    }

    // ---------- rato (aplica na hora, sem terminar sessão) ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] pv, uint winIni);
    private const uint SpiSetMouse = 0x0004, SpifUpdateIni = 0x01, SpifSendChange = 0x02;

    private const string MouseKey = @"Control Panel\Mouse";

    private static void PushMouseToSession()
    {
        int P(string n) => int.TryParse(Read("HKCU", MouseKey, n)?.ToString(), out var v) ? v : 0;
        try { SystemParametersInfo(SpiSetMouse, 0, new[] { P("MouseThreshold1"), P("MouseThreshold2"), P("MouseSpeed") }, SpifUpdateIni | SpifSendChange); }
        catch (Exception ex) { Log.Swallow("aplicar rato na sessão", ex); }
    }

    // ---------- Core Parking (powercfg, por plano de energia ativo) ----------

    private static async Task<(int code, string output)> RunAsync(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return (-1, "");
            var o = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    // "Índice da Configuração de Energia de CA Atual: 0x00000064" — o texto muda com a
    // língua do Windows, mas os dois ÚLTIMOS hex da consulta são sempre CA e CC.
    private static async Task<(int ac, int dc)?> QueryMinCoresAsync()
    {
        var (code, o) = await RunAsync("powercfg", "/query scheme_current sub_processor CPMINCORES");
        if (code != 0) return null;
        var hex = Regex.Matches(o, @"0x([0-9a-fA-F]{8})").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        return hex.Count >= 2 ? (hex[^2], hex[^1]) : null;
    }

    private static int? _minCoresCache; // IsActive é síncrono e chamado pela UI: cache do último valor lido

    public static async Task RefreshSlowStateAsync()
    {
        _minCoresCache = (await QueryMinCoresAsync())?.ac;
        _activeScheme = await ActiveSchemeAsync();
    }

    // ---------- plano de energia Adams (desempenho máximo, serve em qualquer PC) ----------

    private const string AdamsPlan = "4dd1a5e7-0b5f-4c1e-9a6d-5f3a2c7e8b10";
    // origem por ordem de preferência: Desempenho Máximo → Alto Desempenho → Equilibrado.
    // Portáteis com Modern Standby só têm o Equilibrado: duplicá-lo e afinar funciona sempre.
    private static readonly string[] PlanSources =
        { "e9a42b02-d5df-448d-aa00-03f14749eb61", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "381b4222-f694-41f0-9685-ff5bb260df2e" };
    private static string? _activeScheme;

    private static async Task<string?> ActiveSchemeAsync()
    {
        var (code, o) = await RunAsync("powercfg", "/getactivescheme");
        var m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return code == 0 && m.Success ? m.Value.ToLowerInvariant() : null;
    }

    // só LIGADO À CORRENTE (AC): em bateria o plano fica como o de origem
    private static readonly (string sub, string setting, int value)[] PlanAc =
    {
        ("SUB_PROCESSOR", "PROCTHROTTLEMIN", 100),   // CPU nunca abaixo de 100%
        ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 100),
        ("SUB_PROCESSOR", "PERFBOOSTMODE", 2),       // boost agressivo
        ("SUB_PROCESSOR", "CPMINCORES", 100),        // sem estacionar núcleos
        ("SUB_DISK", "DISKIDLE", 0),                 // disco nunca adormece
        ("SUB_PCIEXPRESS", "ASPM", 0),               // PCI-Express sem poupança (GPU/NVMe)
        ("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0), // USB sem suspensão seletiva
        ("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0), // Wi-Fi em desempenho máximo
    };

    private static async Task<string?> ApplyAdamsPlanAsync()
    {
        var prev = await ActiveSchemeAsync();
        if (prev != null && prev != AdamsPlan && !_backup.ContainsKey("adamsplan"))
        {
            _backup["adamsplan"] = new() { ["prev"] = prev };
            Save();
        }

        var (_, list) = await RunAsync("powercfg", "/list");
        if (!list.Contains(AdamsPlan, StringComparison.OrdinalIgnoreCase))
        {
            var made = false;
            foreach (var src in PlanSources)
            {
                var (code, o) = await RunAsync("powercfg", $"/duplicatescheme {src} {AdamsPlan}");
                if (code == 0) { made = true; break; }
                if (o.Contains("acesso", StringComparison.OrdinalIgnoreCase) || o.Contains("access", StringComparison.OrdinalIgnoreCase)) return NoPerm;
            }
            if (!made) return "o Windows não deixou criar o plano de energia";
            await RunAsync("powercfg", $"/changename {AdamsPlan} \"Adams Toolkit — Desempenho\" \"Desempenho máximo ligado à corrente (Adams Toolkit)\"");
        }
        // definições que não existem num PC (ex.: sem Wi-Fi) falham sozinhas e ignoram-se
        foreach (var (sub, setting, value) in PlanAc)
            await RunAsync("powercfg", $"/setacvalueindex {AdamsPlan} {sub} {setting} {value}");
        var (ok, _) = await RunAsync("powercfg", $"/setactive {AdamsPlan}");
        await RefreshSlowStateAsync();
        return ok == 0 ? null : "o Windows recusou ativar o plano";
    }

    private static async Task<string?> RevertAdamsPlanAsync()
    {
        var prev = _backup.TryGetValue("adamsplan", out var d) ? d.GetValueOrDefault("prev") : null;
        // plano anterior apagado entretanto? volta ao Equilibrado, que existe em todos os PCs
        var (code, _) = await RunAsync("powercfg", $"/setactive {prev ?? PlanSources[2]}");
        if (code != 0) await RunAsync("powercfg", $"/setactive {PlanSources[2]}");
        _backup.Remove("adamsplan");
        Save();
        await RefreshSlowStateAsync();
        return null; // o plano Adams fica na lista do Windows (desativado) — reaplicar é instantâneo
    }

    private static async Task<string?> SetMinCoresAsync(int ac, int dc)
    {
        var a = await RunAsync("powercfg", $"/setacvalueindex scheme_current sub_processor CPMINCORES {ac}");
        var b = await RunAsync("powercfg", $"/setdcvalueindex scheme_current sub_processor CPMINCORES {dc}");
        await RunAsync("powercfg", "/setactive scheme_current");
        await RefreshSlowStateAsync();
        if (a.code != 0 || b.code != 0)
            return a.output.Contains("acesso", StringComparison.OrdinalIgnoreCase) || a.output.Contains("access", StringComparison.OrdinalIgnoreCase)
                ? NoPerm : "o powercfg recusou a alteração";
        return null;
    }

    // ---------- catálogo ----------

    private static readonly List<LibTweak> Own = new()
    {
        new()
        {
            Id = "adamsplan", Category = "Desempenho", Recommended = true,
            Name = "Plano de energia Adams (desempenho máximo)",
            Description = "Cria e ativa o plano \"Adams Toolkit — Desempenho\", feito a partir do melhor plano que o teu PC tem (Desempenho Máximo, Alto Desempenho ou Equilibrado — serve em qualquer PC). Ligado à corrente: CPU sempre a 100%, núcleos sempre acordados, boost agressivo, PCI-Express, USB e Wi-Fi sem poupança. Em bateria fica normal. Reverter volta ao plano que tinhas. Precisa de admin.",
            IsActive = () => _activeScheme == AdamsPlan,
            Apply = ApplyAdamsPlanAsync,
            Revert = RevertAdamsPlanAsync,
        },
        new()
        {
            Id = "powerthrottle", Category = "Desempenho", Recommended = true,
            Name = "Desativar Power Throttling",
            Description = "Impede o Windows de abrandar programas em segundo plano para poupar energia — o FiveM e o Discord deixam de levar travão quando não estão em foco. Precisa de admin.",
            IsActive = () => Is("HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1),
            Apply = () => Done(Write("powerthrottle", "HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, RegistryValueKind.DWord)),
            Revert = () => Done(Restore("powerthrottle")),
        },
        new()
        {
            Id = "coreparking", Category = "Desempenho", RiskMedium = true,
            Name = "Desativar Core Parking",
            Description = "Mantém todos os núcleos do CPU acordados em vez de os \"estacionar\" — menos micro-engasgos quando o jogo pede CPU de repente. Num portátil gasta mais bateria e aquece um pouco mais.",
            IsActive = () => _minCoresCache == 100,
            Apply = async () =>
            {
                var cur = await QueryMinCoresAsync();
                if (cur == null) return "não consegui ler o plano de energia";
                if (!_backup.ContainsKey("coreparking"))
                {
                    _backup["coreparking"] = new() { ["ac"] = cur.Value.ac.ToString(), ["dc"] = cur.Value.dc.ToString() };
                    Save();
                }
                return await SetMinCoresAsync(100, 100);
            },
            Revert = async () =>
            {
                var b = _backup.TryGetValue("coreparking", out var d) ? d : null;
                int.TryParse(b?.GetValueOrDefault("ac"), out var ac);
                int.TryParse(b?.GetValueOrDefault("dc"), out var dc);
                var err = await SetMinCoresAsync(b == null ? 0 : ac, b == null ? 0 : dc);
                if (err == null) { _backup.Remove("coreparking"); Save(); }
                return err;
            },
        },
        new()
        {
            Id = "gamemode", Category = "Desempenho", Recommended = true,
            Name = "Modo de Jogo do Windows",
            Description = "Liga o Modo de Jogo: o Windows dá prioridade ao jogo e adia atualizações e notificações enquanto jogas.",
            // sem valor = ligado (é o padrão do Windows 10/11)
            IsActive = () => Read("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled")?.ToString() != "0",
            Apply = () => Done(WriteAll("gamemode",
                ("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord),
                ("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord))),
            Revert = () => Done(Restore("gamemode")),
        },
        new()
        {
            Id = "hags", Category = "Desempenho", RiskMedium = true, NeedsReboot = true,
            Name = "Agendamento de GPU por hardware",
            Description = "A placa gráfica passa a gerir a própria memória de vídeo — menos latência em placas recentes (NVIDIA 10xx+/AMD 5xxx+). Nalguns PCs causa engasgos; se notares, reverte. Só conta depois de reiniciar o PC. Precisa de admin.",
            IsActive = () => Is("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2),
            Apply = () => Done(Write("hags", "HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord)),
            Revert = () => Done(Restore("hags")),
        },
        new()
        {
            Id = "bgapps", Category = "Desempenho",
            Name = "Apps da Loja sem segundo plano",
            Description = "As apps da Microsoft Store deixam de correr escondidas. Atenção: essas apps (ex.: Mail, Telemóvel) deixam de mandar notificações com elas fechadas. Não afeta programas normais como o Discord ou o Steam.",
            IsActive = () => Is("HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
            Apply = () => Done(Write("bgapps", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord)),
            Revert = () => Done(Restore("bgapps")),
        },
        new()
        {
            Id = "widgets", Category = "Sistema", Recommended = true,
            Name = "Desligar Widgets e Notícias",
            Description = "O painel de Widgets (Win11) / Notícias e interesses (Win10) fica a correr escondido e come 100–300 MB de RAM — muito num PC com 8 GB. Fica desligado por política do Windows. Precisa de admin.",
            IsActive = () => Is("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
            Apply = () => Done(WriteAll("widgets",
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, RegistryValueKind.DWord),
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0, RegistryValueKind.DWord))),
            Revert = () => Done(Restore("widgets")),
        },
        new()
        {
            Id = "edgebg", Category = "Sistema",
            Name = "Edge sem arrancar escondido",
            Description = "O Edge abre-se em segundo plano com o Windows (\"Startup boost\") e fica na memória mesmo fechado. Isto desliga as duas coisas — o Edge continua a funcionar normal quando o abres. Aviso: o Edge passa a mostrar \"gerido pela tua organização\" nas definições; é só porque usa uma política. Precisa de admin.",
            IsActive = () => Is("HKLM", @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
            Apply = () => Done(WriteAll("edgebg",
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, RegistryValueKind.DWord),
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, RegistryValueKind.DWord))),
            Revert = () => Done(Restore("edgebg")),
        },
        new()
        {
            Id = "transparency", Category = "Sistema", Recommended = true,
            Name = "Desativar transparência",
            Description = "Tira o efeito de vidro da barra de tarefas e do menu Iniciar. Menos trabalho para a GPU, sobretudo em PCs fracos.",
            IsActive = () => Is("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
            Apply = () => Done(Write("transparency", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord)),
            Revert = () => Done(Restore("transparency")),
        },
        new()
        {
            Id = "startupdelay", Category = "Sistema", Recommended = true,
            Name = "Arranque sem atraso",
            Description = "O Windows espera uns segundos antes de abrir os programas de arranque. Isto tira essa espera — o PC fica pronto mais cedo depois de ligar.",
            IsActive = () => Is("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),
            Apply = () => Done(Write("startupdelay", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, RegistryValueKind.DWord)),
            Revert = () => Done(Restore("startupdelay")),
        },
        new()
        {
            Id = "mouseaccel", Category = "Entrada", Recommended = true,
            Name = "Rato sem aceleração",
            Description = "Desliga \"Melhorar precisão do ponteiro\": o mesmo movimento da mão dá sempre a mesma distância no ecrã — essencial para a mira. Aplica na hora.",
            IsActive = () => Is("HKCU", MouseKey, "MouseSpeed", "0"),
            Apply = () =>
            {
                var err = WriteAll("mouseaccel",
                    ("HKCU", MouseKey, "MouseSpeed", "0", RegistryValueKind.String),
                    ("HKCU", MouseKey, "MouseThreshold1", "0", RegistryValueKind.String),
                    ("HKCU", MouseKey, "MouseThreshold2", "0", RegistryValueKind.String));
                PushMouseToSession();
                return Done(err);
            },
            Revert = () => { var err = Restore("mouseaccel"); PushMouseToSession(); return Done(err); },
        },
    };

    private static LibTweak Wrap(NetTweak t, string category, bool fromGame)
    {
        // os dois boosts antigos são por sessão de Windows (regra do dono) — a nota diz isso
        return new LibTweak
        {
            Id = (fromGame ? "game:" : "net:") + t.Id,
            Name = t.Name,
            Description = t.Description,
            Category = category,
            RiskMedium = t.Aggressive,
            Recommended = !t.Aggressive && !t.Beta,
            Lifetime = "volta ao normal ao reiniciar o PC",
            IsActive = () => fromGame ? GameBoost.IsApplied(t.Id) : NetworkOptimizer.IsApplied(t.Id),
            Apply = () => fromGame ? GameBoost.ApplyAsync(t) : NetworkOptimizer.ApplyAsync(t),
            Revert = () => fromGame ? GameBoost.RevertAsync(t) : NetworkOptimizer.RevertAsync(t),
        };
    }

    /// <summary>Tudo, pela ordem Desempenho → Rede → Sistema → Entrada.</summary>
    public static IReadOnlyList<LibTweak> All { get; } = BuildAll();

    private static List<LibTweak> BuildAll()
    {
        var list = new List<LibTweak>(Own);
        // os "só enquanto a app está aberta" não guardam estado — ficam na aba Otimizações
        foreach (var t in GameBoost.Tweaks.Where(t => !t.SessionOnly))
            list.Add(Wrap(t, t.Id == "accesskeys" ? "Entrada" : "Desempenho", fromGame: true));
        foreach (var t in NetworkOptimizer.Tweaks.Where(t => !t.SessionOnly && t.Id != "powerplan"))
            list.Add(Wrap(t, "Rede", fromGame: false));
        string[] order = { "Desempenho", "Rede", "Sistema", "Entrada" };
        return list.OrderBy(t => Array.IndexOf(order, t.Category)).ThenBy(t => t.RiskMedium).ToList();
    }
}
