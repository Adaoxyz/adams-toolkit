using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// Diagnóstico "o que trava este PC": as verificações do GameBoost (RAM, paginação,
/// disco, espaço, cache do FiveM, overlays) + CPU, memória de vídeo, plano de energia,
/// programas no arranque e RAM ocupada agora. SÓ LÊ — não altera nada.
/// </summary>
public static class PcDiagnosis
{
    public static async Task<List<DiagItem>> RunAsync()
    {
        var items = await GameBoost.DiagnoseAsync();
        await Task.Run(() =>
        {
            Try(items, Cpu);
            Try(items, Vram);
            Try(items, PowerPlan);
            Try(items, Startup);
            Try(items, RamNow);
        });
        // o mais grave primeiro: é isso que o jogador tem de ler
        return items.OrderBy(i => i.Level switch { "bad" => 0, "warn" => 1, _ => 2 }).ToList();
    }

    private static void Try(List<DiagItem> items, Func<DiagItem?> check)
    {
        try { if (check() is { } d) items.Add(d); } catch { }
    }

    private static DiagItem? Cpu()
    {
        var threads = Environment.ProcessorCount;
        int cores = 0;
        using (var s = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
            foreach (ManagementObject mo in s.Get()) cores += Convert.ToInt32(mo["NumberOfCores"]);
        var label = cores > 0 ? $"{cores} núcleos / {threads} threads" : $"{threads} threads";
        return threads <= 4
            ? new DiagItem("bad", $"Processador: {label}", "O FiveM com muita gente à volta pede pelo menos 6 threads. Com 4 ou menos, o CPU é o teu travão — fecha tudo o que não precisas (o Modo Jogo ajuda) e baixa a distância de visão no jogo.")
            : threads <= 6
                ? new DiagItem("warn", $"Processador: {label}", "Chega, mas em zonas cheias vais notar. O Modo Jogo tira os programas em fundo da frente do jogo.")
                : new DiagItem("ok", $"Processador: {label}", "Suficiente para FiveM.");
    }

    private static DiagItem? Vram()
    {
        long best = 0;
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return null;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            var v = k?.GetValue("HardwareInformation.qwMemorySize") ?? k?.GetValue("HardwareInformation.MemorySize");
            long bytes = v switch { long l => l, int i => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0 };
            best = Math.Max(best, bytes);
        }
        if (best <= 0) return null;
        var gb = best / 1073741824.0;
        return gb < 2.5
            ? new DiagItem("bad", $"Memória da placa gráfica: {gb:0.#} GB", "Pouca para FiveM (texturas de carros/roupas custom). Põe texturas em Normal e desliga o MSAA — é aqui que ganhas FPS.")
            : gb < 4.5
                ? new DiagItem("warn", $"Memória da placa gráfica: {gb:0.#} GB", "Dá, mas texturas em Muito Alto enchem-na e aparecem engasgos. Texturas em Alto é o ponto doce.")
                : new DiagItem("ok", $"Memória da placa gráfica: {gb:0.#} GB", "Suficiente.");
    }

    private static DiagItem? PowerPlan()
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        if (p == null) return null;
        var o = p.StandardOutput.ReadToEnd().ToLowerInvariant();
        p.WaitForExit(3000);
        if (o.Contains("a1841308-3541-4fab-bc81-f71556f20b4a"))
            return new DiagItem("bad", "Plano de energia: Poupança de energia", "O Windows está a travar o CPU de propósito. Liga \"Energia em alto desempenho\" na Otimização.");
        if (o.Contains("381b4222-f694-41f0-9685-ff5bb260df2e"))
            return new DiagItem("warn", "Plano de energia: Equilibrado", "O CPU baixa a velocidade entre picos — dá micro-engasgos. \"Energia em alto desempenho\" (Otimização) resolve enquanto jogas.");
        return new DiagItem("ok", "Plano de energia: desempenho", "O CPU não está a ser travado.");
    }

    private static DiagItem? Startup()
    {
        var n = StartupAppsManager.List().Count(a => a.Enabled);
        return n > 10
            ? new DiagItem("warn", $"{n} programas arrancam com o Windows", "Cada um come RAM e CPU o dia todo. Desliga os que não usas no Boost → Arranque do Windows.")
            : new DiagItem("ok", $"{n} programas arrancam com o Windows", "Número normal.");
    }

    private static DiagItem? RamNow()
    {
        var (pct, used, total) = SystemMonitor.GetRam();
        if (pct < 0) return null;
        return pct > 80
            ? new DiagItem("bad", $"RAM ocupada agora: {pct:0}% ({used:0.0}/{total:0.0} GB)", "Antes sequer de abrires o jogo. Ativa o Modo Jogo: põe os launchers em espera e devolve ao jogo a RAM que os programas em fundo não estão a usar.")
            : pct > 60
                ? new DiagItem("warn", $"RAM ocupada agora: {pct:0}% ({used:0.0}/{total:0.0} GB)", "Há programas a ocupar memória que o FiveM vai querer. O Modo Jogo ajuda.")
                : new DiagItem("ok", $"RAM ocupada agora: {pct:0}% ({used:0.0}/{total:0.0} GB)", "Folga suficiente para o jogo.");
    }
}
