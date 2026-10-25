using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdamsToolkit.Core;

/// <summary>
/// Auto-update: no arranque compara a versão local com version.json no nosso servidor;
/// se houver mais recente, descarrega, valida sha256, troca o exe e reinicia.
/// O exe em execução não pode ser apagado mas PODE ser renomeado — truque clássico:
/// atual → ".old" (limpo no próximo arranque), novo → caminho original.
/// </summary>
public static class SelfUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    // O exe antigo vai para %AppData%\AdamsToolkit — nunca fica visível ao lado
    // do exe do utilizador. Fallback (volume diferente): ".old" escondido.
    private static string OldExePath => Path.Combine(ConfigService.DataDir, "previous.exe");

    /// <summary>Apaga restos da versão anterior; retry em background porque o
    /// processo antigo pode ainda estar a terminar quando o novo arranca.</summary>
    public static void CleanupOldVersion()
    {
        CleanupOldInstallFolders();
        var legacy = Environment.ProcessPath + ".old";
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                var allGone = true;
                foreach (var f in new[] { OldExePath, legacy })
                {
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch { allGone = false; }
                }
                if (allGone) return;
                await Task.Delay(2000);
            }
        });
    }

    /// <summary>Raiz da instalação por pastas de versão (a mesma que o AdamsToolkitSetup usa).</summary>
    private static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdamsToolkit");

    /// <summary>
    /// Instala a versão nova numa pasta própria e relança-a a partir de lá.
    /// Pasta por versão = nada é sobrescrito enquanto está em uso, por isso não há o
    /// truque do rename nem ficheiros bloqueados; a antiga é apagada no arranque seguinte.
    /// </summary>
    private static async Task<bool> TryUpdateFromZipAsync(string zipUrl, string? sha, string ver, Action<string> status)
    {
        try
        {
            status($"Nova versão {ver} — a descarregar…");
            var bytes = await Endpoints.FirstOkAsync(Endpoints.WithFallbacks(zipUrl), u => Http.GetByteArrayAsync(u));
            if (bytes == null) { Log.Error($"download do zip falhou em todos os hosts ({zipUrl})"); return false; }

            if (!string.IsNullOrEmpty(sha))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(sha, StringComparison.OrdinalIgnoreCase))
                {
                    status("Atualização com hash inválido — ignorada.");
                    return false;
                }
            }

            status("A instalar atualização…");
            var dir = Path.Combine(InstallRoot, "app", ver);
            var staging = dir + ".tmp";
            var zip = Path.Combine(Path.GetTempPath(), $"AdamsToolkit-{ver}.zip");
            await File.WriteAllBytesAsync(zip, bytes);
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                ZipFile.ExtractToDirectory(zip, staging);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                Directory.Move(staging, dir);
            }
            finally { try { File.Delete(zip); } catch { } }

            var exe = Path.Combine(dir, "Adams Toolkit.exe");
            if (!File.Exists(exe)) return false;

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = dir,
                Arguments = App.StartMinimized ? "--startup" : "",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apaga pastas de versões antigas e o exe single-file legado. Só corre depois de a
    /// versão atual já estar a correr, por isso o que fica é sempre o que está em uso.
    /// </summary>
    private static void CleanupOldInstallFolders()
    {
        try
        {
            var here = Path.GetDirectoryName(Environment.ProcessPath);
            var root = Path.Combine(InstallRoot, "app");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                if (string.Equals(d, here, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { }
            }
            // exe do formato antigo: só some quando já não é ele a correr
            var old = Path.Combine(InstallRoot, "AdamsToolkit.exe");
            if (File.Exists(old) && !string.Equals(old, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
        }
        catch { }
    }

    /// <summary>Devolve true se atualizou (o chamador deve fechar a app; a nova já foi lançada).</summary>
    public static async Task<bool> TryUpdateAsync(Action<string> status)
    {
        try
        {
            // version.json: primeiro onde a config remota manda, depois os hosts
            // conhecidos — um host morto não pode voltar a deixar toda a gente presa
            // numa versão antiga (01-09 e 15-09-2026).
            var json = await Endpoints.FirstOkAsync(
                Endpoints.All("/files/version.json", ConfigService.Current.AuthApiBase),
                u => Http.GetStringAsync(u));
            if (json == null) { Log.Warn("sem version.json em nenhum host — update adiado"); return false; }
            var doc = JsonDocument.Parse(json).RootElement;

            var remote = Version.Parse(doc.GetProperty("version").GetString()!);
            var local = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (remote <= local) return false;

            // Formato atual = zip com pasta normal (exe + DLLs). O exe single-file
            // continua publicado em "url" só para clientes <= 1.90.0, que não sabem ler
            // isto; a partir daqui a app instala-se em %LOCALAPPDATA%\AdamsToolkit\app\<versão>.
            if (doc.TryGetProperty("zip", out var zurl) && zurl.GetString() is string zipUrl)
            {
                var zsha = doc.TryGetProperty("zipSha256", out var zh) ? zh.GetString() : null;
                if (await TryUpdateFromZipAsync(zipUrl, zsha, remote.ToString(3), status)) return true;
                // zip falhou (rede/hash) — cai para o exe único em vez de ficar sem update
            }

            var url = doc.GetProperty("url").GetString()!;
            var sha = doc.TryGetProperty("sha256", out var s) ? s.GetString() : null;

            status($"Nova versão {remote.ToString(3)} — a descarregar…");
            var bytes = await Endpoints.FirstOkAsync(Endpoints.WithFallbacks(url), u => Http.GetByteArrayAsync(u));
            if (bytes == null) { Log.Error($"download do exe falhou em todos os hosts ({url})"); return false; }

            if (!string.IsNullOrEmpty(sha))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(sha, StringComparison.OrdinalIgnoreCase))
                {
                    status("Atualização com hash inválido — ignorada.");
                    return false;
                }
            }

            var current = Environment.ProcessPath;
            if (current == null) return false;
            var tmp = Path.Combine(ConfigService.DataDir, "update.tmp.exe");
            await File.WriteAllBytesAsync(tmp, bytes);

            status("A instalar atualização…");
            var old = OldExePath;
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(current, old); // rename para AppData (mesmo volume) — some da pasta do user
            }
            catch
            {
                // volume diferente: rename cross-volume falha — usa ".old" escondido ao lado
                old = current + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(current, old);
                try { File.SetAttributes(old, FileAttributes.Hidden); } catch { }
            }
            try
            {
                File.Copy(tmp, current, true);
                File.Delete(tmp);
            }
            catch
            {
                File.Move(old, current); // restaura se a troca falhar
                throw;
            }

            // Mantém o modo de arranque: update silencioso na bandeja não deve
            // abrir a janela ao relançar.
            Process.Start(new ProcessStartInfo(current)
            {
                UseShellExecute = true,
                Arguments = App.StartMinimized ? "--startup" : "",
            });
            return true;
        }
        catch
        {
            return false; // offline / servidor sem update — segue arranque normal
        }
    }
}
