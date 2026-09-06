using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace AdamsToolkit.Core;

/// <summary>
/// Instala o pack ReShade do WestRP na pasta de plugins do FiveM
/// (FiveM Application Data). Substitui qualquer ReShade existente:
/// os ficheiros antigos saem e este fica no lugar.
/// </summary>
public static class ReshadeInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // localização real por PC via FiveMLocator (registry → default), nunca caminho fixo
    public static string? FiveMAppData => FiveMLocator.AppDataDir;

    public static string? PluginsDir =>
        FiveMAppData == null ? null : Path.Combine(FiveMAppData, "plugins");

    public static bool FiveMPresent => FiveMAppData != null;

    public static bool IsInstalled
    {
        get
        {
            var p = PluginsDir;
            return p != null &&
                   File.Exists(Path.Combine(p, "dxgi.dll")) &&
                   Directory.Exists(Path.Combine(p, "reshade-shaders"));
        }
    }

    public static async Task<(bool ok, string message)> InstallAsync(
        IProgress<(double pct, string label)> progress, CancellationToken ct)
    {
        var pluginsDir = PluginsDir;
        if (pluginsDir == null)
            return (false, "Pasta do FiveM não encontrada — instala e abre o FiveM primeiro.");

        var tmp = Path.Combine(ConfigService.DataDir, "reshade-westrp.zip");
        try
        {
            // download com progresso
            using (var res = await Http.GetAsync(ConfigService.Current.ReshadeZipUrl,
                       HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? -1L;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buffer = new byte[81920];
                long read = 0; int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                        progress.Report((read * 90.0 / total,
                            $"{read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"));
                }
            }

            progress.Report((92, "A remover ReShade antigo…"));
            Directory.CreateDirectory(pluginsDir);
            // "a outra sai e esta fica no lugar": limpa qualquer instalação anterior
            foreach (var f in new[] { "dxgi.dll", "d3d9.dll", "d3d11.dll", "opengl32.dll",
                                      "ReShade.ini", "ReShadePreset.ini", "dxgi.log", "ReShade.log" })
            {
                var p = Path.Combine(pluginsDir, f);
                if (File.Exists(p)) File.Delete(p);
            }
            var shaders = Path.Combine(pluginsDir, "reshade-shaders");
            if (Directory.Exists(shaders)) Directory.Delete(shaders, true);

            progress.Report((95, "A instalar…"));
            ZipFile.ExtractToDirectory(tmp, pluginsDir, overwriteFiles: true);

            progress.Report((100, "Concluído"));
            return (true, "ReShade instalado! Abre o FiveM — tecla Home abre o menu ReShade.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelado.");
        }
        catch (IOException)
        {
            return (false, "Não foi possível substituir os ficheiros — fecha o FiveM e tenta de novo.");
        }
        catch (Exception e)
        {
            return (false, $"Falhou: {e.Message}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
