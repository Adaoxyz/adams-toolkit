using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class RedeView : UserControl
{
    private record ResultRow(string Name, string Server, string Result, Brush Brush);
    private record AdapterRow(string Title, string Detail, string Warning);

    private CancellationTokenSource? _speedCts;

    public RedeView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAdapters();
        // sair da aba a meio do teste não deixa downloads a correr em fundo
        Unloaded += (_, _) => _speedCts?.Cancel();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private Brush PingBrush(double ms) => Res(ms <= 40 ? "BrSuccess" : ms <= 90 ? "BrWarn" : "BrDanger");

    // ---------- ligação ----------

    private List<AdapterInfo> _adapters = new();

    private void LoadAdapters()
    {
        _adapters = NetTools.Adapters();
        AdapterList.ItemsSource = _adapters.Count == 0
            ? new[] { new AdapterRow("Sem ligação ativa", "Não encontrei nenhuma placa de rede ligada à internet.", "") }
            : _adapters.Select(a => new AdapterRow(
                $"{(a.IsWifi ? "📶 Wi-Fi" : "🔌 Cabo")} — {a.Name}",
                $"{a.Description} · {(a.SpeedMbps > 0 ? $"{a.SpeedMbps} Mbps de ligação" : "velocidade desconhecida")} · IP {a.Ipv4} · router {a.Gateway} · DNS {(a.Dns.Count > 0 ? string.Join(", ", a.Dns) : "—")}",
                a.IsWifi ? "Wi-Fi tem sempre mais jitter e picos do que cabo. Para jogar, um cabo de rede é a melhoria de ping n.º 1."
                         : a.SpeedMbps is > 0 and < 1000 ? $"Ligação a {a.SpeedMbps} Mbps — se a tua internet for mais rápida que isto, o cabo ou a porta estão a limitar." : ""))
              .ToList();

        var dns = _adapters.SelectMany(a => a.Dns).Distinct().ToList();
        DnsCurrent.Text = dns.Count == 0 ? "DNS atual: —" : $"DNS atual: {string.Join(", ", dns)}";
    }

    // ---------- velocidade ----------

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_speedCts != null) { _speedCts.Cancel(); return; }
        _speedCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SpeedBtn.Content = "■  Cancelar";
        DownText.Text = UpText.Text = PingText.Text = JitterText.Text = "—";
        SpeedBar.Visibility = Visibility.Visible;
        SpeedBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        SpeedBar.Value = 0;
        _barTarget = 0;

        var progress = new Progress<(string phase, double value, double frac)>(p =>
        {
            switch (p.phase)
            {
                case "ping": PingText.Text = $"{p.value:0}"; SpeedStatus.Text = "A medir latência…"; break;
                case "download": DownText.Text = $"{p.value:0.0}"; SpeedStatus.Text = "A medir download…"; break;
                case "upload": UpText.Text = $"{p.value:0.0}"; SpeedStatus.Text = "A medir upload…"; break;
            }
            // barra: ping 10 % · download 50 % · upload 40 %
            BarTo(p.phase switch { "ping" => 0.1 * p.frac, "download" => 0.1 + 0.5 * p.frac, _ => 0.6 + 0.4 * p.frac });
        });

        try
        {
            var r = await NetTools.SpeedTestAsync(progress, _speedCts.Token);
            DownText.Text = $"{r.DownMbps:0.0}";
            UpText.Text = $"{r.UpMbps:0.0}";
            PingText.Text = $"{r.PingMs:0}";
            JitterText.Text = $"{r.JitterMs:0.#}";
            BarTo(1);
            SpeedStatus.Text = $"Concluído às {DateTime.Now:HH:mm}. Latência medida até à Cloudflare, não até ao servidor de jogo — esse está no Teste de ping.";
        }
        catch (OperationCanceledException) { SpeedStatus.Text = "Teste cancelado."; }
        catch (Exception ex)
        {
            SpeedStatus.Text = "Não foi possível fazer o teste — sem internet ou a Cloudflare está bloqueada nesta rede.";
            Log.Warn("speedtest: " + ex.Message);
        }
        finally
        {
            _speedCts?.Dispose();
            _speedCts = null;
            SpeedBtn.Content = "▶  Iniciar teste";
        }
    }

    // Os valores chegam aos saltos (4x por segundo); cada um desliza a partir do anterior
    // durante ~0,45 s — a barra parece contínua. Nunca anda para trás.
    private double _barTarget;
    private void BarTo(double target)
    {
        target = Math.Clamp(target, 0, 1);
        if (target < _barTarget && target > 0) return;
        _barTarget = target;
        var a = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        SpeedBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, a, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    // ---------- ping ----------

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        PingBtn.IsEnabled = false;
        var targets = new (string name, string host)[]
        {
            ("Servidor ATL PVP", NetworkOptimizer.ServerHost), ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"),
        };
        if (_adapters.FirstOrDefault()?.Gateway is { } gw and not "—") targets = targets.Append(("O teu router", gw)).ToArray();

        PingList.ItemsSource = targets.Select(t => new ResultRow(t.name, t.host, "a testar…", Res("BrMuted"))).ToList();
        var results = await Task.WhenAll(targets.Select(t => NetTools.PingAsync(t.host)));
        PingList.ItemsSource = targets.Zip(results, (t, r) =>
            new ResultRow(t.name, t.host, r.Summary, r.Ok ? PingBrush(r.Avg) : Res("BrDanger"))).ToList();
        PingBtn.IsEnabled = true;
    }

    // ---------- DNS ----------

    private async void Dns_Click(object sender, RoutedEventArgs e)
    {
        DnsBtn.IsEnabled = false;
        var current = _adapters.SelectMany(a => a.Dns).Distinct().ToList();
        var servers = NetTools.DnsServers.ToList();
        foreach (var ip in current)
            if (servers.All(s => s.ip != ip)) servers.Insert(0, ("O teu DNS atual", ip));

        DnsList.ItemsSource = servers.Select(s => new ResultRow(s.name, s.ip, "a medir…", Res("BrMuted"))).ToList();
        var res = await NetTools.BenchDnsAsync(servers);
        var best = res.FirstOrDefault(r => r.Ok > 0);
        DnsList.ItemsSource = res.Select(r => new ResultRow(
            current.Contains(r.Server) && !r.Name.StartsWith("O teu") ? $"{r.Name} (atual)" : r.Name,
            r.Server,
            r.Summary + (r == best ? "  ★ mais rápido" : ""),
            r.Ok == 0 ? Res("BrDanger") : r == best ? Res("BrSuccess") : Res("BrText"))).ToList();
        DnsBtn.IsEnabled = true;
    }

    private void NetSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Swallow("abrir ncpa.cpl", ex); }
    }
}
