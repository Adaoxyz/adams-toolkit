using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AdamsToolkit.Core;

public class DownloadHistoryEntry
{
    public string AppName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime Date { get; set; }
    public string Status { get; set; } = ""; // Concluído / Falhou / Cancelado
}

/// <summary>
/// Downloads sempre a partir dos links OFICIAIS definidos na config remota.
/// Nada é alojado pela app — apenas gere a transferência e arranca o instalador.
/// </summary>
public static class DownloadManager
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AdamsToolkit/1.0");
        return c;
    }

    public static string DownloadDir
    {
        get
        {
            var dir = Path.Combine(ConfigService.DataDir, "Downloads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string HistoryPath => Path.Combine(ConfigService.DataDir, "history.json");

    public static List<DownloadHistoryEntry> History { get; private set; } = LoadHistory();

    private static List<DownloadHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(
                    File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveHistory()
    {
        try
        {
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(
                History.TakeLast(100).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void AddHistory(AppEntry app, string file, string status)
    {
        History.Add(new DownloadHistoryEntry
        {
            AppName = app.Name,
            FileName = file,
            Url = app.DownloadUrl,
            Date = DateTime.Now,
            Status = status
        });
        SaveHistory();
    }

    /// <summary>Transfere o instalador oficial com progresso; devolve o caminho local ou null.</summary>
    public static async Task<string?> DownloadAsync(
        AppEntry app, IProgress<(double pct, string label)> progress, CancellationToken ct)
    {
        var fileName = string.IsNullOrEmpty(app.FileName)
            ? Path.GetFileName(new Uri(app.DownloadUrl).LocalPath)
            : app.FileName;
        if (string.IsNullOrWhiteSpace(fileName)) fileName = app.Id + "-setup.exe";
        var dest = Path.Combine(DownloadDir, fileName);

        try
        {
            using var res = await Http.GetAsync(app.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();

            var total = res.Content.Headers.ContentLength ?? -1L;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                    progress.Report((read * 100.0 / total,
                        $"{read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"));
                else
                    progress.Report((-1, $"{read / 1048576.0:0.0} MB"));
            }

            AddHistory(app, fileName, "Concluído");
            return dest;
        }
        catch (OperationCanceledException)
        {
            AddHistory(app, fileName, "Cancelado");
            try { File.Delete(dest); } catch { }
            return null;
        }
        catch
        {
            AddHistory(app, fileName, "Falhou");
            try { File.Delete(dest); } catch { }
            return null;
        }
    }

    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
