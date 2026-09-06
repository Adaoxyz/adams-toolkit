using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class DashboardView : UserControl
{
    private DispatcherTimer? _monitorTimer;
    private DispatcherTimer? _pingTimer;
    private DispatcherTimer? _tempTimer;

    // 60 amostras por série (1/s no monitor, 1/3s nas temperaturas)
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private readonly Queue<double> _ramHistory = new();
    private readonly Queue<double> _diskHistory = new();
    private readonly Queue<double> _cpuTempHistory = new();
    private readonly Queue<double> _gpuTempHistory = new();

    private bool _pinging;
    private bool _readingTemp;

    public DashboardView()
    {
        InitializeComponent();
        // Loaded (não construtor): re-lê a preferência sempre que a view volta a
        // aparecer — pode ter mudado no menu da bandeja entretanto.
        Loaded += (_, _) => { RefreshStartupCheck(); Refresh(); StartMonitor(); AppState.Changed += OnVisibilityChanged; };
        Unloaded += (_, _) => { AppState.Changed -= OnVisibilityChanged; StopMonitor(); };
        SizeChanged += (_, _) => RedrawSparks(); // largura dos tiles mudou → refazer curvas
    }

    // ---- iniciar com o Windows ----

    private bool _startupSyncing;

    private void RefreshStartupCheck()
    {
        _startupSyncing = true;
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        _startupSyncing = false;
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_startupSyncing) return;
        StartupManager.SetEnabled(StartupCheck.IsChecked == true);
    }

    // ---- ações rápidas (navegam para a secção respetiva) ----

    private static void Nav(string key) =>
        (Application.Current.MainWindow as MainWindow)?.NavigateTo(key);

    private void QuickOptimize_Click(object sender, RoutedEventArgs e) => Nav("optimizer");
    private void QuickClean_Click(object sender, RoutedEventArgs e) => Nav("optimizer");
    private void QuickFiveM_Click(object sender, RoutedEventArgs e) => Nav("fivem");

    // ---- monitor de desempenho ----

    private void StartMonitor()
    {
        GpuNameText.Text = SystemMonitor.GetGpuName();
        OsText.Text = SystemMonitor.GetOsName();
        OsDetailText.Text = OsText.Text;
        ArchText.Text = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

        StopMonitor();
        // Janela por trás do jogo (ou na bandeja) = ninguém vê os gráficos: não se
        // arranca nada. O AppState avisa quando voltar à frente.
        if (!AppState.IsVisible) return;

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += async (_, _) => await SampleAsync();
        _monitorTimer.Start();
        _ = SampleAsync();

        // ping: 10s chega para um número que quase não muda
        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _pingTimer.Tick += async (_, _) => await UpdatePingAsync();
        _pingTimer.Start();
        _ = UpdatePingAsync();

        // temperaturas: sensores (LHM/WMI/nvidia-smi) são a leitura mais cara — 5s
        _tempTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tempTimer.Tick += async (_, _) => await UpdateTempsAsync();
        _tempTimer.Start();
        _ = UpdateTempsAsync();
    }

    private void StopMonitor()
    {
        _monitorTimer?.Stop(); _monitorTimer = null;
        _pingTimer?.Stop(); _pingTimer = null;
        _tempTimer?.Stop(); _tempTimer = null;
    }

    /// <summary>App voltou à frente → religa o monitor; saiu de vista → desliga tudo.</summary>
    private void OnVisibilityChanged(bool visible)
    {
        // Nota: os contadores ficam abertos no alt-tab (não custam nada parados e
        // reabri-los perderia a 1ª amostra). Só se largam ao ir para a bandeja.
        if (visible) StartMonitor();
        else StopMonitor();
    }

    private async Task UpdateTempsAsync()
    {
        if (_readingTemp) return;
        _readingTemp = true;
        try
        {
            var (cpuT, gpuT) = await Task.Run(SystemMonitor.GetTemps);
            GpuTempText.Text = gpuT < 0 ? "N/D" : $"{gpuT:0}°C";
            if (gpuT >= 0) Push(_gpuTempHistory, gpuT);

            // A temperatura do CPU precisa do driver de kernel (admin) e o Windows 11
            // com Integridade da Memória bloqueia-o — em muitos PCs é impossível de ler.
            // Em vez de mostrar "N/D" (parece avariado), escondemos o campo TEMP CPU
            // quando não há leitura; a GPU ocupa a largura toda. Quem consegue ler
            // (admin + sem bloqueio) continua a vê-lo normalmente.
            if (cpuT < 0)
            {
                CpuTempTile.Visibility = Visibility.Collapsed;
                TempGrid.Columns = 1;
            }
            else
            {
                CpuTempText.Text = $"{cpuT:0}°C";
                CpuTempTile.Visibility = Visibility.Visible;
                TempGrid.Columns = 2;
                Push(_cpuTempHistory, cpuT);
            }
            TempAdminHint.Visibility = Visibility.Collapsed;

            DrawSeries(CpuTempSpark, null, CpuTempSparkHost, _cpuTempHistory, TempScale(_cpuTempHistory));
            DrawSeries(GpuTempSpark, null, GpuTempSparkHost, _gpuTempHistory, TempScale(_gpuTempHistory));
        }
        finally { _readingTemp = false; }
    }

    private void TempAdmin_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas", // pede UAC
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch { /* UAC recusado — fica em N/D */ }
    }

    private async Task SampleAsync()
    {
        var (cpu, gpu, ram, disk) = await Task.Run(() =>
            (SystemMonitor.GetCpuUsage(), SystemMonitor.GetGpuUsage(),
             SystemMonitor.GetRam(), SystemMonitor.GetSystemDisk()));

        CpuText.Text = cpu < 0 ? "—" : $"{cpu:0.0}%";
        GpuText.Text = gpu < 0 ? "—" : $"{gpu:0}%";
        if (ram.pct < 0) { RamText.Text = "—"; }
        else
        {
            RamText.Text = $"{ram.pct:0}%";
            RamDetailText.Text = $"{ram.usedGb:0.0} / {ram.totalGb:0.0} GB";
        }
        if (disk.pct < 0) { DiskText.Text = "—"; }
        else
        {
            DiskText.Text = $"{disk.pct:0}%";
            DiskDetailText.Text = $"{disk.freeGb:0} GB livres";
        }
        UptimeText.Text = SystemMonitor.GetUptime();

        Push(_cpuHistory, Math.Max(0, cpu));
        Push(_gpuHistory, Math.Max(0, gpu));
        Push(_ramHistory, Math.Max(0, ram.pct));
        Push(_diskHistory, Math.Max(0, disk.pct));

        SetGauge(CpuArc, cpu);
        SetGauge(GpuArc, gpu);
        SetGauge(RamArc, ram.pct);
        SetGauge(DiskArc, disk.pct);
        RedrawSparks();
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > 60) q.Dequeue();
    }

    // temperaturas não são 0–100%: escala com folga por cima do pico
    private static double TempScale(Queue<double> q) =>
        q.Count == 0 ? 100 : Math.Max(90, Math.Ceiling(q.Max() / 10) * 10 + 5);

    private void RedrawSparks()
    {
        DrawSeries(GpuSpark, null, GpuSparkHost, _gpuHistory, 100);
        DrawSeries(RamSpark, null, RamSparkHost, _ramHistory, 100);
        DrawSeries(CpuSpark, null, CpuSparkHost, _cpuHistory, 100);
        DrawSeries(DiskSpark, null, DiskSparkHost, _diskHistory, 100);
        DrawSeries(ChartStroke, ChartFill, ChartHost, _cpuHistory, 100);
    }

    // ---- desenho ----

    /// <summary>
    /// Curva suave (Catmull-Rom → Bézier) dentro do host. Redesenhada a cada
    /// amostra (1/s) — nada de render por frame: na bandeja/idle custa zero.
    /// </summary>
    private static void DrawSeries(Path stroke, Path? fill, FrameworkElement host,
                                   Queue<double> values, double max)
    {
        var w = host.ActualWidth;
        var h = host.ActualHeight;
        if (w < 8 || h < 6 || values.Count < 2)
        {
            stroke.Data = null;
            if (fill != null) fill.Data = null;
            return;
        }

        var samples = values.ToArray();
        var n = samples.Length;
        var step = w / Math.Max(1, n - 1);
        var pts = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var y = h - (Math.Clamp(samples[i], 0, max) / max * (h - 5)) - 2.5;
            pts[i] = new Point(i * step, y);
        }

        var sg = new StreamGeometry();
        var fg = new StreamGeometry();
        using (var sc = sg.Open())
        {
            var fc = fill != null ? fg.Open() : null;
            try
            {
                sc.BeginFigure(pts[0], false, false);
                fc?.BeginFigure(new Point(pts[0].X, h), true, true);
                fc?.LineTo(pts[0], false, false);
                for (var i = 0; i < n - 1; i++)
                {
                    var p0 = pts[Math.Max(i - 1, 0)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(i + 2, n - 1)];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                    sc.BezierTo(c1, c2, p2, true, false);
                    fc?.BezierTo(c1, c2, p2, false, false);
                }
                fc?.LineTo(new Point(pts[n - 1].X, h), false, false);
            }
            finally { fc?.Close(); }
        }
        sg.Freeze();
        stroke.Data = sg;
        if (fill != null) { fg.Freeze(); fill.Data = fg; }
    }

    /// <summary>Anel de progresso: arco a começar às 12h, no sentido dos ponteiros.</summary>
    private static void SetGauge(Path arc, double pct, double size = 34, double thickness = 3.4)
    {
        if (pct <= 0.5) { arc.Data = null; return; }

        var r = (size - thickness) / 2.0;
        var c = new Point(size / 2.0, size / 2.0);
        var p = Math.Clamp(pct, 0, 100);

        if (p >= 99.5)
        {
            var full = new EllipseGeometry(c, r, r);
            full.Freeze();
            arc.Data = full;
            return;
        }

        var a = p / 100.0 * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));

        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
            p > 50, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        arc.Data = geo;
    }

    private async Task UpdatePingAsync()
    {
        if (_pinging) return;
        _pinging = true;
        try
        {
            var ms = await SystemMonitor.PingAsync();
            PingText.Text = ms < 0 ? "—" : ms.ToString();
            PingLabel.Text = ms switch
            {
                < 0 => "Sem ligação",
                < 40 => "● Ligação excelente",
                < 90 => "● Ligação boa",
                _ => "● Ligação fraca",
            };

            // cor do badge segue o estado (antes ficava sempre verde, mesmo em "fraca")
            var (fg, bg) = ms switch
            {
                < 0 => (Color.FromRgb(0xF8, 0x71, 0x71), Color.FromArgb(0x1A, 0xF8, 0x71, 0x71)),
                < 40 => (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromArgb(0x1A, 0x34, 0xD3, 0x99)),
                < 90 => (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromArgb(0x1A, 0xFB, 0xBF, 0x24)),
                _ => (Color.FromRgb(0xF8, 0x71, 0x71), Color.FromArgb(0x1A, 0xF8, 0x71, 0x71)),
            };
            PingLabel.Foreground = new SolidColorBrush(fg);
            PingBadge.Background = new SolidColorBrush(bg);

            // anel cheio = ligação perfeita; vazio a partir de 200 ms
            var quality = ms < 0 ? 0 : (1 - Math.Min(ms, 200) / 200.0) * 100;
            SetGauge(PingArc, quality, 84, 3);
        }
        finally { _pinging = false; }
    }

    private void Refresh()
    {
        var cfg = ConfigService.Current;
        var user = MainWindow.CurrentUser;

        GreetingText.Text = user == null ? "Bem-vindo" : $"Bem-vindo, {user.DisplayName}";
        CommunityText.Text = cfg.CommunityStatus;
        NewsList.ItemsSource = cfg.News;

        Task.Run(() =>
        {
            InstallDetector.InvalidateCache();
            var installed = cfg.Apps.Count(InstallDetector.IsInstalled);
            Dispatcher.Invoke(() => InstalledText.Text = $"{installed} / {cfg.Apps.Count}");
        });
    }
}
