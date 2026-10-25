using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace AdamsToolkit.Core;

public class GameModeOptions
{
    public bool PauseLaunchers { get; set; } = true;
    public bool LowerBrowsers { get; set; } = true;
    public bool TrimRam { get; set; } = true;
    public bool FiveMHigh { get; set; } = true;
    public bool AutoOff { get; set; } = true;
}

/// <summary>Um programa mexido pelo Modo Jogo (para a lista na UI e para repor).</summary>
public record GameModeEntry(int Pid, long StartTicks, string Name, string Action, int OriginalPriority);

/// <summary>
/// Modo Jogo de 1 clique: enquanto jogas, os programas pesados em fundo saem da frente
/// e ao desligar (ou quando o FiveM fecha) tudo volta EXATAMENTE como estava.
///
///  • launchers e sincronizações (OneDrive, Epic, EA, Ubisoft, Battle.net, Adobe, Widgets…)
///    ficam em prioridade MÍNIMA + modo eficiência — continuam abertos mas só usam CPU
///    que o jogo não quer. (NADA de NtSuspendProcess: é a função que os cheats usam para
///    congelar anti-cheats/screenshare, e um scanner que leia o exe marcava-a.)
///  • browsers e afins ficam com prioridade baixa + modo eficiência do Windows 11;
///  • a RAM que esses programas não estão a usar é devolvida ao Windows (para o jogo);
///  • o FiveM fica com prioridade alta (o mesmo do Boost).
///
/// Seguro para PC check: não apaga, não fecha e não desliga nada do sistema; nada fica
/// gravado no Windows. Nunca toca em Discord/TeamSpeak/OBS/Steam nem em processos do
/// sistema. Se a app rebentar com o modo ligado, o estado fica em game-mode.json e as
/// prioridades são repostas no arranque seguinte.
/// </summary>
public static class GameMode
{
    // em espera (prioridade mínima): só coisas que não fazem falta a meio de um jogo
    private static readonly HashSet<string> PauseList = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EALauncher", "upc", "UbisoftConnect",
        "Battle.net", "GalaxyClient", "GalaxyClientHelper", "CCXProcess", "CoreSync", "Creative Cloud",
        "AdobeIPCBroker", "PhoneExperienceHost", "Widgets", "WidgetService", "YourPhone",
    };

    // prioridade baixa + eficiência: continuam a funcionar, só deixam de competir com o jogo
    private static readonly HashSet<string> LowerList = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "steamwebhelper", "Teams", "ms-teams",
        "WhatsApp", "Telegram", // Spotify fica de fora: modo eficiência pode cortar a música
    };

    private static string StateFile => Path.Combine(ConfigService.DataDir, "game-mode.json");
    private static string OptsFile => Path.Combine(ConfigService.DataDir, "game-mode-opts.json");

    public static GameModeOptions Options { get; } = LoadOpts();
    public static bool Active { get; private set; }
    public static IReadOnlyList<GameModeEntry> Entries => _entries;
    public static event Action? Changed;

    private static readonly List<GameModeEntry> _entries = new();
    private static readonly DispatcherTimer Watch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private static bool _sawFiveM;
    private static int _missing;

    static GameMode() => Watch.Tick += (_, _) => Tick();

    private static GameModeOptions LoadOpts()
    {
        try { if (File.Exists(OptsFile)) return JsonSerializer.Deserialize<GameModeOptions>(File.ReadAllText(OptsFile)) ?? new(); }
        catch { }
        return new();
    }

    public static void SaveOpts()
    {
        try { File.WriteAllText(OptsFile, JsonSerializer.Serialize(Options)); } catch (Exception ex) { Log.Swallow("gravar opções do modo jogo", ex); }
    }

    // ---------- ligar / desligar ----------

    public static void Enable()
    {
        if (Active) return;
        Active = true;
        _sawFiveM = IsFiveMRunning();
        _missing = 0;
        Scan(trim: Options.TrimRam);
        if (Options.FiveMHigh) _ = GameBoost.ApplyAsync(GameBoost.Tweaks.First(t => t.Id == "prio"));
        Watch.Start();
        Log.Info($"modo jogo ligado ({_entries.Count} programas)", "modojogo");
        Changed?.Invoke();
    }

    public static void Disable(string reason = "")
    {
        if (!Active) return;
        Active = false;
        Watch.Stop();
        RestoreAll();
        if (Options.FiveMHigh) _ = GameBoost.RevertAsync(GameBoost.Tweaks.First(t => t.Id == "prio"));
        Log.Info("modo jogo desligado " + reason, "modojogo");
        Changed?.Invoke();
    }

    /// <summary>Arranque da app: se ficou algo pausado de uma sessão que rebentou, retoma.</summary>
    public static void RestoreLeftovers()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var old = JsonSerializer.Deserialize<List<GameModeEntry>>(File.ReadAllText(StateFile)) ?? new();
            _entries.AddRange(old);
            RestoreAll();
            Log.Info($"modo jogo: repostos {old.Count} programas de uma sessão anterior", "modojogo");
        }
        catch (Exception ex) { Log.Swallow("repor modo jogo anterior", ex); }
    }

    // ---------- trabalho ----------

    private static void Tick()
    {
        // FiveM fechou (visto antes, ausente 2 rondas seguidas = ~10 s) → repõe tudo sozinho
        var running = IsFiveMRunning();
        if (running) { _sawFiveM = true; _missing = 0; }
        else if (_sawFiveM && Options.AutoOff && ++_missing >= 2)
        {
            Disable("(o FiveM fechou)");
            MainWindowToast("Modo Jogo desligado — o FiveM fechou. Tudo voltou ao normal.");
            return;
        }
        Scan(trim: false); // programas abertos entretanto também saem da frente
    }

    private static void MainWindowToast(string text)
    {
        try { MainWindow.Toast(text); } catch { }
    }

    private static bool IsFiveMRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var n = p.ProcessName;
                    if (n.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase) && n.EndsWith("GTAProcess", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static void Scan(bool trim)
    {
        var me = Environment.ProcessId;
        var changed = false;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == me || p.SessionId == 0) continue; // nunca processos de sistema
                    var name = p.ProcessName;
                    var pause = Options.PauseLaunchers && PauseList.Contains(name);
                    var lower = Options.LowerBrowsers && LowerList.Contains(name);
                    if (!pause && !lower) continue;
                    if (_entries.Any(e => e.Pid == p.Id)) continue;

                    var start = p.StartTime.Ticks;
                    var prio = (int)p.PriorityClass;
                    p.PriorityClass = pause ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
                    SetEco(p.Handle, true);
                    _entries.Add(new GameModeEntry(p.Id, start, name, pause ? "em espera" : "prioridade baixa", prio));
                    if (trim) EmptyWorkingSet(p.Handle); // RAM que não estão a usar volta para o jogo
                    changed = true;
                }
                catch { /* sem acesso (elevado/protegido) ou já fechou: ignora */ }
            }
        }
        if (changed) { SaveState(); Changed?.Invoke(); }
    }

    private static void RestoreAll()
    {
        foreach (var e in _entries)
        {
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                if (p.StartTime.Ticks != e.StartTicks) continue; // PID reaproveitado: não é o mesmo programa
                p.PriorityClass = (ProcessPriorityClass)e.OriginalPriority;
                SetEco(p.Handle, false);
            }
            catch { /* já fechou */ }
        }
        _entries.Clear();
        try { File.Delete(StateFile); } catch { }
    }

    private static void SaveState()
    {
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(_entries)); }
        catch (Exception ex) { Log.Error("gravar estado do modo jogo falhou (se a app rebentar, repor à mão)", ex); }
    }

    // ---------- Win32 ----------

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);

    // ProcessPowerThrottling (4) / EXECUTION_SPEED (1): ligado = modo eficiência (EcoQoS, Win11);
    // desligar devolve a decisão ao Windows. Em Windows antigos falha em silêncio.
    private static void SetEco(IntPtr h, bool on)
    {
        try
        {
            var s = new PowerThrottling { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
            SetProcessInformation(h, 4, ref s, Marshal.SizeOf<PowerThrottling>());
        }
        catch { }
    }
}
