using System.Diagnostics;
using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// Cache de arrasto em 720p/60 fps, gerada em PARALELO: o clip é partido em N segmentos e
/// cada um é um ffmpeg próprio (MJPEG cru → pipe) — o encoder MJPEG é single-thread, por isso
/// N processos ≈ N× mais rápido. Cada segmento é indexado (SOI) à medida que chega, logo o
/// arrasto fica disponível nos primeiros segundos e vai preenchendo. Ficheiros:
/// base.sK.mjpg + .idx por segmento, base.ok quando está tudo.
/// </summary>
public sealed class ScrubCache : IDisposable
{
    public const int Fps = 60, Height = 720; // v1.81: 720p/60 (1080p "bugava" no PC do user)
    public const int KeyHeight = 1440;       // v1.88: keyframes (fase Avidemux) à resolução do clip, cap 1440p

    private sealed class Seg
    {
        public double Start, Len;
        public string Path = "";
        public FileStream? Reader;
        public readonly List<long> Off = new(1 << 13);
        public long End;
        public bool Done, Failed;
    }

    private readonly string _base;
    private readonly Seg[] _segs;
    // fase 1 (estilo Avidemux): só keyframes, em memória — pronto em segundos
    private readonly List<(double t, byte[] jpg)> _keys = new();
    public bool KeyframesReady { get; private set; }
    public int KeyframeCount { get { lock (_lock) return _keys.Count; } }
    private readonly object _lock = new();
    public static bool KeysOnly = true;   // v1.87: prioridade = abrir rápido
    public bool Light { get; private set; } // true = terminou em modo leve (só keyframes)
    public bool Complete { get; private set; }
    public bool Failed { get; private set; }
    public event Action? Progress;
    /// <summary>Descodificador usado ("cuda" / "d3d11va" / "" = CPU) — p/ diagnóstico.</summary>
    public string Hw { get; private set; } = "?";

    /// <summary>Segundos contíguos desde o início já disponíveis.</summary>
    public double Available
    {
        get
        {
            lock (_lock)
            {
                double t = 0;
                foreach (var s in _segs)
                {
                    var have = Math.Max(0, s.Off.Count - (s.Done ? 0 : 1)) / (double)Fps;
                    t = s.Start + Math.Min(have, s.Len);
                    if (!s.Done) break;
                }
                return t;
            }
        }
    }
    /// <summary>Fração total gerada (0..1) — para a barra de estado.</summary>
    public double Fraction
    {
        get
        {
            lock (_lock)
            {
                double have = 0, total = 0;
                foreach (var s in _segs) { total += s.Len; have += s.Done ? s.Len : Math.Min(s.Len, Math.Max(0, s.Off.Count - 1) / (double)Fps); }
                return total <= 0 ? 0 : have / total;
            }
        }
    }

    private ScrubCache(string basePath, double duration)
    {
        _base = basePath;
        // com GPU a descodificar, o gargalo é o encode MJPEG (1 thread) → 1 processo por core
        var n = Math.Clamp(Environment.ProcessorCount - 1, 2, 10);
        if (duration < 20) n = 1; else if (duration < 60) n = Math.Min(n, 3);
        _segs = new Seg[n];
        var len = duration / n;
        for (var i = 0; i < n; i++) _segs[i] = new Seg { Start = i * len, Len = i == n - 1 ? duration - i * len : len, Path = $"{basePath}.s{i}.mjpg" };
    }

    private ScrubCache(Seg[] segs) { _base = ""; _segs = segs; Complete = true; Hw = ""; }

    /// <summary>
    /// v1.84: cache de um ficheiro JUNTO composta pelas caches (completas) dos clips que o formam —
    /// zero ffmpeg: só reabre os .mjpg já feitos com o tempo deslocado. Fecha caches parciais.
    /// </summary>
    public static ScrubCache? Compose(IList<(ScrubCache cache, double offset)> parts)
    {
        var segs = new List<Seg>();
        try
        {
            foreach (var (c, off) in parts)
            {
                if (!c.Complete) return null;
                foreach (var s in c._segs)
                {
                    if (!s.Done || s.Off.Count == 0) return null;
                    var n = new Seg { Start = s.Start + off, Len = s.Len, Path = s.Path, End = s.End, Done = true };
                    n.Off.AddRange(s.Off);
                    n.Reader = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
                    segs.Add(n);
                }
            }
            return new ScrubCache(segs.ToArray());
        }
        catch { foreach (var s in segs) { try { s.Reader?.Dispose(); } catch { } } return null; }
    }

