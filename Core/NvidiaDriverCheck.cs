using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdamsToolkit.Core;

/// <summary>
/// Verificação da última versão de driver NVIDIA através dos serviços OFICIAIS
/// da NVIDIA (nvidia.com / gfwsl.geforce.com) — os mesmos que alimentam a página
/// "Download de drivers". Só WHQL. Qualquer falha em qualquer passo devolve null:
/// nunca se afirma "há atualização" sem confirmação da própria NVIDIA.
/// </summary>
public static class NvidiaDriverCheck
{
    public class Result
    {
        public string Installed { get; init; } = "";
        public string Latest { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public bool IsNewer { get; init; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static Result? _cached;
    private static bool _checked;

    /// <summary>
    /// name: nome WMI da GPU (ex. "NVIDIA GeForce RTX 4070").
    /// wmiDriverVersion: versão WMI (ex. "32.0.15.6094" → 560.94).
    /// Cache por sessão — a NVIDIA não publica drivers a cada minuto.
    /// </summary>
    public static async Task<Result?> CheckAsync(string name, string wmiDriverVersion)
    {
        if (_checked) return _cached;
        _checked = true;
        try { _cached = await RunCheck(name, wmiDriverVersion); }
        catch { _cached = null; }
        return _cached;
    }

    private static async Task<Result?> RunCheck(string name, string wmiDriverVersion)
    {
        var installed = ToUserVersion(wmiDriverVersion);
        if (installed == null) return null;

        var gpuName = Normalize(name);

        // nº do modelo (4070, 1060, 960…) → candidatos de série ("40", "10", "900")
        var numMatch = Regex.Match(gpuName, @"\d{3,4}");
        if (!numMatch.Success) return null;
        var num = numMatch.Value;

        var series = await FetchLookup("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2");
        if (series.Count == 0) return null;

        var candidates = series.Where(s =>
        {
            var m = Regex.Match(s.name, @"\d{2,4}");
            if (!m.Success) return false;
            var tok = m.Value;
            return num.StartsWith(tok, StringComparison.Ordinal) ||
                   (num.Length == 3 && tok.Length == 3 && num[0] == tok[0] && tok.EndsWith("00"));
        }).Take(6).ToList();

        foreach (var (psid, _) in candidates)
        {
            var products = await FetchLookup(
                $"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID={psid}");
            var hit = products.FirstOrDefault(p =>
                string.Equals(Normalize(p.name), gpuName, StringComparison.OrdinalIgnoreCase));
            if (hit.value == null) continue;

            var latest = await FetchLatest(psid, hit.value);
            if (latest == null) return null;

            var (latestVer, url) = latest.Value;
            if (!double.TryParse(latestVer, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lv) ||
                !double.TryParse(installed, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var iv))
                return null;

            return new Result
            {
                Installed = installed,
                Latest = latestVer,
                DownloadUrl = url,
                IsNewer = lv > iv + 0.001,
            };
        }
        return null; // GPU não mapeada → silêncio (sem falso positivo)
    }

    /// <summary>Normaliza nomes de GPU: trim + remove prefixo "NVIDIA " (WMI e lista de produtos usam-no de forma inconsistente).</summary>
    private static string Normalize(string s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) s = s[7..].Trim();
        return s;
    }

    /// <summary>"32.0.15.6094" → "560.94" (últimos 5 dígitos com ponto antes dos 2 finais).</summary>
    public static string? ToUserVersion(string wmiVersion)
    {
        var digits = Regex.Replace(wmiVersion ?? "", @"\D", "");
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    private static async Task<List<(string value, string name)>> FetchLookup(string url)
    {
        var list = new List<(string, string)>();
        try
        {
            var xml = await Http.GetStringAsync(url);
            var doc = XDocument.Parse(xml);
            foreach (var lv in doc.Descendants())
            {
                if (!lv.Name.LocalName.Equals("LookupValue", StringComparison.OrdinalIgnoreCase)) continue;
                var name = lv.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                var value = lv.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                    list.Add((value, name));
            }
        }
        catch { }
        return list;
    }

    private static async Task<(string version, string url)?> FetchLatest(string psid, string pfid)
    {
        try
        {
            // isWHQL=1: só drivers certificados; dch=1: drivers modernos Win10/11
            var json = await Http.GetStringAsync(
                "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033" +
                "&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("IDS", out var ids) ||
                ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                return null;
            var info = ids[0].GetProperty("downloadInfo");
            var ver = info.GetProperty("Version").GetString();
            var url = info.TryGetProperty("DownloadURL", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(url)) return null;
            // só aceitamos download alojado pela própria NVIDIA
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("nvidia.com", StringComparison.OrdinalIgnoreCase))
                return null;
            return (ver!, url!);
        }
        catch { return null; }
    }
}
