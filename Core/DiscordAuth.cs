using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdamsToolkit.Core;

public class DiscordUser
{
    [JsonPropertyName("userId")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("avatar")] public string AvatarUrl { get; set; } = "";

    public string DisplayName => Username;
}

/// <summary>
/// 1. A app pede um código ao backend (bot Adao.exe, via Caddy https); a resposta
///    traz também um authUrl (OAuth implicit no browser — 1 clique em "Autorizar").
/// 2. Caminho primário: abrir o authUrl no browser. Fallback: o jogador corre
///    /toolkit &lt;código&gt; no Discord Shame &amp; Adão.
/// 3. O bot confirma que está no servidor e aprova; a app faz poll até receber o token.
/// 4. Nos arranques seguintes o token é revalidado (a presença no guild é verificada de novo).
/// </summary>
public static class DiscordAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string ApiBase => ConfigService.Current.AuthApiBase.TrimEnd('/');
    private static string SessionPath => Path.Combine(ConfigService.DataDir, "session.json");

    public record PollResult(string Status, string? Token, DiscordUser? User);

    private class PollResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("userId")] public string UserId { get; set; } = "";
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    }

    private class ValidateResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("userId")] public string UserId { get; set; } = "";
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    }

    public record StartResult(string Code, string? AuthUrl);

    /// <summary>Pede um código de emparelhamento novo (+ URL OAuth para login de 1 clique);
    /// null se o backend estiver inacessível.</summary>
    public static async Task<StartResult?> StartPairingAsync()
    {
        try
        {
            var res = await Http.PostAsync($"{ApiBase}/toolkit/start", null);
            if (!res.IsSuccessStatusCode) return null;
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            var code = root.GetProperty("code").GetString();
            if (code == null) return null;
            var authUrl = root.TryGetProperty("authUrl", out var a) ? a.GetString() : null;
            return new StartResult(code, authUrl);
        }
        catch { return null; }
    }

    /// <summary>Estado do código: pending / ok (com token+user) / expired / error.</summary>
    public static async Task<PollResult> PollAsync(string code)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"{ApiBase}/toolkit/poll?code={Uri.EscapeDataString(code)}");
            var r = JsonSerializer.Deserialize<PollResponse>(json);
            if (r == null) return new PollResult("error", null, null);
            if (r.Status != "ok") return new PollResult(r.Status, null, null);
            return new PollResult("ok", r.Token,
                new DiscordUser { Id = r.UserId, Username = r.Username, AvatarUrl = r.Avatar });
        }
        catch { return new PollResult("error", null, null); }
    }

    /// <summary>User null + NetworkError false = acesso revogado (saiu do guild).</summary>
    public record ValidateResult(DiscordUser? User, bool NetworkError);

    /// <summary>Revalida o token no backend (confirma presença no guild).</summary>
    public static async Task<ValidateResult> ValidateAsync(string token)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"{ApiBase}/toolkit/validate?token={Uri.EscapeDataString(token)}");
            var r = JsonSerializer.Deserialize<ValidateResponse>(json);
            if (r is not { Ok: true }) return new ValidateResult(null, false);
            return new ValidateResult(
                new DiscordUser { Id = r.UserId, Username = r.Username, AvatarUrl = r.Avatar }, false);
        }
        catch { return new ValidateResult(null, true); }
    }

    /// <summary>Ping de presença (a app está aberta) — alimenta o painel staff "a usar agora".</summary>
    public static async Task PingAsync(string token)
    {
        try { await Http.GetStringAsync($"{ApiBase}/toolkit/ping?token={Uri.EscapeDataString(token)}"); }
        catch { }
    }

    // ---- modo offline ----
    // Backend em baixo (VPS/sslip.io) não pode trancar toda a gente fora: cada
    // validação OK guarda um snapshot local; se o servidor de verificação não
    // responder, a app deixa entrar com esse snapshot durante 7 dias. Uma resposta
    // NEGATIVA (revogado) apaga o snapshot — revogação continua a bloquear na hora.

    private static string OfflinePath => Path.Combine(ConfigService.DataDir, "offline-user.json");
    private const int OfflineGraceDays = 7;

    private class OfflineSnapshot
    {
        [JsonPropertyName("user")] public DiscordUser? User { get; set; }
        [JsonPropertyName("lastOkUtc")] public DateTime LastOkUtc { get; set; }
    }

    public static void SaveOfflineSnapshot(DiscordUser user)
    {
        try
        {
            File.WriteAllText(OfflinePath, JsonSerializer.Serialize(
                new OfflineSnapshot { User = user, LastOkUtc = DateTime.UtcNow }));
        }
        catch { }
    }

    /// <summary>User do último login validado há &lt;7 dias; null se não houver ou já expirou.</summary>
    public static DiscordUser? LoadOfflineUser()
    {
        try
        {
            if (!File.Exists(OfflinePath)) return null;
            var s = JsonSerializer.Deserialize<OfflineSnapshot>(File.ReadAllText(OfflinePath));
            if (s?.User == null) return null;
            var age = DateTime.UtcNow - s.LastOkUtc;
            return age >= TimeSpan.Zero && age <= TimeSpan.FromDays(OfflineGraceDays) ? s.User : null;
        }
        catch { return null; }
    }

    public static void ClearOfflineSnapshot()
    {
        try { File.Delete(OfflinePath); } catch { }
    }

    // ---- sessão persistida (auto-login no próximo arranque) ----

    public static void SaveSession(string token)
    {
        try { File.WriteAllText(SessionPath, token); } catch { }
    }

    public static string? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var token = File.ReadAllText(SessionPath).Trim();
            return token.Length > 0 ? token : null;
        }
        catch { return null; }
    }

    public static void ClearSession()
    {
        try { File.Delete(SessionPath); } catch { }
    }
}
