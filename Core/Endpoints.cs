using System.Net.Http;

namespace AdamsToolkit.Core;

/// <summary>
/// Onde vivem os ficheiros da app (config, updates, ReShade, ffmpeg).
///
/// O host canónico é um DOMÍNIO. O antigo — `adams-&lt;ip&gt;.sslip.io` — tem o IP
/// dentro do próprio nome, por isso morre de cada vez que o servidor muda de
/// casa: aconteceu a 01-09-2026 e outra vez a 15-09-2026, e das duas vezes os
/// instaladores JÁ distribuídos ficaram sem forma de descarregar seja o que for
/// (o URL está cozido dentro do exe de quem o descarregou).
///
/// Daqui em diante há uma LISTA: tenta o domínio, cai para o host antigo. Mudar
/// de servidor passa a ser um registo de DNS, e um host morto deixa de ser um
/// instalador morto.
/// </summary>
public static class Endpoints
{
    /// <summary>Host canónico — muda-se o registo A, não se recompila nada.</summary>
    public const string Primary = "https://cdn.atl-pvp.com";

    /// <summary>Rede de segurança: host antigo (IP no nome), mantido a responder de propósito.</summary>
    public const string Legacy = "https://adams-176-111-109-91.sslip.io";

    public static readonly IReadOnlyList<string> Hosts = new[] { Primary, Legacy };

    /// <summary>Caminho (ex.: "/files/version.json") no host canónico.</summary>
    public static string Url(string path) => Primary + path;

    /// <summary>O mesmo caminho em todos os hosts, por ordem de preferência.</summary>
    public static IEnumerable<string> All(string path) => Hosts.Select(h => h + path);

    /// <summary>
    /// A mesma lista mas começando por <paramref name="preferred"/> (tipicamente o
    /// authApiBase que veio da configuração remota), sem repetidos.
    /// </summary>
    public static IEnumerable<string> All(string path, string? preferred)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var p = preferred.TrimEnd('/') + path;
            if (seen.Add(p)) yield return p;
        }
        foreach (var u in All(path))
            if (seen.Add(u)) yield return u;
    }

    /// <summary>
    /// Um URL que veio de fora (ex.: o "zip" do version.json) seguido do MESMO
    /// caminho nos nossos hosts — para o caso de quem escreveu o version.json ter
    /// apontado para um host que entretanto morreu.
    /// </summary>
    public static IEnumerable<string> WithFallbacks(string url)
    {
        yield return url;
        string path;
        try { path = new Uri(url).AbsolutePath; }
        catch { yield break; }
        foreach (var u in All(path))
            if (!u.Equals(url, StringComparison.OrdinalIgnoreCase)) yield return u;
    }

    /// <summary>
    /// Primeira resposta OK entre os candidatos (downloads com progresso, que
    /// precisam da HttpResponseMessage e não só dos bytes). Se nenhum host der
    /// 2xx mas algum responder, devolve essa resposta para o chamador poder
    /// mostrar o código; se nenhum responder, atira.
    /// </summary>
    public static async Task<HttpResponseMessage> GetFirstOkAsync(
        HttpClient http, IEnumerable<string> urls, CancellationToken ct = default)
    {
        HttpResponseMessage? last = null;
        Exception? lastEx = null;
        foreach (var url in urls)
        {
            try
            {
                var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (r.IsSuccessStatusCode) { last?.Dispose(); return r; }
                Log.Warn($"host devolveu {(int)r.StatusCode}: {url}");
                last?.Dispose();
                last = r;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastEx = ex; Log.Warn($"host falhou: {url} — {ex.Message}"); }
        }
        if (last != null) return last;
        throw new HttpRequestException($"nenhum host respondeu ({string.Join(", ", Hosts)})", lastEx);
    }

    /// <summary>
    /// Corre <paramref name="get"/> para cada URL candidato até um funcionar.
    /// Devolve default se todos falharem (o chamador decide o que fazer).
    /// </summary>
    public static async Task<T?> FirstOkAsync<T>(IEnumerable<string> urls, Func<string, Task<T>> get)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try { return await get(url); }
            catch (Exception ex) { last = ex; Log.Warn($"host falhou: {url} — {ex.Message}"); }
        }
        if (last != null) Log.Warn($"todos os hosts falharam ({string.Join(", ", Hosts)})");
        return default;
    }
}
