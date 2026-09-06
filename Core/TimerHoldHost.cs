using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdamsToolkit.Core;

/// <summary>
/// Modo headless: o exe relançado com "--hold-timer &lt;parentPid&gt;" corre AQUI,
/// sem WPF nenhum. Segura a resolução máxima do timer e não faz mais nada —
/// como o processo não tem WPF a pedir 1ms, o pedido de 0.5ms nunca é pisado e
/// o timer do sistema fica cravado no máximo. Morre sozinho quando o Adams
/// Toolkit principal fecha (vigia o PID do pai).
/// </summary>
public static class TimerHoldHost
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desired, bool set, out uint cur);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int cls, ref PPTS info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PPTS { public uint Version; public uint ControlMask; public uint StateMask; }

    /// <summary>Corre o loop de segurar o timer. Só regressa quando o pai morre.</summary>
    public static void Run(string[] args)
    {
        int parentPid = 0;
        if (args.Length >= 2) int.TryParse(args[1], out parentPid);

        // opt-out do power throttling do Win11 (honra o pedido mesmo em background)
        try
        {
            var st = new PPTS { Version = 1, ControlMask = 4 /*IGNORE_TIMER_RESOLUTION*/, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), 4 /*ProcessPowerThrottling*/, ref st, Marshal.SizeOf<PPTS>());
        }
        catch { }

        Process? parent = null;
        try { if (parentPid > 0) parent = Process.GetProcessById(parentPid); } catch { }

        // aplica a máxima e re-afirma periodicamente (defensivo; sem WPF quase nunca é preciso)
        while (true)
        {
            if (NtQueryTimerResolution(out _, out var max, out var cur) == 0 && cur > max)
                NtSetTimerResolution(max, true, out _);

            if (parent != null)
            {
                try { if (parent.HasExited) break; }
                catch { break; }
            }
            System.Threading.Thread.Sleep(1000);
        }

        try { NtSetTimerResolution(0, false, out _); } catch { }
    }
}
