using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AdamsToolkit.Core;

/// <summary>
/// INATIVO desde a v1.53.0 — a aba "Atualizações" passou a ser só de DRIVERS
/// (decisão do dono: "é mais de drivers e não de aplicativos, estilo Driver
/// Booster"). Ficheiro mantido para o caso de as atualizações de programas
/// voltarem noutra aba; nada o chama neste momento.
///
/// Atualizações de programas instalados via winget (`winget upgrade`) — os
/// pacotes winget apontam para os instaladores oficiais dos fabricantes.
/// O output é uma tabela alinhada por colunas cujo cabeçalho muda com o idioma
/// do Windows; o parser usa as POSIÇÕES das colunas do cabeçalho (linha antes
/// dos '---'), não os nomes. Entradas com ID truncado ('…') são ignoradas —
/// não dava para as atualizar com precisão.
/// </summary>
public static class WingetUpdateService
{
    public sealed class AppUpdate
    {
        public string Name { get; init; } = "";
        public string Id { get; init; } = "";
        public string Current { get; init; } = "";
        public string Available { get; init; } = "";
    }

    public sealed record ScanResult(bool Ok, string? Error, List<AppUpdate> Updates)
    {
        public DateTime When { get; } = DateTime.Now;
    }

    public static ScanResult? Last { get; private set; }

    public static async Task<ScanResult> ScanAsync()
    {
        if (!await WingetInstaller.IsAvailableAsync())
            return Cache(new ScanResult(false, "winget não está disponível neste PC.", new()));

        try
        {
            var psi = new ProcessStartInfo("winget",
                "upgrade --accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return Cache(new ScanResult(false, "winget não arrancou.", new()));

            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return Cache(new ScanResult(false, "O winget demorou demasiado tempo.", new()));
            }

            return Cache(new ScanResult(true, null, Parse(await stdout)));
        }
        catch (Exception e)
        {
            return Cache(new ScanResult(false, e.Message, new()));
        }
    }

    private static ScanResult Cache(ScanResult r) { Last = r; return r; }