    public static string PathFor(string input) => Path.ChangeExtension(VideoEditor.ProxyPathFor(input), null);

    public static ScrubCache OpenOrBuild(string source, string basePath, double duration, CancellationToken ct)
    {
        var c = new ScrubCache(basePath, duration);
        if (c.TryOpenComplete()) return c;
        _ = c.BuildAsync(source, ct);
        return c;
    }

    private bool TryOpenComplete()
    {
        try
        {
            if (!File.Exists(_base + ".ok")) return false;
            var n = int.Parse(File.ReadAllText(_base + ".ok").Trim());
            if (n != _segs.Length) return false;
            foreach (var s in _segs)
            {
                var idx = s.Path + ".idx";
                if (!File.Exists(s.Path) || !File.Exists(idx)) return false;
                using var r = new BinaryReader(File.OpenRead(idx));
                var k = r.ReadInt32(); if (k < 2) return false;
                for (var i = 0; i < k - 1; i++) s.Off.Add(r.ReadInt64()); s.End = r.ReadInt64();
                s.Reader = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
                s.Done = true;
            }
            Complete = true;
            return true;
        }
        catch { foreach (var s in _segs) { s.Off.Clear(); s.Reader?.Dispose(); s.Reader = null; s.Done = false; } return false; }
    }

