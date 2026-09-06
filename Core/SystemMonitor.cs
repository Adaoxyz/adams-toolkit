using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// Métricas de desempenho para o dashboard: CPU/GPU/RAM/disco/ping/uptime.
/// Tudo via contadores nativos do Windows — sem dependências externas.
/// Qualquer leitura que falhe devolve -1 e a UI mostra "—".
/// </summary>
public static class SystemMonitor
{
    // ---- CPU ----
    private static PerformanceCounter? _cpu;

    public static double GetCpuUsage()
    {
        try
        {
            _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            return Math.Clamp(_cpu.NextValue(), 0, 100);
        }
        catch { return -1; }
    }

    // ---- GPU (soma dos engines 3D; Win10+) ----
    private static List<PerformanceCounter>? _gpuCounters;
    private static DateTime _gpuRefreshed = DateTime.MinValue;

    public static double GetGpuUsage()
    {
        try
        {
            // Enumerar as instâncias da categoria "GPU Engine" é a parte cara
            // (centenas de instâncias) — de 2 em 2 minutos chega; entre refrescos
            // reutilizam-se os contadores já abertos.
            if (_gpuCounters == null || DateTime.UtcNow - _gpuRefreshed > TimeSpan.FromMinutes(2))
            {
                _gpuCounters?.ForEach(c => c.Dispose());
                var cat = new PerformanceCounterCategory("GPU Engine");
                _gpuCounters = cat.GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n))
                    .ToList();
                _gpuCounters.ForEach(c => c.NextValue()); // primeira amostra é sempre 0
                _gpuRefreshed = DateTime.UtcNow;
                return 0;
            }
            double sum = 0;
            foreach (var c in _gpuCounters)
            {
                try { sum += c.NextValue(); } catch { }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch { return -1; }
    }

    /// <summary>
    /// Larga os contadores de desempenho quando a app sai de vista (bandeja/jogo
    /// à frente). Sem isto ficavam handles abertos do PDH a nada fazer; a próxima
    /// leitura reabre-os sozinha.
    /// </summary>
    public static void ReleaseCounters()
    {
        try
        {
            _cpu?.Dispose(); _cpu = null;
            _gpuCounters?.ForEach(c => c.Dispose());
            _gpuCounters = null;
            _gpuRefreshed = DateTime.MinValue;
        }
        catch { }
    }

    public static string GetGpuName()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
            {
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4).OrderBy(n => n))
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && desc.Length > 0)
                        return desc;
                }
            }
        }
        catch { }
        return "PC";
    }

    // ---- RAM ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile,
                     ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public static (double pct, double usedGb, double totalGb) GetRam()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return (-1, 0, 0);
            var total = m.ullTotalPhys / 1073741824.0;
            var used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            return (m.dwMemoryLoad, used, total);
        }
        catch { return (-1, 0, 0); }
    }

    // ---- Disco do sistema ----
    public static (double pct, double freeGb, double totalGb) GetSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            var total = d.TotalSize / 1073741824.0;
            var free = d.TotalFreeSpace / 1073741824.0;
            return (total <= 0 ? -1 : (total - free) * 100.0 / total, free, total);
        }
        catch { return (-1, 0, 0); }
    }

    // ---- Ping ----
    public static async Task<long> PingAsync()
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 3000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    // ---- Sistema ----
    public static string GetOsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            // Win11 mantém "Windows 10" no ProductName — corrigir pelo build
            if (int.TryParse(k?.GetValue("CurrentBuild") as string, out var build) && build >= 22000)
                name = name.Replace("Windows 10", "Windows 11");
            return name;
        }
        catch { return "Windows"; }
    }

    public static string GetUptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";
    }

    // ---- temperaturas ----
    // Sem driver de kernel (LibreHardwareMonitor) as fontes nativas são limitadas:
    // CPU/sistema via zona térmica ACPI (WMI), GPU via nvidia-smi. -1 = não exposto.

    // Os fallbacks são caros: o ACPI é uma consulta WMI e o nvidia-smi é um
    // processo novo de cada vez. Em PCs onde a LHM não chega, corriam a cada
    // ciclo — aqui passam a correr no máximo de 20 em 20 segundos, com o último
    // valor a preencher os ciclos pelo meio.
    private static readonly TimeSpan FallbackTtl = TimeSpan.FromSeconds(20);
    private static double _cpuFallback = -1, _gpuFallback = -1;
    private static DateTime _cpuFallbackAt = DateTime.MinValue, _gpuFallbackAt = DateTime.MinValue;

    /// <summary>Lê as duas temperaturas de uma vez (LHM); fallback por sensor abaixo.</summary>
    public static (double cpu, double gpu) GetTemps()
    {
        var (cpu, gpu) = HardwareMonitor.ReadTemps();
        // Placas/CPUs muito recentes que a LHM não conhece devolvem lixo (ex.: 255°C,
        // um sentinela). Descartamos valores absurdos e caímos para nvidia-smi / ACPI.
        if (!Sane(cpu)) cpu = CachedFallback(ref _cpuFallback, ref _cpuFallbackAt, GetCpuTempC);
        if (!Sane(gpu)) gpu = CachedFallback(ref _gpuFallback, ref _gpuFallbackAt, GetGpuTempC);
        return (Sane(cpu) ? cpu : -1, Sane(gpu) ? gpu : -1);
    }

    private static double CachedFallback(ref double cache, ref DateTime at, Func<double> read)
    {
        if (DateTime.UtcNow - at < FallbackTtl) return cache;
        cache = read();
        at = DateTime.UtcNow;
        return cache;
    }

    /// <summary>Temperatura plausível? (fora deste intervalo = leitura inválida)</summary>
    private static bool Sane(double t) => t is > 0 and < 125;

    /// <summary>Temperatura do CPU/sistema em °C via ACPI thermal zone; -1 se indisponível.</summary>
    public static double GetCpuTempC()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double best = -1;
            foreach (var o in searcher.Get())
            {
                // CurrentTemperature vem em décimos de Kelvin
                if (o["CurrentTemperature"] is not null &&
                    double.TryParse(o["CurrentTemperature"].ToString(), out var raw))
                {
                    var c = raw / 10.0 - 273.15;
                    if (c is > 0 and < 130 && c > best) best = c;
                }
            }
            return best;
        }
        catch { return -1; }
    }

    /// <summary>Temperatura da GPU NVIDIA em °C via nvidia-smi; -1 se indisponível.</summary>
    public static double GetGpuTempC()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2500);
            var first = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return int.TryParse(first, out var t) ? t : -1;
        }
        catch { return -1; } // nvidia-smi ausente (sem NVIDIA)
    }
}
