using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

/// <summary>
/// Fundo em rede de partículas (estilo do NX, em roxo): pontinhos nítidos — uns na cor
/// de acento, outros cinzentos — a derivar devagar; quando dois ficam perto, uma linha
/// ténue liga-os e esbate com a distância.
///
/// Peso: 1 só elemento desenhado à mão (OnRender), no máximo 30 fps, pincéis e canetas
/// pré-criados (nada é alocado por frame) e ~40 pontos (≈800 distâncias por frame —
/// trivial). Com a janela sem foco, minimizada ou na bandeja PARA: fica o último frame
/// desenhado, sem custo nenhum. O jogador pode desligar nas Definições (anim.txt).
/// </summary>
public class ParticleField : FrameworkElement
{
    public int Count { get; set; } = 40;

    private static string PrefPath => System.IO.Path.Combine(ConfigService.DataDir, "anim.txt");

    /// <summary>Preferência do jogador (ligado por defeito).</summary>
    public static bool Enabled
    {
        get { try { return System.IO.File.ReadAllText(PrefPath).Trim() != "off"; } catch { return true; } }
        set
        {
            try { System.IO.File.WriteAllText(PrefPath, value ? "on" : "off"); } catch { }
            EnabledChanged?.Invoke();
        }
    }
    private static event Action? EnabledChanged;

    private const double LinkDist = 135;      // px: abaixo disto aparece a linha
    private const int Levels = 12;            // degraus de opacidade das linhas (canetas pré-criadas)
    private const double FrameMs = 1000 / 30.0;

    private struct Dot { public double X, Y, Vx, Vy, R, Alpha; public bool Accent; }

    private Dot[] _dots = Array.Empty<Dot>();
    private Brush[] _accentFill = Array.Empty<Brush>(), _greyFill = Array.Empty<Brush>();
    private Pen[] _linkPens = Array.Empty<Pen>();
    private readonly Stopwatch _clock = new();
    private double _lastMs;
    private bool _running;
    private Window? _window;
    private static readonly Random Rng = new();

    public ParticleField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window != null) { _window.Activated += OnActivated; _window.Deactivated += OnDeactivated; }
            EnabledChanged += OnPref;
            ThemeManager.Changed += OnTheme;
            BuildPalette();
            if (Visible) Start();
        };
        Unloaded += (_, _) =>
        {
            if (_window != null) { _window.Activated -= OnActivated; _window.Deactivated -= OnDeactivated; }
            EnabledChanged -= OnPref;
            ThemeManager.Changed -= OnTheme;
            Stop();
        };
        SizeChanged += (_, _) => { Seed(); InvalidateVisual(); if (!_running && IsLoaded && Visible) Start(); };
    }

    private bool Visible => _window?.IsActive ?? true;
    private void OnActivated(object? s, EventArgs e) => Start();
    private void OnDeactivated(object? s, EventArgs e) => Stop();
    private void OnPref() { InvalidateVisual(); if (Enabled && Visible) Start(); else Stop(); }
    private void OnTheme() { BuildPalette(); InvalidateVisual(); }

    // ---------- cores (do tema atual) ----------

    private void BuildPalette()
    {
        var accent = (TryFindResource("BrAccent") as SolidColorBrush)?.Color ?? Color.FromRgb(0xA6, 0x6B, 0xFF);
        var grey = ThemeManager.IsLight ? Color.FromRgb(0x6E, 0x66, 0x80) : Color.FromRgb(0x9A, 0x94, 0xA8);
        Brush Fill(Color c, double a)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
        // 8 degraus de opacidade para os pontos (cada ponto escolhe o seu)
        _accentFill = Enumerable.Range(0, 8).Select(i => Fill(accent, 0.35 + i * 0.09)).ToArray();
        _greyFill = Enumerable.Range(0, 8).Select(i => Fill(grey, 0.25 + i * 0.07)).ToArray();
        var maxLine = ThemeManager.IsLight ? 0.45 : 0.32;
        _linkPens = Enumerable.Range(0, Levels).Select(i =>
        {
            var p = new Pen(Fill(accent, maxLine * (i + 1) / Levels), 0.8);
            p.Freeze();
            return p;
        }).ToArray();
    }

    // ---------- pontos ----------

    private void Seed()
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 1 || h < 1) return;
        // densidade constante: janela maior = mais pontos (Count é para ~1240x760)
        var n = (int)Math.Clamp(Count * (w * h) / (1240.0 * 760.0), 8, Count * 2);
        if (_dots.Length == n) return;
        _dots = new Dot[n];
        for (var i = 0; i < n; i++)
        {
            var ang = Rng.NextDouble() * Math.PI * 2;
            var speed = 4 + Rng.NextDouble() * 9; // px/s — devagar
            _dots[i] = new Dot
            {
                X = Rng.NextDouble() * w, Y = Rng.NextDouble() * h,
                Vx = Math.Cos(ang) * speed, Vy = Math.Sin(ang) * speed,
                R = 1.1 + Rng.NextDouble() * 1.5,
                Alpha = Rng.NextDouble(),
                Accent = Rng.NextDouble() < 0.6,
            };
        }
    }

    // ---------- ciclo (30 fps, só com a janela à frente) ----------

    private void Start()
    {
        if (_running || !Enabled || ActualWidth < 1) return;
        _running = true;
        _clock.Restart();
        _lastMs = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = now - _lastMs;
        if (dt < FrameMs) return; // limita a 30 fps (o Rendering corre à frequência do monitor)
        _lastMs = now;
        dt = Math.Min(dt, 100) / 1000.0;

        var w = ActualWidth; var h = ActualHeight;
        for (var i = 0; i < _dots.Length; i++)
        {
            ref var d = ref _dots[i];
            d.X += d.Vx * dt; d.Y += d.Vy * dt;
            // sai por um lado, entra pelo outro (sem ressaltos bruscos)
            if (d.X < -10) d.X = w + 10; else if (d.X > w + 10) d.X = -10;
            if (d.Y < -10) d.Y = h + 10; else if (d.Y > h + 10) d.Y = -10;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Enabled || _dots.Length == 0 || _linkPens.Length == 0) return;

        // linhas primeiro (por baixo dos pontos)
        const double max2 = LinkDist * LinkDist;
        for (var i = 0; i < _dots.Length; i++)
        {
            for (var j = i + 1; j < _dots.Length; j++)
            {
                var dx = _dots[i].X - _dots[j].X; var dy = _dots[i].Y - _dots[j].Y;
                var d2 = dx * dx + dy * dy;
                if (d2 >= max2) continue;
                var k = (int)((1 - Math.Sqrt(d2) / LinkDist) * Levels);
                if (k <= 0) continue;
                dc.DrawLine(_linkPens[Math.Min(k, Levels) - 1], new Point(_dots[i].X, _dots[i].Y), new Point(_dots[j].X, _dots[j].Y));
            }
        }
        foreach (var d in _dots)
        {
            var fill = (d.Accent ? _accentFill : _greyFill)[(int)(d.Alpha * 7.99)];
            dc.DrawEllipse(fill, null, new Point(d.X, d.Y), d.R, d.R);
        }
    }
}
