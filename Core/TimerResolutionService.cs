using System.Runtime.InteropServices;

namespace AdamsToolkit.Core;

/// <summary>
/// Réplica do TimerResolution (timerresolution.app): sobe a precisão do timer do
/// Windows de ~15.6ms para o máximo do hardware (normalmente 0.5ms) via ntdll.
/// O pedido é por processo e morre com ele — fechar a app repõe o padrão do
/// Windows instantaneamente, sem reboot e sem admin.
/// Unidades da API: 100ns (156250 = 15.625ms; 5000 = 0.5ms).
/// Atenção à semântica invertida do NT: "Minimum" é o valor MAIOR (pior precisão)
/// e "Maximum" o valor menor (melhor precisão).
/// </summary>
public static class TimerResolutionService
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desired, bool set, out uint current);

    // Windows 11 ignora pedidos de resolução de processos com janela minimizada /
    // em segundo plano (power throttling) — exatamente o cenário de quem ativa e
    // vai jogar. Opt-out explícito: ControlMask=IGNORE_TIMER_RESOLUTION, StateMask=0
    // = "honra SEMPRE os pedidos deste processo".
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4;
    private static bool _throttlingDisabled;

    private static void DisableTimerThrottling()
    {
        if (_throttlingDisabled) return;
        try
        {
            var st = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0,
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            _throttlingDisabled = true;
        }
        catch { } // Windows 10 antigo não tem a API — segue sem
    }

    /// <summary>Máxima aplicada por nós e ainda ativa.</summary>
    public static bool IsApplied => _holder is { HasExited: false };

    // O WPF pede 1ms via timeBeginPeriod durante animações/timers, no MESMO processo,
    // e isso pisava o nosso pedido de 0.5ms → 0.500 flip 1.000. Solução: um processo
    // FILHO dedicado (o próprio exe com --hold-timer), SEM WPF, que segura a máxima
    // sozinho. O timer do sistema fica no menor valor pedido por qualquer processo,
    // por isso o filho crava-o em 0.5ms independentemente do que o WPF do pai faça.
    private static System.Diagnostics.Process? _holder;

    /// <summary>(mínima, máxima, atual) em ms; (-1,-1,-1) se a query falhar.</summary>
    public static (double minMs, double maxMs, double currentMs) Query()
    {
        if (NtQueryTimerResolution(out var min, out var max, out var cur) != 0) return (-1, -1, -1);
        return (min / 10000.0, max / 10000.0, cur / 10000.0);
    }

    /// <summary>Trava o timer na melhor precisão do hardware (normalmente 0.5ms).</summary>
    public static bool SetMaximum()
    {
        if (IsApplied) return true;
        DisableTimerThrottling(); // o pai também opta-out, por garantia
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var me = System.Diagnostics.Process.GetCurrentProcess().Id;
            _holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                Arguments = $"--hold-timer {me}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return _holder != null;
        }
        catch { _holder = null; return false; }
    }

    /// <summary>Mata o segurador — o Windows volta ao timer padrão.</summary>
    public static bool RestoreDefault()
    {
        try { if (_holder is { HasExited: false }) _holder.Kill(); } catch { }
        _holder = null;
        NtSetTimerResolution(0, false, out _);
        return true;
    }
}
