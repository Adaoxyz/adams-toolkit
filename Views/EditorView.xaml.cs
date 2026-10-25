using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AdamsToolkit.Core;
using Range = AdamsToolkit.Core.VideoEditor.Range;

namespace AdamsToolkit.Views;

/// <summary>Editor estilo Avidemux: marcar A/B, apagar/manter troços, exportar por cópia ou recodificação.</summary>
public partial class EditorView : UserControl
{
    private string? _file;
    private VideoEditor.Info? _info;
    private double _dur;
    private double _pendingA = -1;                 // início marcado à espera do fim
    private double _pendingB = -1;                 // v1.88: fim marcado primeiro, à espera do início
    private List<Range> _segments = new();          // troços marcados por ordem (A→B, C→D, …)
    private readonly Stack<List<Range>> _undo = new();
    private static readonly Color[] Palette =
    {
        Color.FromRgb(168, 85, 247), Color.FromRgb(52, 211, 153), Color.FromRgb(251, 191, 36), Color.FromRgb(56, 189, 248),
        Color.FromRgb(248, 113, 113), Color.FromRgb(244, 114, 182), Color.FromRgb(163, 230, 53), Color.FromRgb(251, 146, 60),
    };
    private static Color ColorOf(int i) => Palette[i % Palette.Length];
    private static string Letters(int i) { var a = (char)('A' + (2 * i) % 26); var b = (char)('A' + (2 * i + 1) % 26); var n = (2 * i) / 26; return n == 0 ? $"{a}→{b}" : $"{a}{n}→{b}{n}"; }
    /// <summary>Intervalos que vão para o ficheiro, segundo o modo.</summary>
    private List<Range> Effective()
    {
        var segs = _segments.OrderBy(r => r.Start).ToList();
        if (segs.Count == 0) return new List<Range> { new(0, _dur) };
        if (ModeKeep.IsChecked == true) return segs;
        var full = new List<Range> { new(0, _dur) };
        foreach (var r in segs) full = VideoEditor.Cut(full, r.Start, r.End);
        return full;
    }
    private bool _playing, _seeking, _updating;
    private double _overlayFrom = -1; private DateTime _overlayUntil;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private CancellationTokenSource? _exportCts;
    private bool _hasNvenc;

    public sealed record RangeRow(int Index, string Label, Brush Brush);

