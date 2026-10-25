using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

/// <summary>
/// Início ao estilo NX (na nossa cor): 6 tiras, especificações, 4 gráficos ao vivo,
/// saúde do PC e ações rápidas — só ações seguras para PC check.
///
/// Peso: igual ao painel antigo — 1 amostra/s, ping a cada 5 s, temperaturas a cada
/// 10 s, e tudo parado com a app escondida ou o jogo à frente (AppState).
/// </summary>
public partial class DashboardView : UserControl
{
    public record HealthRow(string Label, string Value, double Width);

    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(5) };

    private readonly Queue<double> _cpuH = new(), _ramH = new(), _netH = new(), _pingH = new();
    private long _lastRx;
    private DateTime _lastNet;
    private double _ramPct = -1, _diskFreePct = -1, _gpuTemp = -1;
    private bool _sampling, _pinging;
    private int _slowTicks;

    public DashboardView()
    {
        InitializeComponent();
        _monitor.Tick += async (_, _) => await SampleAsync();
        _slow.Tick += async (_, _) => await SlowAsync();
        Loaded += (_, _) => { LoadStatic(); RefreshPills(); Start(); AppState.Changed += OnVisibility; };
        Unloaded += (_, _) => { AppState.Changed -= OnVisibility; Stop(); };
        SizeChanged += (_, _) => DrawAll();
    }

    private void OnVisibility(bool visible) { if (visible) Start(); else Stop(); }

    private void Start()
    {
        if (!AppState.IsVisible || _monitor.IsEnabled) return;
        _monitor.Start();
        _slow.Start();
        _ = SampleAsync();
        _ = SlowAsync();
    }

    private void Stop() { _monitor.Stop(); _slow.Stop(); }

    // ---------- estático (uma vez) ----------

    private void LoadStatic()
    {
        SpecOs.Text = SystemMonitor.GetOsName();
        SpecGpu.Text = GpuSub.Text = SystemMonitor.GetGpuName();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            SpecCpu.Text = CpuSub.Text = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "—";
        }
        catch { SpecCpu.Text = "—"; }

        var cfg = ConfigService.Current;
        NewsList.ItemsSource = cfg.News;
        CommunityText.Text = $"Comunidade · {cfg.CommunityStatus}";
        NewsCard.Visibility = cfg.News.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPills()
    {
        var all = TweakLibrary.All;
        bool On(LibTweak t) { try { return t.IsActive(); } catch { return false; } }
        var active = all.Count(On);
        var pending = all.Count(t => t.Recommended && !On(t));
        ActivePill.Text = $"{active} ajustes ativos";
        PendingPill.Text = pending == 0 ? "Recomendados em dia" : $"{pending} recomendados por ativar";
        var admin = UninstallerEngine.IsAdmin();
        AdminPill.Text = admin ? "● Administrador" : "○ Sem admin";
        AdminPill.Foreground = (Brush)FindResource(admin ? "BrSuccess" : "BrWarn");
        AdminPillBox.Background = (Brush)FindResource("BrSurface");
        AdminPillBox.ToolTip = admin ? null : "Clica para reabrir como administrador (precisa para FPS e ajustes de sistema)";
        _recActivePct = all.Count(t => t.Recommended) is var rec and > 0 ? (rec - pending) * 100.0 / rec : 100;
    }
    private double _recActivePct = -1;

    private void AdminPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!UninstallerEngine.IsAdmin() && UninstallerEngine.RestartAsAdmin()) Application.Current.Shutdown();
    }

    // ---------- amostras ----------

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var (cpu, gpu, ram, disk, rx) = await Task.Run(() =>
            {
                long bytes = 0;
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        bytes += n.GetIPv4Statistics().BytesReceived;
                return (SystemMonitor.GetCpuUsage(), SystemMonitor.GetGpuUsage(), SystemMonitor.GetRam(), SystemMonitor.GetSystemDisk(), bytes);
            });

            CpuText.Text = cpu < 0 ? "—" : $"{cpu:0}%";
            GpuText.Text = gpu < 0 ? "—" : $"{gpu:0}%";
            _ramPct = ram.pct;
            RamText.Text = ram.pct < 0 ? "—" : $"{ram.pct:0}%";
            RamSub.Text = ram.pct < 0 ? "" : $"{ram.usedGb:0.0} / {ram.totalGb:0.0} GB";
            SpecRam.Text = ram.totalGb > 0 ? $"{ram.totalGb:0.0} GB" : "—";
            if (disk.pct >= 0)
            {
                DiskText.Text = $"{disk.pct:0}%";
                DiskSub.Text = $"{disk.freeGb:0} GB livres · C:";
                SpecDisk.Text = $"C: · {disk.freeGb:0} GB livres de {disk.totalGb:0} GB";
                _diskFreePct = 100 - disk.pct;
            }

            var now = DateTime.UtcNow;
            double down = 0;
            if (_lastNet != default) down = Math.Max(0, (rx - _lastRx) / Math.Max(0.2, (now - _lastNet).TotalSeconds));
            _lastRx = rx; _lastNet = now;
            NetText.Text = Rate(down);
            NetSub.Text = "download agora";

            Push(_cpuH, Math.Max(0, cpu));
            Push(_ramH, Math.Max(0, ram.pct));
            Push(_netH, down);
            CpuChartVal.Text = CpuText.Text;
            RamChartVal.Text = RamText.Text;
            NetChartVal.Text = Rate(down);
            DrawAll();
        }
        finally { _sampling = false; }
    }

    private async Task SlowAsync()
    {
        SpecUptime.Text = SystemMonitor.GetUptime();
        if (!_pinging)
        {
            _pinging = true;
            try
            {
                var r = await NetTools.PingAsync(NetworkOptimizer.ServerHost, 3);
                PingText.Text = r.Ok ? $"{r.Avg:0} ms" : "—";
                PingSub.Text = !r.Ok ? "sem resposta" : r.Avg < 40 ? "Excelente" : r.Avg < 90 ? "Boa" : "Fraca";
                if (r.Ok) { Push(_pingH, r.Avg); PingChartVal.Text = $"{r.Avg:0} ms"; }
            }
            finally { _pinging = false; }
        }
        // temperatura: processo/sensor — de 10 em 10 s chega
        if (_slowTicks++ % 2 == 0)
        {
            var (cpuT, gpuT) = await Task.Run(SystemMonitor.GetTemps);
            _gpuTemp = gpuT;
            if (gpuT > 0) GpuSub.Text = $"{SystemMonitor.GetGpuName()} · {gpuT:0}°C";
            if (cpuT > 0) CpuSub.Text = $"{SpecCpu.Text} · {cpuT:0}°C";
        }
        UpdateHealth();
        DrawAll();
    }

    // ---------- saúde do PC ----------

    private void UpdateHealth()
    {
        var rows = new List<(string label, double pct)>();
        if (_ramPct >= 0) rows.Add(("Memória livre", 100 - _ramPct));
        if (_diskFreePct >= 0) rows.Add(("Disco livre", Math.Min(100, _diskFreePct * 2.5))); // 40 % livre já é 100
        if (_recActivePct >= 0) rows.Add(("Ajustes", _recActivePct));
        if (_gpuTemp > 0) rows.Add(("Temp. GPU", Math.Clamp((90 - _gpuTemp) * 100 / 40, 0, 100))); // ≤50 °C = 100, 90 °C = 0
        if (rows.Count == 0) return;

        var score = (int)Math.Round(rows.Average(r => r.pct));
        HealthText.Text = score.ToString();
        HealthArc.Stroke = (Brush)FindResource(score >= 70 ? "BrAccent" : score >= 45 ? "BrWarn" : "BrDanger");
        HealthArc.Data = Arc(43, 43, 39.5, score / 100.0);
        HealthBars.ItemsSource = rows.Select(r => new HealthRow(r.label, $"{r.pct:0}", Math.Max(2, r.pct * 0.9))).ToList();
    }

    private static Geometry? Arc(double cx, double cy, double r, double frac)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0) return null;
        var a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, false, false);
            c.ArcTo(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- ações rápidas ----------

    private static void Nav(string key) => (Application.Current.MainWindow as MainWindow)?.NavigateTo(key);
    private void Optimize_Click(object sender, RoutedEventArgs e) => Nav("ajustes");
    private void GameMode_Click(object sender, RoutedEventArgs e) => Nav("gamemode");
    private void Diag_Click(object sender, RoutedEventArgs e) => Nav("diag");
    private void PcCheck_Click(object sender, RoutedEventArgs e) => Nav("fivem");

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        RestoreBtn.IsEnabled = false;
        ActionStatus.Text = "A criar ponto de restauro… (aceita o pedido de administrador)";
        var (ok, msg) = await RestorePointService.CreateAsync("Adams Toolkit - ponto manual");
        ActionStatus.Text = ok ? "" : msg;
        if (ok) MainWindow.Toast("Ponto de restauro criado.");
        RestoreBtn.IsEnabled = true;
    }

    // ---------- desenho ----------

    private static string Rate(double bps) =>
        bps >= 1_048_576 ? $"{bps / 1_048_576:0.0} MB/s" : $"{bps / 1024:0} KB/s";

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > 60) q.Dequeue();
    }

    private void DrawAll()
    {
        DrawSeries(CpuLine, CpuFill, CpuHost, _cpuH, 100);
        DrawSeries(RamLine, RamFill, RamHost, _ramH, 100);
        DrawSeries(NetLine, NetFill, NetHost, _netH, Math.Max(64 * 1024, _netH.DefaultIfEmpty(0).Max() * 1.25));
        DrawSeries(PingLine, PingFill, PingHost, _pingH, Math.Max(60, _pingH.DefaultIfEmpty(0).Max() * 1.4));
    }

    /// <summary>Curva suave (Catmull-Rom → Bézier) — redesenhada 1x por amostra, nunca por frame.</summary>
    private static void DrawSeries(Path stroke, Path fill, FrameworkElement host, Queue<double> values, double max)
    {
        var w = host.ActualWidth;
        var h = host.ActualHeight;
        if (w < 8 || h < 6 || values.Count < 2) { stroke.Data = fill.Data = null; return; }

        var s = values.ToArray();
        var n = s.Length;
        var step = w / 59.0; // 60 amostras ocupam a largura toda; poucas ficam à direita
        var x0 = w - (n - 1) * step;
        var pts = new Point[n];
        for (var i = 0; i < n; i++)
            pts[i] = new Point(x0 + i * step, h - Math.Clamp(s[i], 0, max) / max * (h - 4) - 2);

        var sg = new StreamGeometry();
        var fg = new StreamGeometry();
        using (var sc = sg.Open())
        using (var fc = fg.Open())
        {
            sc.BeginFigure(pts[0], false, false);
            fc.BeginFigure(new Point(pts[0].X, h), true, true);
            fc.LineTo(pts[0], false, false);
            for (var i = 0; i < n - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, n - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                sc.BezierTo(c1, c2, p2, true, false);
                fc.BezierTo(c1, c2, p2, false, false);
            }
            fc.LineTo(new Point(pts[n - 1].X, h), false, false);
        }
        sg.Freeze();
        fg.Freeze();
        stroke.Data = sg;
        fill.Data = fg;
    }
}