    private static List<AppUpdate> Parse(string output)
    {
        var updates = new List<AppUpdate>();
        // o spinner reescreve linhas com \r — só interessa o que ficou depois do último \r
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();

        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var header = lines[i];
            var dashes = lines[i + 1];
            if (dashes.Trim().Length < 10 || dashes.Trim().Any(c => c != '-')) continue;

            // colunas = início de cada palavra do cabeçalho (Nome/Id/Versão/Disponível/Origem)
            var cols = Regex.Matches(header, @"\S+(?:\s\S+)*?(?=\s{2,}|$)")
                .Select(m => m.Index).ToList();
            if (cols.Count < 4) continue;

            for (var j = i + 2; j < lines.Count; j++)
            {
                var line = lines[j];
                if (line.Trim().Length == 0) break;                 // fim da tabela
                if (line.Length < cols[3] + 1) break;               // rodapé "N upgrades available"
                var name = Cut(line, cols[0], cols[1]);
                var id = Cut(line, cols[1], cols[2]);
                var cur = Cut(line, cols[2], cols[3]);
                var avail = Cut(line, cols[3], cols.Count > 4 ? cols[4] : line.Length);
                if (id.Length == 0 || avail.Length == 0) continue;
                if (id.Contains('…') || id.Contains(' ')) continue; // truncado/ilegível → fora
                updates.Add(new AppUpdate { Name = name, Id = id, Current = cur, Available = avail });
            }
            break; // só a primeira tabela; a 2ª ("require explicit targeting") fica fora
        }
        var skipped = LoadSkipList();
        return updates
            .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(u => !IsBlocked(u, skipped))
            .ToList();
    }

    // ---- pacotes que o winget lista mas não consegue atualizar ----
    // Mostrá-los é um falso positivo: o jogador carrega em Atualizar e leva sempre
    // erro. Três origens: lista fixa (casos conhecidos), pré-lançamentos, e a lista
    // aprendida em disco (um upgrade que falhou por "não atualizável" nunca mais
    // aparece).

    private static readonly string[] BlockedIdPrefixes =
    {
        // TeamSpeak: o upgrade silencioso instala o Overwolf embutido sem perguntar
        // e as builds beta (…Client.Beta.5/.Beta.6) nem sequer aplicam por cima do
        // cliente instalado ("newer version available … but doesn't apply").
        "TeamSpeakSystems.TeamSpeakClient",
        // Chocolatey é publicado como "Install Only" — winget recusa o upgrade.
        "Chocolatey.Chocolatey",
    };

    // versão disponível de pré-lançamento → nunca propor
    private static readonly Regex PreRelease =
        new(@"(?i)(^|[-_.])(beta|alpha|rc\d*|preview|nightly|insider|dev|canary)([-_.\d]|$)",
            RegexOptions.Compiled);

    private static bool IsBlocked(AppUpdate u, HashSet<string> skipped)
    {
        if (skipped.Contains(u.Id)) return true;
        if (BlockedIdPrefixes.Any(p => u.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        // o próprio nome do pacote no winget diz que só faz instalação
        if (u.Name.Contains("Install Only", StringComparison.OrdinalIgnoreCase)) return true;
        if (PreRelease.IsMatch(u.Available)) return true;
        return false;
    }

    private static string SkipListPath =>
        Path.Combine(ConfigService.DataDir, "winget-skip.txt");

    private static HashSet<string> LoadSkipList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(SkipListPath))
                foreach (var l in File.ReadAllLines(SkipListPath))
                    if (l.Trim().Length > 0) set.Add(l.Trim());
        }
        catch { }
        return set;
    }

    private static void AddToSkipList(string id)
    {
        try
        {
            if (LoadSkipList().Contains(id)) return;
            File.AppendAllText(SkipListPath, id + Environment.NewLine);
            Log($"pacote marcado como não-atualizável: {id}");
        }
        catch { }
    }

    /// <summary>Mensagens/códigos do winget que significam "este pacote não dá para atualizar".</summary>
    private static bool MeansNotUpgradeable(string message, int exitCode)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("cannot be upgraded using winget") ||
            m.Contains("não pode ser atualizado usando winget") ||
            m.Contains("nao pode ser atualizado usando winget") ||
            m.Contains("method provided by the publisher") ||
            m.Contains("método fornecido pelo editor") ||
            m.Contains("no applicable update") ||
            m.Contains("not applicable") ||
            m.Contains("mas não se aplica") ||
            m.Contains("mas nao se aplica"))
            return true;
        // APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE / _UPGRADE_NOT_SUPPORTED
        var hex = unchecked((uint)exitCode);
        return hex == 0x8A15002B || hex == 0x8A150062;
    }

    /// <summary>Se o programa estiver aberto, o instalador falha — dá a dica certa.</summary>
    private static string? RunningHint(string appName)
    {
        try
        {
            var token = appName.Split(' ', '(')[0].Trim();
            if (token.Length < 4) return null;
            var proc = Process.GetProcesses()
                .FirstOrDefault(p => p.ProcessName.StartsWith(token, StringComparison.OrdinalIgnoreCase));
            return proc == null ? null : $"fecha o {appName} e tenta de novo";
        }
        catch { return null; }
    }

    private static string Cut(string line, int start, int end)
    {
        if (start >= line.Length) return "";
        end = Math.Min(end, line.Length);
        return line[start..end].Trim();
    }

    /// <summary>Atualiza um pacote (silencioso; UAC pode aparecer por instalador).</summary>
    private static void Log(string m)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(DriverUpdateService.LogsDir, "winget.log"),
                $"{DateTime.Now:dd/MM HH:mm:ss} {m}{Environment.NewLine}");
        }
        catch { }
    }

    public static async Task<(bool ok, string message)> UpgradeAsync(
        string id, IProgress<string> progress, CancellationToken ct, string appName = "")
    {
        Log($"upgrade início: {id}");
        try
        {
            var psi = new ProcessStartInfo("winget",
                $"upgrade -e --id {id} --silent --accept-package-agreements " +
                "--accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "winget indisponível");

            var lastLine = "";
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await p.StandardOutput.ReadLineAsync() is { } raw)
                    {
                        var line = raw.Contains('\r') ? raw[(raw.LastIndexOf('\r') + 1)..] : raw;
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        lastLine = line;
                        var m = Regex.Match(line, @"(\d{1,3})\s*%");
                        progress.Report(m.Success ? $"{m.Groups[1].Value}% — {id}" : line);
                    }
                }
                catch { }
            }, ct);
            _ = p.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(20));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "demorou demasiado tempo");
            }

            Log($"upgrade fim: {id} exit=0x{p.ExitCode:X8} última linha: {lastLine}");
            if (p.ExitCode == 0) return (true, "atualizado ✓");

            var msg = lastLine.Length > 0 ? lastLine : $"winget saiu com código 0x{p.ExitCode:X8}";

            // pacote que o winget nunca vai conseguir atualizar → esconder daqui p/ a frente
            if (MeansNotUpgradeable(msg, p.ExitCode))
            {
                AddToSkipList(id);
                Last = null; // força re-scan sem este pacote
                return (false, "este programa não se atualiza pelo winget — foi removido da lista");
            }

            // instalador que rebenta quase sempre porque o programa está aberto
            if (appName.Length > 0 && RunningHint(appName) is { } hint)
                return (false, $"o instalador falhou — {hint}");

            return (false, msg.Length > 90 ? msg[..90] + "…" : msg);
        }
        catch (Exception e)
        {
            Log($"upgrade exceção: {id}: {e.Message}");
            return (false, e.Message);
        }
    }
}