    public EditorView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) =>
        {
            if (_playing && !_seeking) SyncFromPlayer();
            if (!_scrubbing && ScrubImage.Visibility == Visibility.Visible && _overlayFrom >= 0)
            {
                if (_playing)
                {
                    // v2.1.7: sem ponte animada (trocava de resolução e durava sempre 1,5 s) — a imagem
                    // do arrasto fica parada só até o player andar e sai logo
                    if (Pos > _overlayFrom + 0.03 || DateTime.UtcNow > _overlayUntil) { HideOverlay(); _overlayFrom = -1; }
                }
                else if (DateTime.UtcNow > _overlayUntil) { HideOverlay(); _overlayFrom = -1; }
            }
        };
        Loaded += async (_, _) => { if (!_hasNvenc && FfmpegManager.IsReady) _hasNvenc = (await FfmpegManager.ListEncodersAsync()).Contains("h264_nvenc"); };
    }

    public bool HasFile => _file != null;

    /// <summary>Abre um ficheiro (chamado pela janela principal).</summary>
    public async Task OpenAsync(string? file) { if (file != null) await LoadAsync(file); }

    /// <summary>Pára a pré-visualização quando se sai do separador.</summary>
    public void Suspend() { if (_playing) TogglePlay(); }
    public void Shutdown()
    {
        _exportCts?.Cancel(); _stripCts?.Cancel(); _proxyCts?.Cancel(); _scrubGrabCts?.Cancel(); _scrub?.Dispose(); _tick.Stop(); _scrubTimer.Stop(); try { Player.Close(); } catch { }
        foreach (var t in _tempJoins) { try { File.Delete(t); } catch { } }
    }

    // ------------------------------------------------------------------ load

    private async Task LoadAsync(string path)
    {
        try { await LoadCoreAsync(path); }
        catch (Exception ex) { EditorPaths.LogCrash(ex); FileInfo.Text = "erro: " + ex.Message; }
    }
    private async Task LoadCoreAsync(string path)
    {
        if (_playing) TogglePlay();
        _proxyCts?.Cancel(); _proxyFile = null; _pendingPos = -1; _scrubGrabCts?.Cancel(); ScrubImage.Visibility = Visibility.Collapsed; _scrub?.Dispose(); _scrub = null;
        try { Player.Stop(); Player.Close(); } catch { }
        if (!_tempJoins.Contains(path)) { _parts.Clear(); _partInfos.Clear(); }
        else { /* junção: infos já carregadas pelo AppendAsync */ }
        _file = path; _dur = 0; _info = null;
        FileTitle.Text = System.IO.Path.GetFileName(path);
        FileInfo.Text = "a ler…";
        _segments = new List<Range>();
        _undo.Clear(); _pendingA = -1; _pendingB = -1;
        // v1.85: o player abre JÁ (antes do probe) — e se o proxy 720p já existe abre-o diretamente,
        // sem passar pelo original + troca (era: probe bloqueante → original → troca p/ proxy = 2 aberturas).
        var cachedProxy = VideoEditor.ProxyPathFor(path);
        var openProxy = !_tempJoins.Contains(path) && File.Exists(cachedProxy);
        try { Player.IsMuted = true; Player.Source = new Uri(openProxy ? cachedProxy : path); Player.Play(); Player.Pause(); }
        catch (Exception ex) { StatusLine.Text = "Pré-visualização indisponível: " + ex.Message; }
        if (openProxy) _proxyFile = cachedProxy;
        _playing = false; SetPlayUi(false);
        EmptyHint.Visibility = Visibility.Collapsed;
        ExportBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = true;
        _tick.Start();
        var info = await VideoEditor.ProbeAsync(path); // em cache (.info) a partir da 2ª vez
        if (_file != path) return;
        _info = info;
        if (_info == null) { FileInfo.Text = "não deu para ler o ficheiro"; return; }
        _dur = _info.Duration;
        FileInfo.Text = $"{_info.Width}×{_info.Height} · {_info.Fps:0.##} fps · {_info.VideoCodec.ToUpper()} / {_info.AudioCodec.ToUpper()} · {Fmt(_dur)}";
        if (_partInfos.Count == 0) _partInfos.Add((System.IO.Path.GetFileNameWithoutExtension(path), _dur));
        Seek.Maximum = _dur;
        RedrawTrack(); UpdateRanges(); UpdateTime(0);
        _ = BuildProxyAsync(path);   // cria _scrub primeiro (síncrono) → a filmstrip aproveita-a
        _ = BuildStripAsync(path);
        if (_info.VideoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase))
            StatusLine.Text = "HEVC: se o vídeo não aparecer, instala a extensão 'HEVC Video Extensions' da Microsoft Store (a exportação funciona na mesma).";
    }

    private async void Open_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Vídeo|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts|Todos|*.*", InitialDirectory = EditorPaths.OutputDir };
        if (dlg.ShowDialog() == true) await LoadAsync(dlg.FileName);
    }
    private readonly List<string> _tempJoins = new();
    private readonly List<string> _parts = new();   // ficheiros que compõem o que está aberto
    private readonly List<(string name, double dur)> _partInfos = new();

    // AllowDrop não é herdado: sem isto, largar em cima do vídeo/painéis não dispara Drop
    private void View_Loaded(object s, RoutedEventArgs e)
    {
        void Walk(DependencyObject d)
        {
            if (d is UIElement u) u.AllowDrop = true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(this);
    }
    private void Window_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private bool _userMuted;
    // som só durante o Play: em pausa/arrastar o MediaElement solta pedaços de áudio ao procurar (scrub)
    private void ApplyMute() => Player.IsMuted = _userMuted || !_playing;
    private void Mute_Click(object s, RoutedEventArgs e)
    {
        _userMuted = !_userMuted; ApplyMute();
        MuteIcon.Text = _userMuted ? "🔇" : "🔊"; MuteLbl.Text = _userMuted ? "Sem som" : "Som";
    }

    /// <summary>Arrastar: sem ficheiro aberto → abre o 1º e junta os restantes; com ficheiro → junta todos no fim.</summary>
    private async void Window_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var list = files.Where(f => File.Exists(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        if (_file == null) { await LoadAsync(list[0]); list.RemoveAt(0); }
        if (list.Count > 0) await AppendAsync(list);
    }

    private async void Join_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Vídeo|*.mp4;*.mkv;*.mov;*.ts", Multiselect = true, InitialDirectory = EditorPaths.OutputDir, Title = "Escolhe os clips a juntar ao fim (Ctrl+clique)" };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;
        var list = dlg.FileNames.ToList();
        if (_file == null) { await LoadAsync(list[0]); list.RemoveAt(0); }
        if (list.Count > 0) await AppendAsync(list);
    }

    /// <summary>Junta ficheiros ao fim do atual (sem recodificar) — troços já marcados mantêm-se.</summary>
    private async Task AppendAsync(IList<string> more)
    {
        if (_file == null) return;
        // v1.84: junta a partir dos ORIGINAIS (o .ts de cada um fica cacheado) — nunca relê a junção anterior
        var prevParts = _parts.Count == 0 ? new List<string> { _file } : new List<string>(_parts);
        var inputs = new List<string>(prevParts); inputs.AddRange(more);
        var outPath = System.IO.Path.Combine(EditorPaths.LocalDir, $"edit-join-{Guid.NewGuid():N}.mkv");
        ExportText.Text = $"a juntar {more.Count} clip(s)…"; ExportBtn.IsEnabled = false; Status("a juntar…");
        var probeTask = Task.WhenAll(more.Select(VideoEditor.ProbeAsync));
        var err = await VideoEditor.JoinAsync(inputs, outPath, _ => { }, CancellationToken.None);
        if (err != null) { ExportText.Text = "Não deu para juntar: " + err; ExportBtn.IsEnabled = true; Status("não deu para juntar: " + err); return; }
        var probes = await probeTask;
        var newInfos = more.Select((f, i) => (System.IO.Path.GetFileNameWithoutExtension(f), probes[i]?.Duration ?? 0)).ToList();
        var keepSegs = _segments; var keepUndo = _undo.ToArray();
        if (_tempJoins.Contains(_file)) { try { File.Delete(_file); } catch { } _tempJoins.Remove(_file); }
        _tempJoins.Add(outPath);
        _parts.Clear(); _parts.AddRange(inputs);
        _partInfos.AddRange(newInfos);   // antes do Load: a cache/proxy do junto compõe-se a partir das partes
        await LoadAsync(outPath);
        _segments = keepSegs; foreach (var u in keepUndo.Reverse()) _undo.Push(u);
        FileTitle.Text = $"{_parts.Count} clips juntos: " + string.Join(" + ", _parts.Select(System.IO.Path.GetFileNameWithoutExtension));
        ExportText.Text = $"{_parts.Count} clips juntos — marca os troços e exporta.";
        Status($"juntos {_parts.Count} clips ({Fmt(_dur)})");
        UpdateRanges(); RedrawTrack();
    }

    // ---------------------------------------------------------------- player

    private double _pendingPos = -1; private bool _pendingPlay;
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan && _dur <= 0) { _dur = Player.NaturalDuration.TimeSpan.TotalSeconds; Seek.Maximum = _dur; }
        if (_pendingPos >= 0)
        {
            var p = _pendingPos; _pendingPos = -1;
            SeekTo(p);
            if (_pendingPlay && !_playing) TogglePlay();
            _pendingPlay = false;
        }
    }
    private void Player_MediaEnded(object s, RoutedEventArgs e) { Player.Pause(); _playing = false; SetPlayUi(false); }
    private void Player_MediaFailed(object s, ExceptionRoutedEventArgs e)
    {
        StatusLine.Text = "Pré-visualização indisponível (codec) — os cortes e a exportação funcionam na mesma. " + e.ErrorException.Message;
    }
    private void Player_Click(object s, MouseButtonEventArgs e) => UserTogglePlay();
    // só ações do utilizador mostram o ícone (o arrasto pausa/retoma por dentro sem piscar)
    private void UserTogglePlay() { if (_file == null) return; FlashPlayIcon(!_playing); TogglePlay(); }

    // ▶/⏸ no centro do vídeo: 0.6→1.0 de escala e fade in/out, ~650 ms (ease-out)
    private void FlashPlayIcon(bool playing)
    {
        PlayFlashIcon.Text = playing ? "▶" : "⏸";
        PlayFlashIcon.Margin = playing ? new Thickness(5, 0, 0, 2) : new Thickness(0, 0, 0, 2);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var op = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        op.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.95, TimeSpan.FromMilliseconds(0)));
        op.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.95, TimeSpan.FromMilliseconds(180)));
        op.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(650), ease));
        var sc = new System.Windows.Media.Animation.DoubleAnimation(0.6, 1.25, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease };
        PlayFlash.BeginAnimation(UIElement.OpacityProperty, op);
        PlayFlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
        PlayFlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
    }

    private void TogglePlay()
    {
        if (_file == null) return;
        if (_playing) { Player.Pause(); _playing = false; SetPlayUi(false); }
        else
        {
            // v2.1.7: Play direto no player, sem imagem da cache por cima
            Player.Play(); _playing = true; SetPlayUi(true);
        }
    }

    // troca imagem→vídeo com fade curto (a diferença 480p→1080p num corte seco lia-se como "paragem")
    private void HideOverlay()
    {
        // v1.79.2: sem fade (pedido) — corte direto
        ScrubImage.BeginAnimation(UIElement.OpacityProperty, null);
        ScrubImage.Opacity = 1; ScrubImage.Visibility = Visibility.Collapsed;
    }
    private void ShowOverlay()
    {
        ScrubImage.BeginAnimation(UIElement.OpacityProperty, null);
        ScrubImage.Opacity = 1; ScrubImage.Visibility = Visibility.Visible;
    }

    private void SetPlayUi(bool playing) { PlayIcon.Text = playing ? "⏸" : "▶"; PlayLbl.Text = playing ? "Pausa" : "Play"; ApplyMute(); }
    private void Play_Click(object s, RoutedEventArgs e) => UserTogglePlay();
    private void Start_Click(object s, RoutedEventArgs e) => SeekTo(0);
    private void FrameBack_Click(object s, RoutedEventArgs e) => Step(-5);
    private void FrameFwd_Click(object s, RoutedEventArgs e) => Step(5);
    private void Step(int frames)
    {
        if (_playing) TogglePlay();
        var fps = _info?.Fps is > 1 and < 500 ? _info.Fps : 60;
        SeekTo(Math.Clamp(Pos + frames / fps, 0, _dur));
    }

    private double Pos => Player.Position.TotalSeconds;
    private void SeekTo(double t)
    {
        t = Math.Clamp(t, 0, Math.Max(0, _dur));
        try { Player.Position = TimeSpan.FromSeconds(t); } catch { }
        _updating = true; Seek.Value = t; _updating = false;
        UpdateTime(t); RedrawTrack();
    }
    private void SyncFromPlayer()
    {
        var t = Pos;
        _updating = true; Seek.Value = t; _updating = false;
        UpdateTime(t); RedrawTrack();
    }
    private void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        if (_seeking) _scrubTarget = Seek.Value;
        else { try { Player.Position = TimeSpan.FromSeconds(Seek.Value); } catch { } }
        UpdateTime(Seek.Value); RedrawTrack();
    }
    private void Seek_Down(object s, MouseButtonEventArgs e)
    {
        _seeking = true; _scrubbing = true; _scrubWasPlaying = _playing; if (_playing) TogglePlay();
        StartPreSeek();
        _ = ScrubLoopAsync();
    }
    private void Seek_Up(object s, MouseButtonEventArgs e) => EndScrub();

    // Arrastar na timeline: captura o rato (continua a deslizar fora da barra) e
    // coalesce os seeks — o MediaElement engasga se levar um seek por pixel.
    private bool _scrubbing;
    private double _scrubTarget = -1;
    private readonly System.Windows.Threading.DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _scrubWasPlaying;

    private void Track_Down(object s, MouseButtonEventArgs e)
    {
        if (_dur <= 0) return;
        _scrubSurface = s as FrameworkElement ?? Track;
        _scrubWasPlaying = _playing;
        if (_playing) TogglePlay();
        _scrubbing = true; _seeking = true;
        _scrubSurface.CaptureMouse();
        ScrubTo(e.GetPosition(_scrubSurface).X, _scrubSurface.ActualWidth);
        StartPreSeek();
        _ = ScrubLoopAsync();
        e.Handled = true;
    }
    private void Track_Mouse(object s, MouseEventArgs e)
    {
        if (!_scrubbing || e.LeftButton != MouseButtonState.Pressed) return;
        ScrubTo(e.GetPosition(_scrubSurface).X, _scrubSurface.ActualWidth);
    }
    private void Track_Up(object s, MouseButtonEventArgs e) { if (_scrubbing) _scrubSurface.ReleaseMouseCapture(); }
    private void Track_Lost(object s, MouseEventArgs e) => EndScrub();
    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false; _seeking = false;
        _scrubTimer.Stop();
        var t = _scrubTarget >= 0 ? _scrubTarget : Seek.Value; _scrubTarget = -1;
        SeekTo(t);
        if (_scrubWasPlaying) TogglePlay();
        // a imagem do ffmpeg fica a tapar até o vídeo mostrar mesmo o ponto novo:
        // a tocar → some assim que a posição avança; em pausa → 350 ms (tempo do seek)
        _overlayFrom = t; _overlayUntil = DateTime.UtcNow.AddMilliseconds(_scrubWasPlaying ? 1500 : 350);
    }

    // ---- frame server: um ffmpeg de cada vez, sempre o alvo mais recente ----
    private System.Windows.Media.Imaging.WriteableBitmap? _scrubBmp;
    private int _scrubW, _scrubH;
    private CancellationTokenSource? _scrubGrabCts;
    private async Task ScrubLoopAsync()
    {
        var src = _proxyFile ?? _file;
        if (src == null || _info == null || !FfmpegManager.IsReady) return;
        // v1.88: qualidade estilo Avidemux — frame exato à resolução do clip (limite 1440p p/ não pesar)
        var aspect = _info.Width > 0 && _info.Height > 0 ? (double)_info.Width / _info.Height : 16.0 / 9;
        var h = Math.Max(2, Math.Min(_info.Height > 0 ? _info.Height : 720, 1440) / 2 * 2);
        var w = Math.Max(2, (int)Math.Round(h * aspect / 2) * 2);
        if (_scrubBmp == null || _scrubW != w || _scrubH != h)
        {
            _scrubBmp = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _scrubW = w; _scrubH = h; ScrubImage.Source = _scrubBmp;
        }
        _scrubGrabCts?.Cancel();
        var cts = _scrubGrabCts = new CancellationTokenSource();
        double shown = -1;
        while (_scrubbing && !cts.IsCancellationRequested)
        {
            var t = _scrubTarget >= 0 ? _scrubTarget : Seek.Value;
            if (Math.Abs(t - shown) < 0.0005) { await Task.Delay(4, CancellationToken.None); continue; }
            var cache = _scrub;
            if (cache != null)
            {
                // caminho rápido: JPEG por offset → pixels BGRA (thread de fundo) → WritePixels no
                // MESMO WriteableBitmap (sem textura nova por frame: era isso que engasgava a 60 Hz)
                var frame = await Task.Run<(byte[] px, int cw, int chh)?>(() =>
                {
                    var jpg = cache.Get(t); if (jpg == null) return null;
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(jpg); bi.EndInit(); bi.Freeze();
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0); conv.Freeze();
                    var cw = conv.PixelWidth; var chh = conv.PixelHeight;
                    var px = new byte[cw * chh * 4];
                    conv.CopyPixels(px, cw * 4, 0);
                    return (px, cw, chh);
                });
                if (cts.IsCancellationRequested) break;
                if (frame == null) { var px2 = await VideoEditor.GrabFrameAsync(src, t, w, h, cts.Token); if (px2 != null && !cts.IsCancellationRequested) { if (_scrubBmp == null || _scrubW != w || _scrubH != h) { _scrubBmp = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null); _scrubW = w; _scrubH = h; ScrubImage.Source = _scrubBmp; } _scrubBmp.WritePixels(new Int32Rect(0, 0, w, h), px2, w * 4, 0); if (!ReferenceEquals(ScrubImage.Source, _scrubBmp)) ScrubImage.Source = _scrubBmp; ShowOverlay(); } shown = t; continue; }
                if (frame is { } f)
                {
                    if (_scrubBmp == null || _scrubW != f.cw || _scrubH != f.chh)
                    {
                        _scrubBmp = new System.Windows.Media.Imaging.WriteableBitmap(f.cw, f.chh, 96, 96, PixelFormats.Bgra32, null);
                        _scrubW = f.cw; _scrubH = f.chh; ScrubImage.Source = _scrubBmp;
                    }
                    _scrubBmp.WritePixels(new Int32Rect(0, 0, f.cw, f.chh), f.px, f.cw * 4, 0);
                    if (!ReferenceEquals(ScrubImage.Source, _scrubBmp)) ScrubImage.Source = _scrubBmp;
                    ShowOverlay();
                }
                shown = t; continue;
            }
            var px = await VideoEditor.GrabFrameAsync(src, t, w, h, cts.Token);
            if (cts.IsCancellationRequested) break;
            if (px != null)
            {
                _scrubBmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
                if (!ReferenceEquals(ScrubImage.Source, _scrubBmp)) ScrubImage.Source = _scrubBmp;
                ShowOverlay();
                shown = t;
            }
            else shown = t; // falhou (fora do fim?) — não insistir no mesmo t
        }
    }
    private FrameworkElement _scrubSurface = null!;
    private void ScrubTo(double x, double width)
    {
        var t = Math.Clamp(x / Math.Max(1, width), 0, 1) * _dur;
        // v1.88.1: estilo Avidemux — onde só há keyframes, o cursor SALTA keyframe a keyframe:
        // o frame mostrado é o da posição real, e ao largar não aparece "outro frame" ms depois
        if (_scrub is { } sc) t = sc.SnapForScrub(t);
        _scrubTarget = t;
        // UI segue o rato de imediato; o vídeo apanha no próximo tick
        _updating = true; Seek.Value = t; _updating = false;
        UpdateTime(t); RedrawTrack();
    }
    // Enquanto arrastas, o player (escondido por baixo da imagem) vai sendo posto perto do
    // alvo a cada ~120 ms — ao largar o seek final é curto e o Play arranca sem pausa.
    private DateTime _lastPreSeek;
    private double _preSeekPos = -1;
    private void StartPreSeek()
    {
        _lastPreSeek = DateTime.MinValue; _preSeekPos = -1;
        if (!_scrubTimer.IsEnabled) { _scrubTimer.Tick -= ScrubTick; _scrubTimer.Tick += ScrubTick; _scrubTimer.Start(); }
    }
    private void ScrubTick(object? s, EventArgs e)
    {
        if (!_scrubbing) { _scrubTimer.Stop(); return; }
        var t = _scrubTarget >= 0 ? _scrubTarget : Seek.Value;
        if ((DateTime.UtcNow - _lastPreSeek).TotalMilliseconds < 120 || Math.Abs(t - _preSeekPos) < 0.02) return;
        _lastPreSeek = DateTime.UtcNow; _preSeekPos = t;
        try { Player.Position = TimeSpan.FromSeconds(t); } catch { }
    }
    private void Track_SizeChanged(object s, SizeChangedEventArgs e) => RedrawTrack();

    private void UpdateTime(double t) => TimeText.Text = $"{Fmt(t)} / {Fmt(_dur)}";
    private static string Fmt(double t) => t < 0 ? "--:--.---" : $"{(int)t / 60:00}:{t % 60:00.000}";

    // --------------------------------------------------------------- marking

    // v1.88: marcação em qualquer ordem — podes marcar o Fim primeiro e o Início depois.
    // A única regra: o Início tem de ficar ANTES do Fim (tentativa ao contrário é rejeitada, sem troca silenciosa).
    private void SetA_Click(object s, RoutedEventArgs e)
    {
        var t = Pos;
        if (_pendingB >= 0)
        {
            if (t >= _pendingB) { Status($"o Início tem de ficar ANTES do Fim já marcado ({Fmt(_pendingB)}) — recua e volta a marcar"); return; }
            CommitSegment(t, _pendingB);
            return;
        }
        _pendingA = t;
        var n = _segments.Count;
        StartLbl.Text = $"Início ({Letters(n).Split('→')[0]})"; EndLbl.Text = $"Fim ({Letters(n).Split('→')[1]})";
        StartIcon.Foreground = EndIcon.Foreground = new SolidColorBrush(ColorOf(n));
        Status($"início do troço {Letters(n).Split('→')[0]} = {Fmt(_pendingA)} — agora marca o Fim (à frente)");
        RedrawTrack();
    }
    private void SetB_Click(object s, RoutedEventArgs e)
    {
        var t = Pos;
        if (_pendingA >= 0)
        {
            if (t <= _pendingA) { Status($"o Fim tem de ficar DEPOIS do Início já marcado ({Fmt(_pendingA)}) — avança e volta a marcar"); return; }
            CommitSegment(_pendingA, t);
            return;
        }
        _pendingB = t;
        var n = _segments.Count;
        StartLbl.Text = $"Início ({Letters(n).Split('→')[0]})"; EndLbl.Text = $"Fim ({Letters(n).Split('→')[1]})";
        StartIcon.Foreground = EndIcon.Foreground = new SolidColorBrush(ColorOf(n));
        Status($"fim do troço {Letters(n).Split('→')[1]} = {Fmt(_pendingB)} — agora marca o Início (atrás)");
        RedrawTrack();
    }
    private void CommitSegment(double a, double b)
    {
        if (b - a < 0.05) { Status("troço demasiado curto"); return; }
        _undo.Push(_segments);
        _segments = _segments.Append(new Range(a, b)).ToList();
        _pendingA = -1; _pendingB = -1;
        var n = _segments.Count;
        Status($"troço {Letters(n - 1)} marcado ({Fmt(a)} → {Fmt(b)}) — próximo: {Letters(n)}");
        StartLbl.Text = $"Início ({Letters(n).Split('→')[0]})"; EndLbl.Text = $"Fim ({Letters(n).Split('→')[1]})";
        StartIcon.Foreground = EndIcon.Foreground = new SolidColorBrush(ColorOf(n));
        UpdateRanges(); RedrawTrack();
    }
    // corta já: o clip passa a ser só o minuto antes da linha (troços anteriores substituídos; Ctrl+Z desfaz)
    private void LastMinute_Click(object s, RoutedEventArgs e)
    {
        if (_file == null) return;
        var b = Pos; var a = Math.Max(0, b - 60);
        if (b - a < 0.05) { Status("anda com a linha para a frente primeiro"); return; }
        _undo.Push(_segments);
        _segments = new List<Range> { new(a, b) };
        _pendingA = -1; _pendingB = -1;
        ModeKeep.IsChecked = true;
        UpdateRanges(); RedrawTrack();
        Status($"clip cortado: último minuto {Fmt(a)} → {Fmt(b)} — é só exportar");
    }
    private void RemoveLast_Click(object s, RoutedEventArgs e)
    {
        if (_pendingA >= 0 || _pendingB >= 0) { var wasA = _pendingA >= 0; _pendingA = -1; _pendingB = -1; Status(wasA ? "início cancelado" : "fim cancelado"); RedrawTrack(); return; }
        if (_segments.Count == 0) return;
        _undo.Push(_segments);
        _segments = _segments.Take(_segments.Count - 1).ToList();
        UpdateRanges(); RedrawTrack();
    }
    private void SegMode_Changed(object s, RoutedEventArgs e) { if (RangesTitle != null) UpdateRanges(); }
    private void Undo_Click(object s, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _segments = _undo.Pop(); _pendingA = -1; _pendingB = -1; UpdateRanges(); RedrawTrack(); Status("anulado");
    }
    private void RangeRemove_Click(object s, RoutedEventArgs e)
    {
        var i = (int)((Button)s).Tag;
        if (i < 0 || i >= _segments.Count) return;
        _undo.Push(_segments);
        _segments = _segments.Where((_, k) => k != i).ToList();
        UpdateRanges(); RedrawTrack();
    }

    private void UpdateRanges()
    {
        RangesList.ItemsSource = _segments.Select((r, i) => new RangeRow(i, $"{Letters(i),-6} {Fmt(r.Start)} → {Fmt(r.End)}  ({r.Length:0.0}s)", new SolidColorBrush(ColorOf(i)))).ToList();
        var eff = Effective();
        var keep = ModeKeep.IsChecked == true;
        RangesTitle.Text = _segments.Count == 0 ? "Troços marcados" : keep ? $"Troços a manter ({_segments.Count})" : $"Troços a apagar ({_segments.Count})";
        RangesTotal.Text = _segments.Count == 0 ? $"sem troços — exporta o clip inteiro ({Fmt(_dur)})" : $"ficheiro final: {Fmt(eff.Sum(r => r.Length))}";
        ExportBtn.IsEnabled = _file != null && eff.Count > 0;
        var n = _segments.Count;
        if (StartLbl != null && _pendingA < 0 && _pendingB < 0)
        {
            StartLbl.Text = $"Início ({Letters(n).Split('→')[0]})"; EndLbl.Text = $"Fim ({Letters(n).Split('→')[1]})";
            StartIcon.Foreground = EndIcon.Foreground = new SolidColorBrush(ColorOf(n));
        }
    }

    private void RedrawTrack()
    {
        Track.Children.Clear(); ClipsBar.Children.Clear();
        var w = Track.ActualWidth; var h = Track.ActualHeight;
        if (w <= 0 || _dur <= 0) return;
        double X(double t) => t / _dur * w;
        // barra de cima: que clip é qual (marcadores com o nome)
        if (_partInfos.Count > 1)
        {
            double t0 = 0;
            for (var i = 0; i < _partInfos.Count; i++)
            {
                var (name, d) = _partInfos[i];
                var shade = i % 2 == 0 ? Color.FromArgb(200, 42, 36, 80) : Color.FromArgb(200, 60, 50, 110);
                var blk = new Border { Width = Math.Max(2, X(t0 + d) - X(t0) - 2), Height = 18, Background = new SolidColorBrush(shade), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0), ClipToBounds = true,
                    Child = new TextBlock { Text = $"{i + 1} · {name}", FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
                Canvas.SetLeft(blk, X(t0)); ClipsBar.Children.Add(blk);
                if (i > 0)
                {
                    var line = new Rectangle { Width = 2, Height = h, Fill = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)) };
                    Canvas.SetLeft(line, X(t0)); Track.Children.Add(line);
                }
                t0 += d;
            }
        }
        var keep = ModeKeep.IsChecked == true;
        // fundo: no modo "manter" o resto fica escuro; no modo "apagar" o resto fica verde claro
        if (!keep && _segments.Count > 0)
        {
            var bg = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Color.FromArgb(70, 52, 211, 153)) };
            Track.Children.Add(bg);
        }
        for (var i = 0; i < _segments.Count; i++)
        {
            var r = _segments[i]; var c = ColorOf(i);
            var rect = new Rectangle { Width = Math.Max(2, X(r.End) - X(r.Start)), Height = h, Fill = new SolidColorBrush(Color.FromArgb(keep ? (byte)150 : (byte)200, c.R, c.G, c.B)) };
            Canvas.SetLeft(rect, X(r.Start)); Track.Children.Add(rect);
            var lbl = new TextBlock { Text = Letters(i), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            Canvas.SetLeft(lbl, X(r.Start) + 3); Canvas.SetTop(lbl, 6); Track.Children.Add(lbl);
        }
        if (_pendingA >= 0)
        {
            var c = ColorOf(_segments.Count);
            var m = new Rectangle { Width = 3, Height = h, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(m, X(_pendingA)); Track.Children.Add(m);
            var tb = new TextBlock { Text = Letters(_segments.Count).Split('→')[0] + " …", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c) };
            Canvas.SetLeft(tb, X(_pendingA) + 5); Canvas.SetTop(tb, 6); Track.Children.Add(tb);
        }
        if (_pendingB >= 0)
        {
            var c = ColorOf(_segments.Count);
            var m = new Rectangle { Width = 3, Height = h, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(m, X(_pendingB)); Track.Children.Add(m);
            var tb = new TextBlock { Text = "… " + Letters(_segments.Count).Split('→')[1], FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c) };
            Canvas.SetRight(tb, w - X(_pendingB) + 5); Canvas.SetTop(tb, 6); Track.Children.Add(tb);
        }
        var cur = new Rectangle { Width = 2, Height = h, Fill = Brushes.White };
        Canvas.SetLeft(cur, X(Seek.Value)); Track.Children.Add(cur);
        if (StripBox.Visibility == Visibility.Visible)
            StripCursor.Margin = new Thickness(Math.Clamp(Seek.Value / _dur, 0, 1) * StripBox.ActualWidth, 0, 0, 0);
    }

    // --------------------------------------------------- cache de arrasto (progressiva)
    // v1.78: sem proxy — a reprodução usa o original; o arrasto vem desta cache, que fica
    // utilizável poucos segundos depois de abrir (no troço já gerado) e cresce em fundo.
    private CancellationTokenSource? _proxyCts;
    private string? _proxyFile;            // (mantido a null — já não há proxy)
    private ScrubCache? _scrub;
    private async Task BuildProxyAsync(string path)
    {
        _proxyCts?.Cancel();
        var cts = _proxyCts = new CancellationTokenSource();
        _scrub?.Dispose(); _scrub = null;
        if (_dur <= 0 || !FfmpegManager.IsReady) return;
        if (!ScrubCache.KeysOnly && _parts.Count > 1 && _partInfos.Count == _parts.Count && _tempJoins.Contains(path))
        {
            try { await BuildJoinedAsync(path, _parts.ToList(), _partInfos.Select(p => p.dur).ToList(), cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { EditorPaths.LogCrash(ex); }
            return;
        }
        ScrubCache? cache = null;
        try
        {
            cache = ScrubCache.OpenOrBuild(path, ScrubCache.PathFor(path), _dur, cts.Token);
            _scrub = cache;
            var c = cache;
            void Upd() => Dispatcher.BeginInvoke(() =>
            {
                if (_file != path || !ReferenceEquals(_scrub, c)) return;
                if (c.Failed) { ExportText.Text = "arrasto instantâneo indisponível (fica o modo lento)"; return; }
                if (c.Complete) { if (_proxyFile == null) ExportText.Text = ""; Status("arrasto instantâneo pronto para o clip todo"); return; }
                if (c.Light) { ExportText.Text = ""; Status($"pronto ({c.KeyframeCount} keyframes)"); return; }
                ExportText.Text = (c.KeyframesReady ? $"arrasto: keyframes prontos ({c.KeyframeCount}) · detalhe {c.Fraction * 100:0}%" : $"arrasto: a ler keyframes… · detalhe {c.Fraction * 100:0}%") + $" · {(c.Hw == "" ? "CPU" : c.Hw == "?" ? "…" : "GPU " + c.Hw)}";
            });
            c.Progress += Upd;
            Upd();
        }
        catch (Exception ex) { EditorPaths.LogCrash(ex); }

        // v1.81: proxy 720p/60 p/ reprodução (mesma qualidade da cache → troca invisível).
        // Arranca depois da cache (para não competir CPU) e troca a fonte sem perder posição.
        try
        {
            while (cache != null && !cache.Complete && !cache.Failed && !cache.Light && !cts.IsCancellationRequested) await Task.Delay(200, CancellationToken.None);
            if (cts.IsCancellationRequested || _file != path) return;
            var proxy = VideoEditor.ProxyPathFor(path);
            if (!File.Exists(proxy))
            {
                if (cache == null || cache.Light || !cache.Complete) return; // v1.87: sem proxy em modo leve
                if (!_hasNvenc) _hasNvenc = (await FfmpegManager.ListEncodersAsync()).Contains("h264_nvenc");
                var prog = new Progress<double>(p => { if (_file == path) ExportText.Text = $"a preparar vídeo 720p… {p * 100:0}%"; });
                var ok = await VideoEditor.BuildProxyAsync(path, proxy, _hasNvenc, prog, _dur, cts.Token, cache?.SegmentFiles);
                if (!ok && cache?.SegmentFiles.Count > 0 && !cts.IsCancellationRequested) ok = await VideoEditor.BuildProxyAsync(path, proxy, _hasNvenc, prog, _dur, cts.Token);
                if (_file == path) ExportText.Text = "";
                if (!ok) return;
            }
            if (cts.IsCancellationRequested || _file != path) return;
            if (_proxyFile == proxy) return; // v1.85: já abriu diretamente no proxy
            var pos = Pos; var wasPlaying = _playing;
            if (_playing) TogglePlay();
            _pendingPos = pos; _pendingPlay = wasPlaying;
            try { Player.Close(); Player.Source = new Uri(proxy); Player.Play(); Player.Pause(); }
            catch (Exception ex) { EditorPaths.LogCrash(ex); _pendingPos = -1; return; }
            _proxyFile = proxy;
            Status("vídeo 720p pronto — reprodução e arrasto na mesma qualidade (exportação usa o original)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EditorPaths.LogCrash(ex); }
    }

    /// <summary>
    /// v1.84: ficheiro JUNTO — cache de arrasto e proxy compostos a partir dos clips originais.
    /// Cada clip é preparado 1 vez (sequencial, para não afogar o PC) e fica em cache; juntar
    /// mais um só prepara o novo. Nada é recalculado sobre o ficheiro junto.
    /// </summary>
    private async Task BuildJoinedAsync(string path, List<string> parts, List<double> durs, CancellationToken ct)
    {
        var caches = new List<(ScrubCache cache, double offset)>();
        double off = 0;
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i]; var d = durs[i] > 0 ? durs[i] : (await VideoEditor.ProbeAsync(part))?.Duration ?? 0;
                if (d <= 0) return;
                var c = ScrubCache.OpenOrBuild(part, ScrubCache.PathFor(part), d, ct);
                var idx = i;
                void Upd() { if (_file == path) ExportText.Text = $"a preparar clip {idx + 1}/{parts.Count}… {c.Fraction * 100:0}%"; }
                c.Progress += () => Dispatcher.BeginInvoke(Upd);
                Upd();
                while (!c.Complete && !c.Failed && !c.Light) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, CancellationToken.None); }
                if (c.Failed) { c.Dispose(); if (_file == path) ExportText.Text = "arrasto instantâneo indisponível (fica o modo lento)"; return; }
                if (c.Light) { c.Dispose(); if (_file == path) ExportText.Text = ""; return; } // v1.87: modo leve — sem composição/proxy
                caches.Add((c, off)); off += d;
            }
            ct.ThrowIfCancellationRequested();
            var composed = ScrubCache.Compose(caches);
            if (composed != null && _file == path) { _scrub = composed; ExportText.Text = ""; Status("arrasto instantâneo pronto para os clips todos"); }
        }
        finally { foreach (var (c, _) in caches) c.Dispose(); }
        if (_file != path) return;

        // proxy 720p/60 do junto = concat por cópia dos proxies de cada clip (feitos 1 vez)
        var proxy = VideoEditor.ProxyPathFor(path);
        if (!File.Exists(proxy))
        {
            if (!_hasNvenc) _hasNvenc = (await FfmpegManager.ListEncodersAsync()).Contains("h264_nvenc");
            var partProxies = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                var pp = VideoEditor.ProxyPathFor(parts[i]);
                if (!File.Exists(pp))
                {
                    var idx = i;
                    var prog = new Progress<double>(p => { if (_file == path) ExportText.Text = $"a preparar vídeo 720p do clip {idx + 1}/{parts.Count}… {p * 100:0}%"; });
                    var segs = i < caches.Count ? caches[i].cache.SegmentFiles : null;
                    var okp = await VideoEditor.BuildProxyAsync(parts[i], pp, _hasNvenc, prog, durs[i], ct, segs);
                    if (!okp && segs?.Count > 0 && !ct.IsCancellationRequested) okp = await VideoEditor.BuildProxyAsync(parts[i], pp, _hasNvenc, prog, durs[i], ct);
                    if (!okp) { if (_file == path) ExportText.Text = ""; return; }
                }
                partProxies.Add(pp);
            }
            if (_file == path) ExportText.Text = "a juntar vídeo 720p…";
            var ok = await VideoEditor.ConcatCopyAsync(partProxies, proxy, ct);
            if (_file == path) ExportText.Text = "";
            if (!ok) return;
        }
        if (ct.IsCancellationRequested || _file != path) return;
        var pos = Pos; var wasPlaying = _playing;
        if (_playing) TogglePlay();
        _pendingPos = pos; _pendingPlay = wasPlaying;
        try { Player.Close(); Player.Source = new Uri(proxy); Player.Play(); Player.Pause(); }
        catch (Exception ex) { EditorPaths.LogCrash(ex); _pendingPos = -1; return; }
        _proxyFile = proxy;
        Status("vídeo 720p pronto — reprodução e arrasto na mesma qualidade (exportação usa os originais)");
    }

    // ------------------------------------------------------------- filmstrip
    // Uma imagem só (tile Nx1, 56 px de altura) gerada pelo ffmpeg em background;
    // ~1 miniatura por 90 px de barra. Ficheiro novo cancela o anterior.
    private CancellationTokenSource? _stripCts;
    private async Task BuildStripAsync(string path)
    {
        _stripCts?.Cancel();
        var cts = _stripCts = new CancellationTokenSource();
        StripBox.Visibility = Visibility.Collapsed; Strip.Source = null;
        if (_dur <= 0 || !FfmpegManager.IsReady) return;
        var n = Math.Clamp((int)(Math.Max(600, Track.ActualWidth) / 90), 8, 40);
        // v1.85: miniaturas a partir da cache de arrasto (JPEGs já no disco/memória) — zero ffmpeg.
        // Espera até 6 s pelos keyframes; só cai no ffmpeg se a cache falhar ou não existir.
        var sc = _scrub;
        if (sc != null && ReferenceEquals(sc, _scrub))
        {
            var waited = 0;
            while (!sc.Failed && !sc.Complete && !sc.KeyframesReady && waited < 6000 && !cts.IsCancellationRequested) { await Task.Delay(100, CancellationToken.None); waited += 100; }
            if (cts.IsCancellationRequested || _file != path) return;
            if (!sc.Failed && (sc.Complete || sc.KeyframesReady) && await TryStripFromCacheAsync(sc, n, 56, cts.Token)) return;
        }
        var png = System.IO.Path.Combine(EditorPaths.LocalDir, $"strip-{Guid.NewGuid():N}.png");
        try
        {
            var ok = await VideoEditor.FilmstripAsync(path, _dur, n, 56, png, cts.Token);
            if (!ok || cts.IsCancellationRequested || !File.Exists(png)) return;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(png); bmp.EndInit(); bmp.Freeze();
            Strip.Source = bmp; StripBox.Visibility = Visibility.Visible;
            RedrawTrack();
        }
        catch (Exception ex) { EditorPaths.LogCrash(ex); }
        finally { try { File.Delete(png); } catch { } }
    }

    private async Task<bool> TryStripFromCacheAsync(ScrubCache sc, int n, int h, CancellationToken ct)
    {
        try
        {
            var dur = _dur; var step = Math.Max(0.05, dur / n);
            var frames = await Task.Run(() =>
            {
                var list = new System.Windows.Media.Imaging.BitmapSource?[n];
                for (var i = 0; i < n; i++)
                {
                    if (ct.IsCancellationRequested) return null;
                    var jpg = sc.Get(Math.Min(dur - 0.01, i * step + step / 2));
                    if (jpg == null) continue;
                    try
                    {
                        var b = new System.Windows.Media.Imaging.BitmapImage();
                        b.BeginInit(); b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.DecodePixelHeight = h;
                        b.StreamSource = new MemoryStream(jpg); b.EndInit(); b.Freeze();
                        list[i] = b;
                    }
                    catch { }
                }
                return list;
            }, ct);
            if (frames == null || ct.IsCancellationRequested || frames.All(f => f == null)) return false;
            var w = (int)Math.Round(h * 16.0 / 9);
            var first = frames.First(f => f != null)!; if (first.PixelHeight > 0) w = (int)Math.Round((double)h * first.PixelWidth / first.PixelHeight);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                for (var i = 0; i < n; i++) if (frames[i] != null) dc.DrawImage(frames[i], new Rect(i * w, 0, w, h));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w * n, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            Strip.Source = rtb; StripBox.Visibility = Visibility.Visible;
            RedrawTrack();
            return true;
        }
        catch (Exception ex) { EditorPaths.LogCrash(ex); return false; }
    }

    // ---------------------------------------------------------------- export

    private void Mode_Changed(object s, RoutedEventArgs e)
    {
        if (ModeHint == null) return;
        var gif = (FmtCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "gif";
        var precise = ModePrecise.IsChecked == true || gif;
        ResCombo.IsEnabled = SpeedCombo.IsEnabled = QualCombo.IsEnabled = precise;
        VolSlider.IsEnabled = precise && MuteCheck.IsChecked != true;
        ModeHint.Text = gif ? "GIF: 15 fps, sem som, recodifica sempre." :
            precise ? $"Preciso: corte exato ao frame, recodifica com {(_hasNvenc ? "NVENC" : "x264")}." :
            "Rápido: sem perda de qualidade, corte ao keyframe (~2 s).";
    }

    private async void Export_Click(object s, RoutedEventArgs e)
    {
        var ranges = Effective();
        if (_file == null || ranges.Count == 0) return;
        if (_exportCts != null) { _exportCts.Cancel(); return; }
        var fmt = (FmtCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp4";
        var o = new VideoEditor.ExportOptions
        {
            Reencode = ModePrecise.IsChecked == true || fmt == "gif",
            Container = fmt,
            Height = int.TryParse((ResCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hh) && hh > 0 ? hh : null,
            Mute = MuteCheck.IsChecked == true,
            Volume = (float)(VolSlider.Value / 100),
            UseNvenc = _hasNvenc,
            Crf = int.TryParse((QualCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var crf) ? crf : 14,
            Speed = double.TryParse((SpeedCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 1,
        };
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = EditorPaths.OutputDir,
            FileName = (_parts.Count > 1 ? "Clips juntos " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") : System.IO.Path.GetFileNameWithoutExtension(_file) + " (editado)") + "." + fmt,
            Filter = fmt switch { "mkv" => "MKV|*.mkv", "gif" => "GIF|*.gif", _ => "MP4|*.mp4" },
        };
        if (dlg.ShowDialog() != true) return;

        if (_playing) TogglePlay();
        _exportCts = new CancellationTokenSource();
        ExportBtn.Content = "Cancelar"; ExportBar.Visibility = Visibility.Visible; ExportBar.Value = 0; ExportText.Text = "a exportar…";
        var prog = new Progress<double>(p => { ExportBar.Value = p; ExportText.Text = $"a exportar… {p * 100:0}%"; });
        var err = await VideoEditor.ExportAsync(_file, ranges, o, dlg.FileName, prog, _ => { }, _exportCts.Token);
        _exportCts = null;
        ExportBtn.Content = "Exportar"; ExportBar.Visibility = Visibility.Collapsed;
        ExportText.Text = err == null ? "Guardado: " + System.IO.Path.GetFileName(dlg.FileName) : "Falhou: " + err;
        if (err == null) Status("exportado para " + dlg.FileName);
    }

    /// <summary>Fecha o clip no editor — o ficheiro não é tocado.</summary>
    private void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_file == null) return;
        if (_playing) TogglePlay();
        _proxyCts?.Cancel(); _stripCts?.Cancel(); _scrubGrabCts?.Cancel(); _exportCts?.Cancel();
        _scrub?.Dispose(); _scrub = null; _proxyFile = null;
        try { Player.Stop(); Player.Close(); Player.Source = null; } catch { }
        foreach (var t in _tempJoins) { try { File.Delete(t); } catch { } }
        _tempJoins.Clear();
        _file = null; _info = null; _dur = 0; _segments = new(); _undo.Clear(); _pendingA = -1; _pendingB = -1; _parts.Clear(); _partInfos.Clear();
        FileTitle.Text = "Sem ficheiro — abre um clip ou arrasta para aqui"; FileInfo.Text = "";
        EmptyHint.Visibility = Visibility.Visible; ExportBtn.IsEnabled = false; DeleteBtn.IsEnabled = false;
        StripBox.Visibility = Visibility.Collapsed; Strip.Source = null; ScrubImage.Visibility = Visibility.Collapsed;
        Track.Children.Clear(); ClipsBar.Children.Clear(); RangesList.ItemsSource = null; RangesTitle.Text = "Troços marcados"; RangesTotal.Text = ""; ExportText.Text = "";
        Seek.Value = 0; UpdateTime(0);
        Status("clip fechado");
    }

    private async void Snapshot_Click(object s, RoutedEventArgs e)
    {
        if (_file == null) return;
        var png = System.IO.Path.Combine(EditorPaths.OutputDir, $"Frame {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        var ok = await VideoEditor.FrameToPngAsync(_file, Pos, png);
        Status(ok ? "frame guardado: " + System.IO.Path.GetFileName(png) : "não deu para guardar o frame");
    }

    // ------------------------------------------------------------------ keys

    private void Window_KeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: UserTogglePlay(); break;
            case Key.Left: Step(Keyboard.Modifiers == ModifierKeys.Shift ? -30 : -5); break;
            case Key.Right: Step(Keyboard.Modifiers == ModifierKeys.Shift ? 30 : 5); break;
            case Key.Home: SeekTo(0); break;
            case Key.End: SeekTo(_dur); break;
            case Key.A: SetA_Click(s, e); break;
            case Key.B: SetB_Click(s, e); break;
            case Key.Delete: RemoveLast_Click(s, e); break;
            case Key.M: Mute_Click(s, e); break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: Undo_Click(s, e); break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control: if (ExportBtn.IsEnabled) Export_Click(s, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Status(string m) => StatusLine.Text = m;
}
