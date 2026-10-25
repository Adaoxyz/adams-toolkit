using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AdamsToolkit.Core;

public class OverlayMetric
{
    public string Key { get; set; } = "";
    public bool On { get; set; } = true;
    public string Color { get; set; } = "#FFFFFF";
}

public class OverlaySettings
{
    public bool Enabled { get; set; }
    /// <summary>TL · TR · BL · BR</summary>
    public string Position { get; set; } = "TL";
    public double FontSize { get; set; } = 13;
    public List<OverlayMetric> Metrics { get; set; } = new()
    {
        new() { Key = "fps", Color = "#FF5C7A" }, new() { Key = "cpu" }, new() { Key = "ram" }, new() { Key = "disk", On = false },
        new() { Key = "gpu", Color = "#3DDC97" }, new() { Key = "vram", On = false, Color = "#3DDC97" },
        new() { Key = "temp", On = false, Color = "#FFB547" },
        new() { Key = "net", On = false }, new() { Key = "ping", Color = "#6EA8FF" },
    };
}

/// <summary>Uma leitura de tudo o que o overlay pode mostrar (-1 = sem dados).</summary>
public record OverlaySnapshot(double Fps, double Cpu, double Ram, double RamUsed, double RamTotal, double Disk,
    double Gpu, double VramGb, double GpuTemp, double DownBps, double UpBps, long PingMs);

/// <summary>
/// Overlay leve por cima do jogo (FiveM em janela/sem bordas): CPU, RAM, disco, GPU,
/// VRAM, temperatura da GPU, rede e ping ao servidor.
///
/// Peso: 1 leitura de contadores por segundo e 1 ping a cada 3 s, numa janela que não
/// recebe foco nem cliques. É a ÚNICA coisa da app que trabalha com o jogo à frente —
/// e só se o jogador a ligar. FPS fica de fora: medi-lo exige ETW/PresentMon (driver
/// de captura), que é exatamente o tipo de coisa que não queremos no PC de quem joga.
/// </summary>
public static class OverlayService
{
    private static string SettingsFile => Path.Combine(ConfigService.DataDir, "overlay.json");

    public static OverlaySettings Settings { get; private set; } = LoadSettings();

