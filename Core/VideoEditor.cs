using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AdamsToolkit.Core;

/// <summary>
/// Motor do editor (estilo Avidemux) sobre ffmpeg: lista de intervalos mantidos,
/// exportação por cópia (corte em keyframes, instantâneo) ou recodificação (precisa),
/// junção de vários ficheiros, frame → PNG.
/// </summary>
public static class VideoEditor
{
    public sealed record Info(double Duration, int Width, int Height, double Fps, string VideoCodec, string AudioCodec);
    public sealed record Range(double Start, double End) { public double Length => End - Start; }

    public sealed class ExportOptions
    {
        public bool Reencode;                  // false = -c copy (keyframes)
        public string Container = "mp4";       // mp4 | mkv | gif
        public int? Height;                    // null = igual
        public bool Mute;
        public float Volume = 1f;              // só com Reencode
        public bool UseNvenc;                  // só com Reencode
        public int Crf = 18;                   // qualidade recodificação
        public double Speed = 1.0;             // 0.25..4 (só Reencode)
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.###", Inv);

    // ------------------------------------------------------------------ probe

    /// <summary>v1.85: probe em cache (proxy\&lt;hash&gt;.info) — 2ª abertura não lança ffmpeg.</summary>
    public static async Task<Info?> ProbeAsync(string path)
    {
        string? infoPath = null;
        try
        {
            infoPath = Path.ChangeExtension(ProxyPathFor(path), ".info");
            if (File.Exists(infoPath))
            {
                var f = (await File.ReadAllTextAsync(infoPath)).Split('|');
                if (f.Length == 6) return new Info(double.Parse(f[0], Inv), int.Parse(f[1]), int.Parse(f[2]), double.Parse(f[3], Inv), f[4], f[5]);
            }
        }
        catch { }
        var info = await ProbeRawAsync(path);
        if (info != null && infoPath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
                await File.WriteAllTextAsync(infoPath, string.Join('|', info.Duration.ToString(Inv), info.Width, info.Height, info.Fps.ToString(Inv), info.VideoCodec, info.AudioCodec));
            }
            catch { }
        }
        return info;
    }
    private static async Task<Info?> ProbeRawAsync(string path)
    {
        var txt = await RunAsync($"-hide_banner -i \"{path}\"", null, null);
        var dur = Regex.Match(txt, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!dur.Success) return null;
        var d = int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 + double.Parse(dur.Groups[3].Value, Inv);
        var v = Regex.Match(txt, @"Video:\s*(\w+).*?\s(\d{2,5})x(\d{2,5})[,\s].*?(\d+(?:\.\d+)?)\s*fps");
        var a = Regex.Match(txt, @"Audio:\s*(\w+)");
        return new Info(d,
            v.Success ? int.Parse(v.Groups[2].Value) : 0, v.Success ? int.Parse(v.Groups[3].Value) : 0,
            v.Success ? double.Parse(v.Groups[4].Value, Inv) : 60,
            v.Success ? v.Groups[1].Value : "?", a.Success ? a.Groups[1].Value : "—");
    }

    // ----------------------------------------------------------------- ranges

    /// <summary>Remove [a,b] dos intervalos mantidos.</summary>
    public static List<Range> Cut(List<Range> ranges, double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        var res = new List<Range>();
        foreach (var r in ranges)
        {
            if (b <= r.Start || a >= r.End) { res.Add(r); continue; }
            if (a > r.Start) res.Add(new Range(r.Start, a));
            if (b < r.End) res.Add(new Range(b, r.End));
        }
        return res.Where(r => r.Length > 0.05).ToList();
    }

    /// <summary>Mantém só a interseção com [a,b].</summary>
    public static List<Range> Keep(List<Range> ranges, double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        return ranges.Select(r => new Range(Math.Max(r.Start, a), Math.Min(r.End, b))).Where(r => r.Length > 0.05).ToList();
    }

    // ----------------------------------------------------------------- export