    private async Task BuildAsync(string source, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_base)!);
        try { File.Delete(_base + ".ok"); } catch { }
        // 1º segmento com GPU; se falhar, todos em CPU (evita N tentativas GPU falhadas)
        var hw = await ProbeHwAsync(source, ct);
        Hw = hw;
        var keyTask = BuildKeyframesAsync(source, hw, ct);
        // v1.87: modo leve — só keyframes (abre em segundos); o detalhe 60fps + proxy só se já existirem em disco
        if (KeysOnly) { await keyTask; if (!ct.IsCancellationRequested) { Light = true; Progress?.Invoke(); } return; }
        var tasks = _segs.Select(s => RunSegAsync(source, s, hw, ct)).ToArray();
        await keyTask;
        await Task.WhenAll(tasks);
        if (ct.IsCancellationRequested) return;
        if (_segs.Any(s => s.Failed)) { Failed = true; Progress?.Invoke(); return; }
        try
        {
            foreach (var s in _segs)
            {
                long[] off; long end; lock (_lock) { off = s.Off.ToArray(); end = s.End; }
                await using var w = new BinaryWriter(File.Create(s.Path + ".idx"));
                w.Write(off.Length + 1); foreach (var o in off) w.Write(o); w.Write(end);
            }
            File.WriteAllText(_base + ".ok", _segs.Length.ToString());
        }
        catch { }
        Complete = true;
        Progress?.Invoke();
    }

    // cuda (NVIDIA, mais rápido) → d3d11va (qualquer GPU) → CPU. Só conta o exit code
    // (avisos no stderr são normais — a versão anterior exigia stderr vazio e caía sempre em CPU).
    /// <summary>
    /// Keyframes do clip todo (-skip_frame nokey: o descodificador nem toca nos outros frames),
    /// com o tempo de cada um via showinfo no stderr. 10 min de clip ≈ 300 frames ≈ segundos.
    /// </summary>
    private async Task BuildKeyframesAsync(string source, string hw, CancellationToken ct)
    {
        try
        {
            var hwa = hw.Length > 0 ? $"-hwaccel {hw} " : "";
            var psi = new ProcessStartInfo(FfmpegManager.ExePath,
                $"-hide_banner -loglevel info -nostdin {hwa}-skip_frame nokey -i \"{source}\" -an -sn -vsync passthrough -vf \"scale=-2:'min(ih,{KeyHeight})':flags=lanczos,showinfo\" -c:v mjpeg -q:v 2 -f mjpeg pipe:1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var times = new List<double>();
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                {
                    var k = line.IndexOf("pts_time:", StringComparison.Ordinal);
                    if (k < 0) continue;
                    var sp = line.IndexOf(' ', k + 9); var txt = sp > 0 ? line[(k + 9)..sp] : line[(k + 9)..];
                    if (double.TryParse(txt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) lock (times) times.Add(t);
                }
            });
            var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await p.WaitForExitAsync(CancellationToken.None);
            await errTask;
            var data = ms.GetBuffer(); var len = (int)ms.Length;
            var soi = new List<int>();
            for (var i = 0; i < len - 2; i++) if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF) soi.Add(i);
            lock (_lock)
            {
                _keys.Clear();
                for (var i = 0; i < soi.Count; i++)
                {
                    var a = soi[i]; var b = i + 1 < soi.Count ? soi[i + 1] : len;
                    var t = i < times.Count ? times[i] : (i * (_segs.Sum(x => x.Len) / Math.Max(1, soi.Count)));
                    _keys.Add((t, data.AsSpan(a, b - a).ToArray()));
                }
                KeyframesReady = _keys.Count > 0;
            }
            Progress?.Invoke();
        }
        catch (Exception) { }
    }

    private static async Task<string> ProbeHwAsync(string source, CancellationToken ct)
    {
        foreach (var hw in new[] { "cuda", "d3d11va" })
        {
            try
            {
                var psi = new ProcessStartInfo(FfmpegManager.ExePath, $"-hide_banner -loglevel error -nostdin -hwaccel {hw} -ss 0 -t 0.3 -i \"{source}\" -an -sn -vf \"scale=-2:360\" -f null -")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!;
                var err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && !err.Contains("Failed", StringComparison.OrdinalIgnoreCase) && !err.Contains("Error", StringComparison.OrdinalIgnoreCase)) return hw;
            }
            catch { }
        }
        return "";
    }

    private async Task RunSegAsync(string source, Seg seg, string hw, CancellationToken ct)
    {
        var tmp = seg.Path + ".part";
        var ok = await RunOnce(source, seg, tmp, hw, ct);
        if (!ok && hw.Length > 0 && !ct.IsCancellationRequested) { lock (_lock) { seg.Off.Clear(); seg.End = 0; } ok = await RunOnce(source, seg, tmp, "", ct); }
        if (!ok) { seg.Failed = true; try { File.Delete(tmp); } catch { } return; }
        try
        {
            seg.Reader?.Dispose(); seg.Reader = null;
            File.Move(tmp, seg.Path, true);
            seg.Reader = new FileStream(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        }
        catch { seg.Failed = true; return; }
        lock (_lock) seg.Done = true;
        Progress?.Invoke();
    }

    private async Task<bool> RunOnce(string source, Seg seg, string tmp, string hw, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var hwa = hw.Length > 0 ? $"-hwaccel {hw} " : "";
        // -ss antes do -i = salto rápido ao keyframe; o fps= depois garante a grelha certa
        var psi = new ProcessStartInfo(FfmpegManager.ExePath,
            $"-hide_banner -loglevel error -nostdin {hwa}-threads 2 -ss {seg.Start.ToString("0.###", inv)} -t {seg.Len.ToString("0.###", inv)} -i \"{source}\" -an -sn -vf \"fps={Fps},scale=-2:'min(ih,{Height})':flags=fast_bilinear\" -c:v mjpeg -q:v 4 -threads 2 -f mjpeg pipe:1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        Process p;
        try { p = Process.Start(psi)!; } catch { return false; }
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        try
        {
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 20);
            seg.Reader?.Dispose();
            seg.Reader = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
            var buf = new byte[1 << 20]; var carry = new byte[2]; var carryLen = 0; long pos = 0; int n;
            var last = DateTime.UtcNow;
            var src = p.StandardOutput.BaseStream;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                for (var i = -carryLen; i < n - 2; i++)
                {
                    byte b0 = i < 0 ? carry[carryLen + i] : buf[i];
                    byte b1 = i + 1 < 0 ? carry[carryLen + i + 1] : buf[i + 1];
                    if (b0 == 0xFF && b1 == 0xD8 && buf[i + 2] == 0xFF) lock (_lock) seg.Off.Add(pos + i);
                }
                carryLen = Math.Min(2, n); Array.Copy(buf, n - carryLen, carry, 0, carryLen);
                pos += n;
                lock (_lock) seg.End = pos;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { last = DateTime.UtcNow; Progress?.Invoke(); }
            }
            await fs.FlushAsync(ct);
            await p.WaitForExitAsync(CancellationToken.None);
            lock (_lock) return !ct.IsCancellationRequested && seg.Off.Count >= 1 && p.ExitCode == 0;
        }
        catch { return false; }
        finally { try { p.Dispose(); } catch { } }
    }

    /// <summary>JPEG do frame mais próximo de t; se essa zona ainda não está na cache completa, o keyframe anterior (fase 1); null se nada.</summary>
    public byte[]? Get(double t)
    {
        long a, b; FileStream? r;
        lock (_lock)
        {
            Seg? seg = null;
            foreach (var s in _segs) { if (t < s.Start + s.Len || ReferenceEquals(s, _segs[^1])) { seg = s; break; } }
            var ok = seg != null && seg.Off.Count > 0;
            var i = ok ? Math.Max(0, (int)Math.Round((t - seg!.Start) * Fps)) : 0;
            if (ok && seg!.Done) i = Math.Min(i, seg.Off.Count - 1);
            else if (ok && i >= seg!.Off.Count - 1) ok = false;
            if (!ok) return NearestKey(t);
            a = seg!.Off[i]; b = i + 1 < seg.Off.Count ? seg.Off[i + 1] : seg.End;
            r = seg.Reader;
        }
        var len = (int)(b - a);
        if (r == null || len <= 0 || len > 8_000_000) return null;
        var bytes = new byte[len];
        lock (r) { r.Position = a; var got = 0; while (got < len) { var k = r.Read(bytes, got, len - got); if (k <= 0) return null; got += k; } }
        return bytes;
    }

    /// <summary>v1.88.1 (estilo Avidemux): tempo a usar durante o arrasto. Onde a cache completa ainda
    /// não chegou (fase keyframes), devolve o tempo EXATO do keyframe que Get(t) mostraria — assim a
    /// posição do cursor e o frame no ecrã são o MESMO frame, e ao largar o player aterra nesse
    /// keyframe (seek a sync point = exato) em vez de descodificar um frame diferente uns ms depois.
    /// Com cache completa devolve t (frame exato já disponível).</summary>
    public double SnapForScrub(double t)
    {
        lock (_lock)
        {
            Seg? seg = null;
            foreach (var s in _segs) { if (t < s.Start + s.Len || ReferenceEquals(s, _segs[^1])) { seg = s; break; } }
            var ok = seg != null && seg.Off.Count > 0;
            var i = ok ? Math.Max(0, (int)Math.Round((t - seg!.Start) * Fps)) : 0;
            if (ok && seg!.Done) i = Math.Min(i, seg.Off.Count - 1);
            else if (ok && i >= seg!.Off.Count - 1) ok = false;
            if (ok || _keys.Count == 0) return t;
            int lo = 0, hi = _keys.Count - 1;
            while (lo < hi) { var mid = (lo + hi + 1) / 2; if (_keys[mid].t <= t) lo = mid; else hi = mid - 1; }
            return _keys[lo].t;
        }
    }

    // keyframe anterior ou igual a t (lista ordenada por tempo)
    private byte[]? NearestKey(double t)
    {
        if (_keys.Count == 0) return null;
        int lo = 0, hi = _keys.Count - 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (_keys[mid].t <= t) lo = mid; else hi = mid - 1; }
        return _keys[lo].jpg;
    }

    /// <summary>v1.86: segmentos MJPEG completos (caminho, início, duração) — o proxy 720p é codificado a partir daqui, sem voltar a descodificar o original.</summary>
    public IReadOnlyList<(string path, double start, double len)> SegmentFiles
    {
        get
        {
            lock (_lock)
            {
                if (!Complete || _segs.Any(s => !s.Done || s.Failed || string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path))) return Array.Empty<(string, double, double)>();
                return _segs.Select(s => (s.Path, s.Start, s.Len)).ToList();
            }
        }
    }

    public void Dispose() { foreach (var s in _segs) { try { s.Reader?.Dispose(); } catch { } } }
}
