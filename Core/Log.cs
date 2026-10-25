using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace AdamsToolkit.Core;

/// <summary>
/// Registo único da app: %AppData%\AdamsToolkit\toolkit.log.
///
/// Antes disto havia meia dúzia de logs soltos (start.log, editor-crash.log,
/// appx, winget) e, sobretudo, ~150 `catch { }` a engolir erros em silêncio —
/// o jogador via "✓ feito" e ninguém ficava a saber que não tinha sido feito.
/// Regra nova: um catch que engole um erro de uma operação que MEXE no sistema
/// chama `Log.Swallow(...)`; o utilizador pode não querer saber, nós queremos.
///
/// Peso (regra da pena): sem timers, sem threads, sem buffers vivos — só escreve
/// quando alguém chama, e nunca deixa uma exceção sua rebentar a app.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(ConfigService.DataDir, "toolkit.log");
    private static string OldPath => FilePath + ".1";

    public static void Info(string msg, [CallerMemberName] string? who = null) => Write("INFO ", msg, who);
    public static void Warn(string msg, [CallerMemberName] string? who = null) => Write("AVISO", msg, who);

    public static void Error(string msg, Exception? ex = null, [CallerMemberName] string? who = null) =>
        Write("ERRO ", ex == null ? msg : $"{msg} — {Describe(ex)}", who);

    /// <summary>
    /// Erro apanhado de propósito e não mostrado ao utilizador (limpezas, sondas,
    /// "se não der, paciência"). Fica no log para o dia em que "não fez nada".
    /// </summary>
    public static void Swallow(string what, Exception ex, [CallerMemberName] string? who = null) =>
        Write("ENGOL", $"{what} — {Describe(ex)}", who);

    /// <summary>Exceção não apanhada: vai com stack trace inteiro.</summary>
    public static void Crash(string origin, Exception ex)
    {
        Write("CRASH", $"{origin}: {ex}", null);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void Write(string level, string msg, string? who)
    {
        try
        {
            lock (Gate)
            {
                var f = FilePath;
                try
                {
                    if (File.Exists(f) && new FileInfo(f).Length > MaxBytes)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(f, OldPath);
                    }
                }
                catch { /* rotação falhou: escrever é mais importante que rodar */ }

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {(who == null ? "" : who + ": ")}{msg}\n";
                File.AppendAllText(f, line, Encoding.UTF8);
            }
        }
        catch { /* sem disco/sem permissões: o log nunca pode partir a app */ }
    }

    /// <summary>
    /// Junta TODOS os registos num zip no Ambiente de trabalho, para o jogador
    /// arrastar para o ticket. Devolve o caminho, ou null se nem isso deu.
    /// </summary>
    public static string? ExportZip()
    {
        try
        {
            var dir = ConfigService.DataDir;
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"AdamsToolkit-registos-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            if (File.Exists(dest)) File.Delete(dest);

            using var z = ZipFile.Open(dest, ZipArchiveMode.Create);
            foreach (var name in new[]
                     {
                         "toolkit.log", "toolkit.log.1", "start.log", "appx.log",
                         "editor-crash.log", "netopt.json", "gameboost.json",
                     })
            {
                var src = Path.Combine(dir, name);
                if (!File.Exists(src)) continue;
                // cópia primeiro: o ficheiro pode estar a ser escrito neste instante
                var tmp = Path.Combine(Path.GetTempPath(), $"atk-{name}");
                try
                {
                    File.Copy(src, tmp, true);
                    z.CreateEntryFromFile(tmp, name);
                }
                catch (Exception ex) { Swallow($"exportar {name}", ex); }
                finally { try { File.Delete(tmp); } catch { } }
            }

            // logs\ : winget + os 3 relatórios de drivers mais recentes
            try
            {
                var logs = new DirectoryInfo(Path.Combine(dir, "logs"));
                if (logs.Exists)
                    foreach (var fi in logs.GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), "atk-" + fi.Name);
                        try { File.Copy(fi.FullName, tmp, true); z.CreateEntryFromFile(tmp, "logs/" + fi.Name); }
                        catch (Exception ex) { Swallow($"exportar logs/{fi.Name}", ex); }
                        finally { try { File.Delete(tmp); } catch { } }
                    }
            }
            catch (Exception ex) { Swallow("listar logs/", ex); }

            using (var s = z.CreateEntry("versao.txt").Open())
            {
                var b = Encoding.UTF8.GetBytes(
                    $"Adams Toolkit v{SelfUpdater.CurrentVersion}\n" +
                    $"Windows {Environment.OSVersion.Version}\n" +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                s.Write(b, 0, b.Length);
            }

            return dest;
        }
        catch (Exception ex)
        {
            Error("exportar registos falhou", ex);
            return null;
        }
    }

}