    /// <summary>Exporta os intervalos (por ordem) para outPath. progress 0..1.</summary>
    public static async Task<string?> ExportAsync(string input, List<Range> ranges, ExportOptions o, string outPath,
        IProgress<double> progress, Action<string> log, CancellationToken ct)
    {
        if (ranges.Count == 0) return "sem intervalos";
        var tmpDir = Path.Combine(EditorPaths.LocalDir, "edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var total = ranges.Sum(r => r.Length) / Math.Max(0.1, o.Reencode ? o.Speed : 1);
            double done = 0;
            var parts = new List<string>();

            if (o.Container == "gif")
            {
                // GIF: sempre recodifica, junta tudo num filtro só (select por intervalos → concat)
                var sel = string.Join("+", ranges.Select(r => $"between(t\\,{F(r.Start)}\\,{F(r.End)})"));
                var h = o.Height ?? 480;
                var vf = $"select='{sel}',setpts=N/FRAME_RATE/TB,fps=20,scale=-2:{h}:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer";
                var err = await RunAsync($"-hide_banner -loglevel error -y -progress pipe:2 -i \"{input}\" -filter_complex \"{vf}\" -an \"{outPath}\"",
                    us => progress.Report(Math.Min(1, us / total)), log, ct);
                return File.Exists(outPath) ? null : (err.Length > 0 ? err : "falhou");
            }

            for (var i = 0; i < ranges.Count; i++)
            {
                var r = ranges[i];
                var part = Path.Combine(tmpDir, $"p{i:000}.{(o.Reencode ? "mkv" : "ts")}");
                var sb = new StringBuilder("-hide_banner -loglevel error -y -progress pipe:2 ");
                if (o.Reencode)
                {
                    sb.Append($"-ss {F(r.Start)} -i \"{input}\" -t {F(r.Length)} ");
                    var vf = new List<string>();
                    if (o.Height is { } hh) vf.Add($"scale=-2:{hh}:flags=lanczos");
                    if (Math.Abs(o.Speed - 1) > 0.01) vf.Add($"setpts=PTS/{F(o.Speed)}");
                    if (vf.Count > 0) sb.Append($"-vf \"{string.Join(",", vf)}\" ");
                    // qualidade máxima por defeito: NVENC p6 + AQ / x264 medium, alto bitrate permitido
                    sb.Append(o.UseNvenc
                        ? $"-c:v h264_nvenc -preset p6 -tune hq -rc vbr -cq {o.Crf} -b:v 0 -spatial-aq 1 -temporal-aq 1 -rc-lookahead 16 -bf 2 -profile:v high -pix_fmt yuv420p "
                        : $"-c:v libx264 -preset medium -crf {o.Crf} -profile:v high -pix_fmt yuv420p ");
                    if (o.Mute) sb.Append("-an ");
                    else
                    {
                        var af = new List<string>();
                        if (Math.Abs(o.Volume - 1) > 0.01) af.Add($"volume={F(o.Volume)}");
                        if (Math.Abs(o.Speed - 1) > 0.01) af.Add(Atempo(o.Speed));
                        if (af.Count > 0) sb.Append($"-af \"{string.Join(",", af)}\" ");
                        sb.Append("-c:a aac -b:a 256k ");
                    }
                }
                else
                {
                    // cópia: -ss antes do -i salta para o keyframe anterior; corte ~ exato ao keyframe
                    sb.Append($"-ss {F(r.Start)} -i \"{input}\" -t {F(r.Length)} -map 0 -c copy -avoid_negative_ts make_zero ");
                    if (o.Mute) sb.Append("-an ");
                    sb.Append("-f mpegts ");
                }
                sb.Append('"').Append(part).Append('"');
                var d0 = done; var len = r.Length / (o.Reencode ? o.Speed : 1);
                var err = await RunAsync(sb.ToString(), us => progress.Report(Math.Min(1, (d0 + Math.Min(us, len)) / total)), log, ct);
                if (!File.Exists(part)) return err.Length > 0 ? err : $"falhou no intervalo {i + 1}";
                done += len; parts.Add(part);
            }

            var mux = o.Container == "mkv" ? "-f matroska" : "-movflags +faststart";
            if (parts.Count == 1 && o.Reencode)
            {
                var err = await RunAsync($"-hide_banner -loglevel error -y -i \"{parts[0]}\" -map 0 -c copy {mux} \"{outPath}\"", null, log, ct);
                return File.Exists(outPath) ? null : err;
            }
            var list = Path.Combine(tmpDir, "list.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            var e2 = await RunAsync($"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{list}\" -map 0 -c copy {mux} \"{outPath}\"", null, log, ct);
            progress.Report(1);
            return File.Exists(outPath) ? null : (e2.Length > 0 ? e2 : "junção falhou");
        }
        catch (OperationCanceledException) { return "cancelado"; }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    private static string Atempo(double speed)
    {
        // atempo aceita 0.5..2 por instância — encadeia
        var parts = new List<string>();
        while (speed > 2) { parts.Add("atempo=2"); speed /= 2; }
        while (speed < 0.5) { parts.Add("atempo=0.5"); speed /= 0.5; }
        parts.Add($"atempo={F(speed)}");
        return string.Join(",", parts);
    }

    /// <summary>Caminho do .ts (remux por cópia) cacheado de um clip — reutilizado em todas as junções.</summary>
    public static string JoinPartPathFor(string input)
    {
        var fi = new FileInfo(input);
        var key = $"ts1|{input.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(EditorPaths.LocalDir, "proxy", hash + ".join.ts");
    }

    /// <summary>
    /// Junta vários ficheiros (mesmo codec) por cópia. v1.84: o remux .ts de cada clip é
    /// cacheado (1 vez por clip, em paralelo, prioridade baixa) — juntar mais um clip a 5 já
    /// juntos só remuxa o novo + 1 concat por cópia, em vez de reler tudo em série.
    /// </summary>
    public static async Task<string?> JoinAsync(IList<string> inputs, string outPath, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(EditorPaths.LocalDir, "proxy"));
        var parts = new string[inputs.Count];
        string? fail = null;
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        await Task.WhenAll(inputs.Select(async (input, i) =>
        {
            var part = input.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? input : JoinPartPathFor(input);
            parts[i] = part;
            if (File.Exists(part) && new FileInfo(part).Length > 1000) return;
            await gate.WaitAsync(ct);
            try
            {
                var tmp = part + ".part";
                await RunAsync($"-hide_banner -loglevel error -y -i \"{input}\" -map 0 -c copy -f mpegts \"{tmp}\"", null, log, ct, background: true);
                if (!File.Exists(tmp) || new FileInfo(tmp).Length < 1000) { fail ??= $"não deu para ler {Path.GetFileName(input)}"; try { File.Delete(tmp); } catch { } return; }
                File.Move(tmp, part, true);
            }
            finally { gate.Release(); }
        }));
        if (fail != null) return fail;
        var list = Path.Combine(EditorPaths.LocalDir, $"join-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            var mux = outPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "-f matroska" : "-movflags +faststart";
            var err = await RunAsync($"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{list}\" -map 0 -c copy {mux} \"{outPath}\"", null, log, ct);
            return File.Exists(outPath) ? null : err;
        }
        finally { try { File.Delete(list); } catch { } }
    }

