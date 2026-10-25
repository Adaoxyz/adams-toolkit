using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace AdamsToolkit.Core;

/// <summary>
/// Garante ffmpeg.exe em %LOCALAPPDATA%\AdamsToolkit\editor\ffmpeg\ (reaproveita o do Adams Clips se existir). Não vem embebido no exe
/// (145MB) — é descarregado uma vez do nosso servidor (zip 56MB, build BtbN gpl
/// com ddagrab + nvenc/amf/qsv/x264) e verificado por sha256.
/// </summary>
public static class FfmpegManager
{
    // v1.79: build shared 7.1 (DLLs p/ o leitor FFME + ffmpeg.exe)
    public const string ZipPath = "/files/ffmpeg7-win64.zip";
    public static string ZipUrl => Endpoints.Url(ZipPath);
    public const string ZipSha256 = "4acb8e2bfa52f6906c73bce33b0b10bfd26a3299a9fe33dc0e13aa41158f3e5d";

    public static string Dir => Path.Combine(EditorPaths.LocalDir, "ffmpeg7");
    public static string ExePath => Path.Combine(Dir, "ffmpeg.exe");
    /// <summary>ffmpeg.exe + DLLs (avcodec-61 é a maior; sem ela nem o exe arranca).</summary>
    public static bool IsReady
    {
        get
        {
            try { return File.Exists(ExePath) && File.Exists(Path.Combine(Dir, "avcodec-61.dll")) && new FileInfo(Path.Combine(Dir, "avcodec-61.dll")).Length > 50_000_000; }
            catch { return false; }
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static async Task EnsureAsync(IProgress<(double pct, string msg)> progress, CancellationToken ct = default)
    {
        if (IsReady) return;
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Dir, "ffmpeg.zip.part");

        progress.Report((0, "A descarregar o motor de vídeo (60 MB)…"));
        using (var resp = await Endpoints.GetFirstOkAsync(Http, Endpoints.All(ZipPath), ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long done = 0; int n;
            var last = DateTime.UtcNow;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
                {
                    last = DateTime.UtcNow;
                    var pct = total > 0 ? done * 100.0 / total : 0;
                    progress.Report((pct, $"A descarregar o motor de vídeo… {done / 1048576} / {(total > 0 ? total / 1048576 : 60)} MB"));
                }
            }
        }

        progress.Report((100, "A verificar…"));
        await using (var fs = File.OpenRead(tmp))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("Download do FFmpeg corrompido (sha256 inválido). Tenta outra vez.");
            }
        }

        progress.Report((100, "A extrair…"));
        using (var zip = ZipFile.OpenRead(tmp))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(Path.Combine(Dir, entry.Name), true);
            }
        }
        File.Delete(tmp);
        progress.Report((100, "FFmpeg pronto."));
    }


    /// <summary>
    /// Escapa um valor de opção para dentro de um filtergraph do ffmpeg. O ffmpeg faz DOIS
    /// níveis de parse: o graph (separadores , ; [ ] e aspas) e depois as opções do filtro
    /// (separador :). Um só "\:" era desfeito no 1º nível e o 2º partia o caminho no ':'
    /// do disco ("m=C" + "/Users/...") -> "Error parsing a filter description" (bug v1.8-1.11:
    /// o RNNoise nunca chegou a correr). Agora: 1º nível p/ ':' '\' ''' e 2º nível p/ os do
    /// graph -> "C\\:/Users/...". Também aguenta usernames com ' [ ] , ;.
    /// </summary>
    public static string EscapeFilterValue(string v)
    {
        var l1 = new System.Text.StringBuilder();
        foreach (var c in v) { if (c is '\\' or ':' or '\'') l1.Append('\\'); l1.Append(c); }
        var l2 = new System.Text.StringBuilder();
        foreach (var c in l1.ToString()) { if (c is '\\' or '\'' or '[' or ']' or ',' or ';') l2.Append('\\'); l2.Append(c); }
        return l2.ToString();
    }


    /// <summary>Lista encoders compilados (nome → true) para pré-filtrar candidatos.</summary>
    public static async Task<HashSet<string>> ListEncodersAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo(ExePath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var txt = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in txt.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V') set.Add(parts[1]);
            }
        }
        catch { }
        return set;
    }
}
