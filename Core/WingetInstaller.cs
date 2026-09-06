using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdamsToolkit.Core;

/// <summary>
/// Instalação silenciosa via Windows Package Manager (winget, incluído no Win10/11).
/// Os pacotes winget apontam para os instaladores oficiais dos fabricantes —
/// a app continua sem alojar nem redistribuir ficheiros de terceiros.
/// </summary>
public static class WingetInstaller
{
    private static bool? _available;

    public static async Task<bool> IsAvailableAsync()
    {
        if (_available.HasValue) return _available.Value;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p == null) return (_available = false).Value;
            await p.WaitForExitAsync();
            _available = p.ExitCode == 0;
        }
        catch { _available = false; }
        return _available.Value;
    }

    private static readonly Regex PctRx = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(
        @"([\d.,]+)\s*(KB|MB|GB)\s*/\s*([\d.,]+)\s*(KB|MB|GB)", RegexOptions.Compiled);

    private static double ToMb(string num, string unit)
    {
        if (!double.TryParse(num.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return unit switch { "KB" => v / 1024.0, "GB" => v * 1024.0, _ => v };
    }

    /// <summary>Instala silenciosamente; devolve (sucesso, mensagem-final).</summary>
    public static async Task<(bool ok, string message)> InstallAsync(
        string wingetId, IProgress<(double pct, string label)> progress, CancellationToken ct)
    {
        // id vem do JSON remoto — só aceitar o formato winget (Empresa.Pacote), nunca espaços/aspas
        if (!System.Text.RegularExpressions.Regex.IsMatch(wingetId ?? "", @"^[A-Za-z0-9._+-]{2,128}$"))
            return (false, "ID winget inválido.");
        var psi = new ProcessStartInfo("winget",
            $"install -e --id {wingetId} --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "winget indisponível");

            string lastLine = "";
            var reader = Task.Run(async () =>
            {
                while (!p.StandardOutput.EndOfStream)
                {
                    var raw = await p.StandardOutput.ReadLineAsync();
                    if (raw == null) break;
                    // winget reescreve a mesma linha com \r e caracteres de spinner/barra
                    foreach (var piece in raw.Split('\r'))
                    {
                        var line = new string(piece.Where(c => !char.IsControl(c) && c < 0x2500).ToArray()).Trim();
                        if (line.Length == 0) continue;
                        lastLine = line;

                        var size = SizeRx.Match(line);
                        var pct = PctRx.Match(line);
                        if (size.Success)
                        {
                            var done = ToMb(size.Groups[1].Value, size.Groups[2].Value);
                            var total = ToMb(size.Groups[3].Value, size.Groups[4].Value);
                            progress.Report((total > 0 ? done * 100.0 / total : -1,
                                $"{done:0.0} / {total:0.0} MB (winget)"));
                        }
                        else if (pct.Success && int.TryParse(pct.Groups[1].Value, out var n) && n <= 100)
                        {
                            progress.Report((n, $"{n}% (winget)"));
                        }
                        else
                        {
                            progress.Report((-1, Truncate(line, 60)));
                        }
                    }
                }
            }, ct);

            await p.WaitForExitAsync(ct);
            await reader;

            if (p.ExitCode == 0) return (true, "Instalado com sucesso.");

            // já instalado / sem versão aplicável — tratar como sucesso
            if (lastLine.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                lastLine.Contains("já está instalado", StringComparison.OrdinalIgnoreCase) ||
                (uint)p.ExitCode == 0x8A15002B)
                return (true, "Já estava instalado.");

            return (false, Truncate(lastLine.Length > 0 ? lastLine : $"winget saiu com código {p.ExitCode}", 80));
        }
        catch (OperationCanceledException) { return (false, "Cancelado."); }
        catch (Exception e) { return (false, Truncate(e.Message, 80)); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