    /// <summary>Concat por cópia de proxies já feitos (mesmos parâmetros → junção invisível).</summary>
    public static async Task<bool> ConcatCopyAsync(IList<string> parts, string outPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var list = outPath + ".list.txt"; var tmpOut = outPath + ".part.mp4";
        try
        {
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            await RunAsync($"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{list}\" -map 0 -c copy -movflags +faststart \"{tmpOut}\"", null, null, ct, background: true);
            if (!File.Exists(tmpOut) || new FileInfo(tmpOut).Length < 1000) return false;
            File.Move(tmpOut, outPath, true);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { try { File.Delete(list); } catch { } try { File.Delete(tmpOut); } catch { } }
    }

    public static async Task<bool> FrameToPngAsync(string input, double t, string outPng)
    {
        await RunAsync($"-hide_banner -loglevel error -y -ss {F(t)} -i \"{input}\" -frames:v 1 \"{outPng}\"", null, null);
        return File.Exists(outPng);
    }

    /// <summary>Tira de n miniaturas (tile n×1, altura h) — 1 png. Rápido: só keyframes-ish via -skip_frame nokey? Não: fps exato p/ tempos certos.</summary>
    public static async Task<bool> FilmstripAsync(string input, double duration, int n, int h, string outPng, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        var step = Math.Max(0.05, duration / n);
        // select por tempo evita descodificar tudo a fps cheio; -skip_frame nokey acelera muito em vídeos longos
        var vf = $"fps=1/{F(step)},scale=-2:{h}:flags=fast_bilinear,tile={n}x1";
        await RunAsync($"-hide_banner -loglevel error -y -skip_frame nokey -i \"{input}\" -vf \"{vf}\" -frames:v 1 \"{outPng}\"", null, null, ct, background: true);
        if (!File.Exists(outPng))
            await RunAsync($"-hide_banner -loglevel error -y -i \"{input}\" -vf \"{vf}\" -frames:v 1 \"{outPng}\"", null, null, ct, background: true);
        return File.Exists(outPng);
    }

    /// <summary>
    /// Proxy de pré-visualização: 540p, TODOS os frames keyframe (-g 1), sem B-frames → o
    /// MediaElement salta para qualquer frame na hora (o original em 1080p60 com GOP de 2 s
    /// obriga a descodificar até 120 frames por seek = "aos cortes"). Só p/ ver; a exportação
    /// usa sempre o original. Cache por (caminho, tamanho, mtime) em editor\proxy\.
    /// </summary>
    public static string ProxyPathFor(string input)
    {
        var fi = new FileInfo(input);
        var key = $"v7|{input.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; // v7 = qualidade Avidemux (v1.88): sem proxy antigo 720p, keyframes nativos
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(EditorPaths.LocalDir, "proxy", hash + ".mp4");
    }

    /// <summary>
    /// v1.81: proxy de reprodução a 720p/60 (mesma qualidade da cache de arrasto → a troca não se
    /// nota), gerado em PARALELO por segmentos (.ts) e juntado por cópia. NVENC se existir, senão
    /// x264 veryfast; falha por segmento cai para x264.
    /// </summary>
    public static async Task<bool> BuildProxyAsync(string input, string outPath, bool useNvenc, IProgress<double>? progress, double duration, CancellationToken ct,
        IReadOnlyList<(string path, double start, double len)>? fromCache = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        // v1.86: se a cache de arrasto (MJPEG 720p/60 já descodificado) existir, o proxy sai dela:
        // decode MJPEG é barato e não há scale → poupa a 2ª passagem completa sobre o original.
        var useCache = fromCache is { Count: > 0 };
        var n = useCache ? fromCache!.Count : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        if (!useCache) { if (duration < 20) n = 1; else if (duration < 60) n = Math.Min(n, 3); }
        var len = duration / n;
        var tmpDir = outPath + ".parts"; Directory.CreateDirectory(tmpDir);
        var done = new double[n];
        try
        {
            var tasks = Enumerable.Range(0, n).Select(async i =>
            {
                var start = useCache ? fromCache![i].start : i * len; var l = useCache ? fromCache![i].len : (i == n - 1 ? duration - start : len);
                var part = Path.Combine(tmpDir, $"p{i:00}.ts");
                async Task<bool> Run(bool nv)
                {
                    var vcodec = nv
                        ? "-c:v h264_nvenc -preset p2 -tune hq -rc constqp -qp 22 -g 30 -bf 0 -profile:v high"
                        : "-c:v libx264 -preset veryfast -tune fastdecode -crf 21 -g 30 -bf 0 -profile:v high -threads 2";
                    var args = useCache
                        ? $"-hide_banner -loglevel error -y -progress pipe:2 -threads 2 -framerate {ScrubCache.Fps} -f mjpeg -i \"{fromCache![i].path}\" -ss {F(start)} -t {F(l)} -i \"{input}\" -map 0:v:0 -map 1:a:0? -shortest {vcodec} -pix_fmt yuv420p -c:a aac -b:a 160k -ac 2 -f mpegts \"{part}\""
                        : $"-hide_banner -loglevel error -y -progress pipe:2 -threads 2 -ss {F(start)} -t {F(l)} -i \"{input}\" -vf \"fps=60,scale=-2:'min(ih,720)':flags=bicubic\" {vcodec} -pix_fmt yuv420p -c:a aac -b:a 160k -ac 2 -f mpegts \"{part}\"";
                    await RunAsync(args, us => { done[i] = Math.Min(us, l); progress?.Report(Math.Min(1, done.Sum() / Math.Max(0.1, duration))); }, null, ct, background: true);
                    return File.Exists(part) && new FileInfo(part).Length > 1000;
                }
                var ok = await Run(useNvenc);
                if (!ok && useNvenc && !ct.IsCancellationRequested) ok = await Run(false);
                return ok ? part : null;
            }).ToArray();
            var parts = await Task.WhenAll(tasks);
            if (ct.IsCancellationRequested || parts.Any(p => p == null)) return false;
            var list = Path.Combine(tmpDir, "list.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p!.Replace("'", "'\\''")}'"), ct);
            var tmpOut = outPath + ".part.mp4";
            await RunAsync($"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{list}\" -map 0 -c copy -movflags +faststart \"{tmpOut}\"", null, null, ct, background: true);
            if (!File.Exists(tmpOut) || new FileInfo(tmpOut).Length < 1000) return false;
            File.Move(tmpOut, outPath, true);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    /// <summary>Apaga proxies com mais de 7 dias (ou tudo se passar de 3 GB).</summary>
    public static void CleanupProxies()
    {
        try
        {
            var dir = Path.Combine(EditorPaths.LocalDir, "proxy");
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles("*.*").OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-3) || total > 8L << 30) { total -= f.Length; f.Delete(); }
            }
        }
        catch { }
    }

    /// <summary>
    /// Frame exato em t → BGRA cru (w×h×4). Usado no arrasto da timeline: o MediaElement
    /// demora ~100 ms por seek; o ffmpeg num proxy all-intra devolve o frame em ~30-60 ms.
    /// </summary>
    public static async Task<byte[]?> GrabFrameAsync(string file, double t, int w, int h, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegManager.ExePath,
            $"-hide_banner -loglevel error -nostdin -ss {F(t)} -i \"{file}\" -frames:v 1 -an -sn -vf \"scale={w}:{h}:flags=lanczos\" -f rawvideo -pix_fmt bgra pipe:1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        try
        {
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            var need = w * h * 4; var buf = new byte[need]; var got = 0;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            while (got < need)
            {
                var n = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(got, need - got), ct);
                if (n <= 0) break; got += n;
            }
            await p.WaitForExitAsync(CancellationToken.None);
            return got == need ? buf : null;
        }
        catch { return null; }
    }

    /// <summary>ffmpeg genérico c/ progresso (p/ ScrubCache).</summary>
    public static async Task<bool> RunFfmpegAsync(string args, Action<double>? onTime, CancellationToken ct)
    {
        try { await RunAsync(args, onTime, null, ct, background: true); return true; } catch (OperationCanceledException) { return false; }
    }

    // ------------------------------------------------------------------- run

    /// <summary>Corre ffmpeg; devolve stderr (sem linhas de -progress). onTime recebe out_time em segundos.</summary>
    private static async Task<string> RunAsync(string args, Action<double>? onTime, Action<string>? log, CancellationToken ct = default, bool background = false)
    {
        var psi = new ProcessStartInfo(FfmpegManager.ExePath, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            var eq = e.Data.IndexOf('=');
            if (eq > 0 && eq < 20 && e.Data.AsSpan(0, eq).IndexOfAny(" :") < 0)
            {
                if (e.Data.StartsWith("out_time_us=") && long.TryParse(e.Data[12..], out var us)) onTime?.Invoke(us / 1_000_000.0);
                return;
            }
            lock (err) { err.AppendLine(e.Data); if (err.Length > 6000) err.Remove(0, 3000); }
            log?.Invoke(e.Data);
        };
        p.Start();
        // trabalho de fundo (proxy/cache/miniaturas) não pode engasgar a reprodução nem a UI
        if (background) { try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } }
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        lock (err) return err.ToString().Trim();
    }
}