    private static OverlaySettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsFile));
                if (s != null)
                {
                    // métricas novas em versões futuras entram no fim, desligadas
                    foreach (var m in new OverlaySettings().Metrics)
                        if (s.Metrics.All(x => x.Key != m.Key)) s.Metrics.Add(new OverlayMetric { Key = m.Key, On = false, Color = m.Color });
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Swallow("ler overlay.json", ex); }
        return new OverlaySettings();
    }

    public static void SaveSettings()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings)); }
        catch (Exception ex) { Log.Swallow("gravar overlay.json", ex); }
        _fpsTried = false; // mudou algo: volta a tentar o FPS (ex.: reabriu como admin)
        _window?.ApplyLayout();
        Render();
    }

    public static string Label(string key) => key switch
    {
        "fps" => "FPS (precisa de admin)", "cpu" => "CPU", "ram" => "RAM", "disk" => "Disco", "gpu" => "GPU", "vram" => "VRAM",
        "temp" => "Temp. GPU", "net" => "Rede", "ping" => "Ping (servidor)", _ => key,
    };

    // ---------- amostragem (1 timer para overlay + pré-visualização) ----------

    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private static int _previewRefs;
    private static OverlaySnapshot? _last;

    static OverlayService() => Timer.Tick += (_, _) => Tick();

    private static void UpdateTimer()
    {
        var run = Settings.Enabled || _previewRefs > 0;
        if (run && !Timer.IsEnabled) { Tick(); Timer.Start(); }
        else if (!run)
        {
            Timer.Stop();
            if (FpsCounter.Running) Task.Run(FpsCounter.Stop);
            _fpsTried = false;
        }
    }

    /// <summary>A aba Overlay está à vista: amostra mesmo com o overlay desligado (pré-visualização).</summary>
    public static void AddPreview() { _previewRefs++; UpdateTimer(); }
    public static void RemovePreview() { _previewRefs = Math.Max(0, _previewRefs - 1); UpdateTimer(); }

    private static long _lastRx, _lastTx;
    private static DateTime _lastNet;
    private static long _ping = -1;
    private static DateTime _lastPing;
    private static PerformanceCounter? _disk;
    private static List<PerformanceCounter>? _vram;
    private static DateTime _vramRefreshed;
    private static double _gpuTemp = -1;
    private static DateTime _lastTemp;

    private static bool _sampling;

    // A leitura corre fora da thread da UI: o 1º contador de desempenho do Windows
    // chega a demorar segundos a abrir e congelava a janela (e o overlay) entretanto.
    private static async void Tick()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var on = Settings.Metrics.Where(m => m.On).Select(m => m.Key).ToHashSet();
            _last = await Task.Run(() => Sample(on));
            Render();
        }
        catch (Exception ex) { Log.Swallow("amostra do overlay", ex); }
        finally { _sampling = false; }
    }

    private static bool _fpsTried;

    private static OverlaySnapshot Sample(HashSet<string> on)
    {
        // FPS: sessão ETW só com o overlay ligado E a métrica FPS ativa — a
        // pré-visualização não a abre (a janela da frente aí é a própria app).
        var wantFps = Settings.Enabled && on.Contains("fps");
        if (wantFps && !FpsCounter.Running && !_fpsTried) { _fpsTried = true; FpsCounter.Start(); }
        else if (!wantFps && FpsCounter.Running) { FpsCounter.Stop(); _fpsTried = false; }

        var (ramPct, ramUsed, ramTotal) = on.Contains("ram") ? SystemMonitor.GetRam() : (-1, 0, 0);

        double down = -1, up = -1;
        if (on.Contains("net"))
        {
            long rx = 0, tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var st = n.GetIPv4Statistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
            var now = DateTime.UtcNow;
            var secs = (now - _lastNet).TotalSeconds;
            if (_lastNet != default && secs > 0) { down = Math.Max(0, (rx - _lastRx) / secs); up = Math.Max(0, (tx - _lastTx) / secs); }
            _lastRx = rx; _lastTx = tx; _lastNet = now;
        }

        if (on.Contains("ping") && DateTime.UtcNow - _lastPing > TimeSpan.FromSeconds(3))
        {
            _lastPing = DateTime.UtcNow;
            try
            {
                using var p = new Ping();
                var r = p.Send(NetworkOptimizer.ServerHost, 800);
                _ping = r.Status == IPStatus.Success ? r.RoundtripTime : -1;
            }
            catch { _ping = -1; }
        }

        // temperatura: nvidia-smi é um processo — no máximo a cada 10 s (regra da pena)
        if (on.Contains("temp") && DateTime.UtcNow - _lastTemp > TimeSpan.FromSeconds(10))
        {
            _lastTemp = DateTime.UtcNow;
            try { _gpuTemp = SystemMonitor.GetTemps().gpu; } catch { _gpuTemp = -1; }
        }

        return new OverlaySnapshot(
            wantFps ? FpsCounter.Sample() : -1,
            on.Contains("cpu") ? SystemMonitor.GetCpuUsage() : -1,
            ramPct, ramUsed, ramTotal,
            on.Contains("disk") ? DiskBusy() : -1,
            on.Contains("gpu") ? SystemMonitor.GetGpuUsage() : -1,
            on.Contains("vram") ? VramGb() : -1,
            on.Contains("temp") ? _gpuTemp : -1,
            down, up, on.Contains("ping") ? _ping : -1);
    }

    private static double DiskBusy()
    {
        try
        {
            _disk ??= new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
            return Math.Clamp(100 - _disk.NextValue(), 0, 100);
        }
        catch { return -1; }
    }

    private static double VramGb()
    {
        try
        {
            if (_vram == null || DateTime.UtcNow - _vramRefreshed > TimeSpan.FromMinutes(2))
            {
                _vram?.ForEach(c => c.Dispose());
                _vram = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames()
                    .Select(n => new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n)).ToList();
                _vramRefreshed = DateTime.UtcNow;
            }
            // uma instância por placa: a dedicada é a que mais usa
            return _vram.Count == 0 ? -1 : _vram.Max(c => { try { return c.NextValue(); } catch { return 0; } }) / 1073741824.0;
        }
        catch { return -1; }
    }

    // ---------- desenho (mesmo código na pré-visualização e no overlay) ----------

    private static string Rate(double bps) => bps < 0 ? "—" :
        bps >= 1_048_576 ? $"{bps / 1_048_576:0.0}MB/s" : $"{bps / 1024:0}KB/s";

    private static string Text(string key, OverlaySnapshot s) => key switch
    {
        "fps" => FpsCounter.Error == "admin" ? "FPS (admin)" : s.Fps < 0 ? "FPS —" : $"{s.Fps:0} FPS",
        "cpu" => s.Cpu < 0 ? "CPU —" : $"CPU {s.Cpu:0}%",
        "ram" => s.Ram < 0 ? "RAM —" : $"RAM {s.Ram:0}% {s.RamUsed:0.0}/{s.RamTotal:0.0}GB",
        "disk" => s.Disk < 0 ? "DSK —" : $"DSK {s.Disk:0}%",
        "gpu" => s.Gpu < 0 ? "GPU —" : $"GPU {s.Gpu:0}%",
        "vram" => s.VramGb < 0 ? "VRAM —" : $"VRAM {s.VramGb:0.0}GB",
        "temp" => s.GpuTemp <= 0 ? "GPU —°C" : $"GPU {s.GpuTemp:0}°C",
        "net" => $"NET ↓{Rate(s.DownBps)} ↑{Rate(s.UpBps)}",
        "ping" => s.PingMs < 0 ? "PING —" : $"PING {s.PingMs}ms",
        _ => "",
    };

    private static readonly List<TextBlock> Targets = new();

    /// <summary>Liga um TextBlock ao overlay — passa a ser redesenhado a cada amostra.</summary>
    public static void Attach(TextBlock tb) { if (!Targets.Contains(tb)) Targets.Add(tb); RenderInto(tb); }
    public static void Detach(TextBlock tb) => Targets.Remove(tb);

    private static void Render() { foreach (var t in Targets) RenderInto(t); }

    private static void RenderInto(TextBlock tb)
    {
        var s = _last ?? new OverlaySnapshot(-1, -1, -1, 0, 0, -1, -1, -1, -1, -1, -1, -1);
        tb.Inlines.Clear();
        tb.FontSize = Settings.FontSize;
        var first = true;
        foreach (var m in Settings.Metrics.Where(m => m.On))
        {
            if (!first) tb.Inlines.Add(new Run("   "));
            first = false;
            Brush br;
            try { br = new SolidColorBrush((Color)ColorConverter.ConvertFromString(m.Color)); }
            catch { br = Brushes.White; }
            br.Freeze();
            tb.Inlines.Add(new Run(Text(m.Key, s)) { Foreground = br });
        }
        if (first) tb.Inlines.Add(new Run("(nenhuma métrica ligada)") { Foreground = Brushes.Gray });
    }

    // ---------- janela por cima do jogo ----------

    private static OverlayWindow? _window;

    public static void SetEnabled(bool on)
    {
        Settings.Enabled = on;
        SaveSettings();
        Apply();
    }

    /// <summary>Abre/fecha a janela conforme a definição gravada (chamado no login e ao mudar).</summary>
    public static void Apply()
    {
        if (Settings.Enabled && _window == null) { _window = new OverlayWindow(); _window.Show(); }
        else if (!Settings.Enabled && _window != null) { _window.Close(); _window = null; }
        UpdateTimer();
    }

    public static void Shutdown()
    {
        Timer.Stop();
        FpsCounter.Stop();
        _window?.Close();
        _window = null;
    }

    private sealed class OverlayWindow : Window
    {
        private readonly TextBlock _text = new()
        {
            FontFamily = new FontFamily("Bahnschrift SemiBold, Segoe UI Semibold, Segoe UI"),
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.9, Color = Colors.Black },
        };
        private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "Adams Overlay";
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x08, 0x07, 0x0C)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 5, 10, 5),
                Child = _text,
            };
            SourceInitialized += (_, _) => MakeClickThrough();
            SizeChanged += (_, _) => ApplyLayout();
            // jogos sem bordas também pedem "topmost": reafirmar sem roubar foco
            _topmost.Tick += (_, _) => KeepOnTop();
            Loaded += (_, _) => { Attach(_text); _topmost.Start(); };
            Closed += (_, _) => { Detach(_text); _topmost.Stop(); };
        }

        public void ApplyLayout()
        {
            var sw = SystemParameters.PrimaryScreenWidth;
            var sh = SystemParameters.PrimaryScreenHeight;
            const double m = 8;
            Left = Settings.Position is "TR" or "BR" ? sw - ActualWidth - m : m;
            Top = Settings.Position is "BL" or "BR" ? sh - ActualHeight - m : m;
        }

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        private void MakeClickThrough()
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
        }

        private void KeepOnTop()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }
}
