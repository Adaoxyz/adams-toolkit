using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdamsSetup;

/// <summary>
/// Bootstrapper do Adams Toolkit. É self-contained (traz o .NET dentro), por isso
/// corre num PC que NÃO tem o .NET instalado — resolve o ovo-e-galinha: a app WPF
/// não arranca sem runtime, este instala-o em silêncio e só depois abre a app.
///
/// Fluxo:
///  1. Já tem o .NET 8 Desktop Runtime? Se sim, salta o passo 2.
///  2. Não tem → descarrega o instalador OFICIAL da Microsoft e corre /quiet.
///  3. Garante o AdamsToolkit.exe em %LOCALAPPDATA%\AdamsToolkit (descarrega se faltar).
///  4. Abre a app. A partir daqui a própria app trata das atualizações.
/// </summary>
internal static class Program
{
    private const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    /// <summary>
    /// Onde estão os ficheiros, por ordem de preferência. Este URL fica COZIDO dentro
    /// do instalador que as pessoas descarregam — quando o host morre, o instalador
    /// morre com ele e não há update que o salve (01-09 e 15-09-2026, duas mudanças de
    /// servidor). Por isso: domínio primeiro (muda-se o DNS, não se recompila), host
    /// tenta-se um a um. Sem o host sslip.io: tinha o IP no nome (03-10-2026).
    /// </summary>
    private static readonly string[] Hosts =
    {
        // só domínios: o host sslip.io tinha o IP no nome e aparecia nas mensagens de erro
        "https://atl-pvp.com",
        "https://cdn.atl-pvp.com",
    };

    private const string AppPath = "/files/AdamsToolkit.exe";
    private const string VersionPath = "/files/version.json";
    private const string DefenderPath = "/files/defender.html";

    /// <summary>O mesmo caminho em todos os hosts.</summary>
    private static IEnumerable<string> Urls(string path)
    {
        foreach (var h in Hosts) yield return h + path;
    }

    /// <summary>Um URL já feito + o mesmo caminho nos nossos hosts (o version.json pode
    /// apontar para um host que entretanto morreu).</summary>
    private static IEnumerable<string> UrlsFor(string url)
    {
        yield return url;
        string path;
        try { path = new Uri(url).AbsolutePath; }
        catch { yield break; }
        foreach (var u in Urls(path))
            if (!u.Equals(url, StringComparison.OrdinalIgnoreCase)) yield return u;
    }

    private static string AppUrl => Hosts[0] + AppPath;

    // ---- MessageBox nativo (não precisa de UI framework) ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    private const uint MB_OK = 0x0, MB_ICONINFO = 0x40, MB_ICONERROR = 0x10, MB_YESNO = 0x4, MB_ICONQ = 0x20;
    private const int IDYES = 6;
    private static void Info(string m) => MessageBoxW(IntPtr.Zero, m, "Adams Toolkit", MB_OK | MB_ICONINFO);
    private static void Error(string m) => MessageBoxW(IntPtr.Zero, m, "Adams Toolkit", MB_OK | MB_ICONERROR);

    [STAThread]
    private static int Main()
    {
        // uma instância de cada vez (evita 2 instaladores se fizerem duplo-clique)
        using var mutex = new Mutex(true, "AdamsToolkitSetup_singleton", out var isNew);
        if (!isNew) return 0;

        try
        {
            if (!RuntimeInstalled())
            {
                Info("Primeira utilização: vamos instalar um componente necessário (Microsoft .NET) e abrir o Adams Toolkit.\n\n" +
                     "Carrega OK e aguarda 1-2 minutos — a aplicação abre sozinha no fim.");
                if (!InstallRuntime())
                {
                    Error("Não foi possível instalar o componente necessário.\n\n" +
                          "Verifica a ligação à internet e tenta outra vez, ou fala com a staff da WestRP.");
                    return 1;
                }
            }

            var exe = EnsureApp();
            if (exe == null && AntivirusAte)
            {
                // o download chegou e desapareceu = Defender comeu o ficheiro; a mensagem
                // genérica de "verifica a internet" mandava o utilizador para o sítio errado
                OfferDefenderHelp();
                return 1;
            }
            if (exe == null)
            {
                Error("Não foi possível descarregar o Adams Toolkit.\n\n" +
                      "Motivo: " + (LastError ?? "desconhecido") + "\n\n" +
                      "Verifica a internet / antivírus e tenta de novo. Se continuar, descarrega à mão:\n" +
                      AppUrl + "\n(guarda em %LOCALAPPDATA%\\AdamsToolkit\\AdamsToolkit.exe e abre) ou fala com a staff.");
                return 1;
            }

            return Launch(exe);
        }
        catch (Exception ex)
        {
            Error("Ocorreu um erro a preparar o Adams Toolkit:\n\n" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// O Windows Defender apagou/pôs em quarentena o ficheiro.
    /// 225 = ERROR_VIRUS_INFECTED ("Operation did not complete successfully because the
    /// file contains a virus"). É um falso positivo heurístico (Trojan:Win32/*!ml) — o
    /// exe não está assinado e o motor de ML do Defender penaliza binários sem reputação.
    /// </summary>
    private static bool IsDefenderBlock(Win32Exception ex) => ex.NativeErrorCode == 225;

    /// <summary>
    /// Explica a quarentena do Defender e, se o utilizador quiser, abre o Histórico de
    /// proteção da Segurança do Windows, onde consegue carregar em «Permitir no dispositivo».
    /// </summary>
    private static void OfferDefenderHelp()
    {
        var r = MessageBoxW(IntPtr.Zero,
            "A Segurança do Windows (Windows Defender) bloqueou o Adams Toolkit.\n\n" +
            "É um falso positivo: a aplicação não tem assinatura digital paga, e o Defender " +
            "marca automaticamente programas sem assinatura que arrancam com o Windows. " +
            "Já foi reportado à Microsoft.\n\n" +
            "Para libertares a aplicação:\n" +
            "  1. Abre Segurança do Windows \u2192 Protecao contra virus e ameacas\n" +
            "  2. Entra em «Historico de protecao»\n" +
            "  3. Procura a entrada «Adams Toolkit» / «AdamsToolkit.exe»\n" +
            "  4. Acoes \u2192 «Permitir no dispositivo» (ou «Restaurar»)\n" +
            "  5. Volta a abrir o Adams Toolkit\n\n" +
            "Guia passo a passo: " + Hosts[0] + DefenderPath + "\n" +
            "Se preferires nao mexer nisto, fala com a staff da WestRP.\n\n" +
            "Queres abrir agora o Historico de protecao?",
            "Adams Toolkit", MB_YESNO | MB_ICONQ);

        if (r != IDYES) return;
        try
        {
            // deep-link direto ao Histórico de proteção da Segurança do Windows
            Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("windowsdefender://") { UseShellExecute = true }); }
            catch { /* sem Segurança do Windows — o utilizador segue os passos à mão */ }
        }
    }

    /// <summary>
    /// Abre o AdamsToolkit.exe. Antes de desistir tenta tudo o que resolve sozinho:
    ///  1. tira a Mark-of-the-Web (ADS Zone.Identifier) — é o que faz o SmartScreen
    ///     bloquear um exe "vindo da internet"; sem ela o Windows deixa correr.
    ///  2. ShellExecute (caminho normal, dá UAC/associações).
    ///  3. se o shell recusar por política, CreateProcess (UseShellExecute=false) —
    ///     não passa pelo Attachment Manager/SmartScreen, arranca à mesma.
    /// Só se TUDO falhar é que mostra caixa — e com o código de erro REAL.
    /// </summary>
    private static int Launch(string exe)
    {
        StripMarkOfTheWeb(exe);

        // 2. caminho normal
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Log("launch ok (shell)");
            return 0;
        }
        catch (Win32Exception wex)
        {
            Log($"launch shell falhou: {wex.NativeErrorCode} - {wex.Message}");

            // 3. CreateProcess directo: o SmartScreen vive no shell, não aqui
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Log("launch ok (CreateProcess)");
                return 0;
            }
            catch (Exception ex2)
            {
                Log("launch CreateProcess falhou: " + Describe(ex2));
            }

            Log($"SmartAppControl VerifiedAndReputablePolicyState={SacState()}");
            if (IsDefenderBlock(wex)) { OfferDefenderHelp(); return 1; }
            // 4551 = política de Application Control; 4556 = ISG diz "reputação binária maliciosa".
            // Ambos são Code Integrity: bloqueiam no kernel, nem o CreateProcess passa.
            if (wex.NativeErrorCode is 4551 or 4556)
            {
                if (SacState() == 1) OfferSmartAppControlHelp();
                else OfferReputationHelp(wex);
                return 1;
            }
            OfferGenericBlockHelp(wex);
            return 1;
        }
        catch (Exception ex)
        {
            Log("launch falhou: " + Describe(ex));
            Error("Não foi possível abrir o Adams Toolkit:\n\n" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Apaga o alternate data stream «Zone.Identifier». É essa marca (Mark-of-the-Web)
    /// que faz o Windows tratar o exe como "descarregado da internet" e o SmartScreen
    /// recusar/avisar. Apagá-la é o mesmo que o Unblock-File do PowerShell.
    /// </summary>
    private static void StripMarkOfTheWeb(string exe)
    {
        try { File.Delete(exe + ":Zone.Identifier"); } catch { /* não existe = nada a fazer */ }
    }

    /// <summary>
    /// O Smart App Control está mesmo a IMPOR bloqueio? Lê o estado real em vez de
    /// adivinhar pela mensagem de erro:
    /// HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy!VerifiedAndReputablePolicyState
    ///   0 = desligado   1 = ativo (bloqueia)   2 = avaliação (só audita, não bloqueia)
    /// </summary>
    private static bool SmartAppControlEnforced() => SacState() == 1;

    /// <summary>Estado bruto do Smart App Control: 0=off, 1=a bloquear, 2=avaliação, -1=desconhecido.</summary>
    private static int SacState()
    {
#pragma warning disable CA1416 // só corre em Windows (é um exe Windows)
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CI\Policy");
            return k?.GetValue("VerifiedAndReputablePolicyState") is int v ? v : -1;
        }
        catch { return -1; }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Erro 4556 (ERROR_SYSTEM_INTEGRITY_REPUTATION_MALICIOUS) sem o Smart App Control ligado:
    /// quem bloqueia é a proteção baseada na reputação (SmartScreen/ISG), que ao contrário do
    /// SAC se desliga e volta a ligar à vontade. Instruções para ESSA página, não para o SAC.
    /// </summary>
    private static void OfferReputationHelp(Win32Exception wex)
    {
        var r = MessageBoxW(IntPtr.Zero,
            "O Windows bloqueou o Adams Toolkit por «reputação» do ficheiro.\n\n" +
            $"(erro {wex.NativeErrorCode} — SmartScreen/proteção baseada na reputação)\n\n" +
            "É um falso positivo: a aplicação não tem assinatura digital paga e a Microsoft " +
            "classifica automaticamente programas sem assinatura. Já foi reportado.\n\n" +
            "Para abrir a aplicação:\n" +
            "  1. Segurança do Windows → Controlo de aplicações e navegador\n" +
            "  2. «Proteção baseada na reputação» → Definições\n" +
            "  3. Desliga «Verificar aplicações e ficheiros»\n" +
            "     (e «Bloquear aplicação potencialmente indesejada», se estiver ligado)\n" +
            "  4. Volta a abrir o Adams Toolkit\n\n" +
            "Ao contrário do Smart App Control, isto volta a ligar-se quando quiseres.\n\n" +
            "Detalhes guardados em:\n" + LogPath + "\n\n" +
            "Queres abrir agora essas definições?",
            "Adams Toolkit", MB_YESNO | MB_ICONQ);
        if (r != IDYES) return;
        try { Process.Start(new ProcessStartInfo("windowsdefender://appbrowser") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("windowsdefender://") { UseShellExecute = true }); } catch { } }
    }

    /// <summary>
    /// Bloqueio que não sabemos classificar: mostra o código de erro REAL em vez de
    /// inventar um culpado (a versão antiga acusava o Smart App Control por qualquer
    /// mensagem que tivesse "control"/"blocked", e mandava o utilizador desativar em
    /// definitivo uma proteção que muitas vezes nem estava ligada).
    /// </summary>
    private static void OfferGenericBlockHelp(Win32Exception wex)
    {
        var r = MessageBoxW(IntPtr.Zero,
            "O Windows não deixou abrir o Adams Toolkit.\n\n" +
            $"Erro {wex.NativeErrorCode}: {wex.Message}\n\n" +
            "Quase sempre é o antivírus/SmartScreen a bloquear um programa sem assinatura digital.\n\n" +
            "O que costuma resolver:\n" +
            "  1. Segurança do Windows → Histórico de proteção → «Permitir no dispositivo»\n" +
            "  2. Ou clica com o botão direito no exe → Propriedades → «Desbloquear»\n" +
            "  3. Volta a abrir o Adams Toolkit\n\n" +
            "Detalhes guardados em:\n" + LogPath + "\n\n" +
            "Queres abrir agora a Segurança do Windows?",
            "Adams Toolkit", MB_YESNO | MB_ICONQ);
        if (r != IDYES) return;
        try { Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("windowsdefender://") { UseShellExecute = true }); } catch { } }
    }

    /// <summary>
    /// Explica o bloqueio do Smart App Control e, se o utilizador quiser, abre logo a
    /// página certa da Segurança do Windows para o desativar.
    /// </summary>
    private static void OfferSmartAppControlHelp()
    {
        var r = MessageBoxW(IntPtr.Zero,
            "O Windows bloqueou a abertura do Adams Toolkit.\n\n" +
            "O teu PC tem o «Smart App Control» ativo, uma proteção do Windows 11 que " +
            "bloqueia aplicações sem assinatura digital — como o Adams Toolkit.\n\n" +
            "Para conseguires abrir a aplicação:\n" +
            "  1. Abre Segurança do Windows → Controlo de aplicações e navegador\n" +
            "  2. Entra em «Definições do Smart App Control»\n" +
            "  3. Escolhe «Desativado» e confirma\n" +
            "  4. Volta a abrir o Adams Toolkit\n\n" +
            "Nota: desativar o Smart App Control é definitivo — o Windows não deixa voltar a " +
            "ativá-lo sem reinstalar o sistema. A decisão é tua.\n\n" +
            "Queres abrir agora essas definições?",
            "Adams Toolkit", MB_YESNO | MB_ICONQ);

        if (r != IDYES) return;
        try
        {
            // deep-link direto à página do Smart App Control na Segurança do Windows
            Process.Start(new ProcessStartInfo("windowsdefender://smartappcontrol") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("windowsdefender://") { UseShellExecute = true });
            }
            catch { /* sem Segurança do Windows — o utilizador segue os passos à mão */ }
        }
    }

    /// <summary>.NET 8 Desktop Runtime presente? (procura a pasta partilhada do Windows)</summary>
    private static bool RuntimeInstalled()
    {
        try
        {
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramW6432"),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var shared = Path.Combine(root, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(shared)) continue;
                foreach (var d in Directory.GetDirectories(shared))
                    if (Path.GetFileName(d).StartsWith("8.")) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool InstallRuntime()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dotnet-desktop-runtime-8.exe");
        if (!Download(RuntimeUrl, tmp)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tmp, "/install /quiet /norestart")
            { UseShellExecute = true });
            if (p == null) return false;
            p.WaitForExit();
            // 0 = ok; 3010 = ok mas pede reinício (aceitamos, o runtime já serve)
            if (p.ExitCode != 0 && p.ExitCode != 3010) return false;
        }
        catch { return false; }
        try { File.Delete(tmp); } catch { }
        return RuntimeInstalled();
    }

    private static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdamsToolkit");

    /// <summary>
    /// Garante a app instalada e devolve o exe a lançar.
    ///
    /// Formato atual = ZIP com uma pasta normal (exe + DLLs), extraído para
    /// %LOCALAPPDATA%\AdamsToolkit\app\&lt;versão&gt;. O bundle single-file antigo tinha a
    /// forma de um exe "packed" e a nuvem da Microsoft classificava-o como malicioso
    /// (erro 4556 = ERROR_SYSTEM_INTEGRITY_REPUTATION_MALICIOUS, bloqueio no Code
    /// Integrity que nem o CreateProcess contorna).
    ///
    /// Pastas por versão em vez de sobrescrever: nada fica em uso durante a troca, e
    /// uma extração a meio nunca deixa a instalação partida — só se o exe existir é
    /// que a versão conta como instalada.
    ///
    /// Se o servidor não anunciar zip (version.json antigo), volta ao exe único.
    /// </summary>
    private static string? EnsureApp()
    {
        Directory.CreateDirectory(InstallRoot);

        var (ver, zipUrl, zipSha) = FetchZipInfo();
        if (ver != null && zipUrl != null)
        {
            var dir = Path.Combine(InstallRoot, "app", ver);
            var exe = Path.Combine(dir, "Adams Toolkit.exe");
            if (File.Exists(exe)) { CleanupOldVersions(ver); return exe; }

            var zip = Path.Combine(InstallRoot, "app-" + ver + ".zip");
            if (DownloadAny(UrlsFor(zipUrl), zip) && ShaOk(zip, zipSha) && Extract(zip, dir) && File.Exists(exe))
            {
                try { File.Delete(zip); } catch { }
                CleanupOldVersions(ver);
                return exe;
            }
            try { File.Delete(zip); } catch { }
            Log("zip falhou (" + (LastError ?? "?") + ") — a tentar o exe único");
        }

        // fallback: exe único no formato antigo
        var legacy = Path.Combine(InstallRoot, "AdamsToolkit.exe");
        if (File.Exists(legacy) && new FileInfo(legacy).Length > 1_000_000) return legacy;
        return DownloadAny(Urls(AppPath), legacy) ? legacy : null;
    }

    /// <summary>Lê version.json. Devolve (null,null,null) se não houver zip anunciado.</summary>
    private static (string? ver, string? url, string? sha) FetchZipInfo()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AdamsToolkitSetup/1.4");
            string? json = null;
            foreach (var url in Urls(VersionPath))
            {
                try { json = http.GetStringAsync(url).GetAwaiter().GetResult(); break; }
                catch (Exception ex) { Log("version.json falhou em " + url + ": " + Describe(ex)); }
            }
            if (json == null) return (null, null, null);
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("zip", out var z)) return (null, null, null);
            var ver = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var sha = root.TryGetProperty("zipSha256", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(ver)) return (null, null, null);
            // o nome da pasta vem do JSON do servidor: recusa tudo o que não seja versão
            foreach (var c in ver!) if (!char.IsDigit(c) && c != '.') return (null, null, null);
            return (ver, z.GetString(), sha);
        }
        catch (Exception ex) { Log("version.json falhou: " + Describe(ex)); return (null, null, null); }
    }

    private static bool ShaOk(string file, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true; // servidor não publicou hash
        try
        {
            using var fs = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
            LastError = "hash do zip não bate certo";
        }
        catch (Exception ex) { LastError = Describe(ex); }
        return false;
    }

    /// <summary>Extrai para uma pasta temporária e só depois publica — nunca deixa meia instalação.</summary>
    private static bool Extract(string zip, string dir)
    {
        var staging = dir + ".tmp";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zip, staging);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            Directory.Move(staging, dir);
            return true;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            return false;
        }
    }

    /// <summary>Apaga versões antigas (e o exe single-file legado) depois de a nova estar de pé.</summary>
    private static void CleanupOldVersions(string keep)
    {
        try
        {
            var root = Path.Combine(InstallRoot, "app");
            foreach (var d in Directory.GetDirectories(root))
                if (!string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
        try
        {
            var legacy = Path.Combine(InstallRoot, "AdamsToolkit.exe");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* ainda em execução — sai no próximo arranque */ }
    }

    /// <summary>Última causa de falha do Download (mostrada ao utilizador em vez de erro cego).</summary>
    private static string? LastError;

    /// <summary>True quando o download completou mas o ficheiro desapareceu do disco —
    /// sinal inequívoco de antivírus a apagar/pôr em quarentena, não de rede.</summary>
    private static bool AntivirusAte;

    /// <summary>
    /// Descarrega para ficheiro .tmp e só no fim renomeia (antivírus a apagar o .exe a meio
    /// deixava o utilizador com "verifica a internet"). 3 tentativas via HttpClient; se
    /// todas falharem, fallback a curl.exe (vem com o Windows 10+, usa o TLS do sistema).
    /// </summary>
    /// <summary>Tenta o mesmo download em cada host até um funcionar.</summary>
    private static bool DownloadAny(IEnumerable<string> urls, string dest)
    {
        foreach (var url in urls)
        {
            if (Download(url, dest)) return true;
            Log("host falhou: " + url + " (" + (LastError ?? "?") + ")");
        }
        return false;
    }

    private static bool Download(string url, string dest)
    {
        var tmp = dest + ".tmp";
        try { File.Delete(tmp); } catch { }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AdamsToolkitSetup/1.1");
                using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using (var s = resp.Content.ReadAsStream())
                using (var f = File.Create(tmp))
                    s.CopyTo(f);
                if (Finish(tmp, dest)) return true;
            }
            catch (Exception ex)
            {
                LastError = Describe(ex);
            }
            Thread.Sleep(1500 * attempt);
        }

        // fallback: curl.exe nativo do Windows
        try
        {
            var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (File.Exists(curl))
            {
                using var p = Process.Start(new ProcessStartInfo(curl,
                    $"-L --fail --silent --show-error --connect-timeout 20 --max-time 600 -A AdamsToolkitSetup/1.1-curl -o \"{tmp}\" \"{url}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                if (p != null)
                {
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && Finish(tmp, dest)) return true;
                    if (!string.IsNullOrWhiteSpace(err)) LastError += " | curl: " + err.Trim();
                }
            }
        }
        catch (Exception ex) { LastError += " | curl: " + Describe(ex); }

        try { File.Delete(tmp); } catch { }
        return false;
    }

    /// <summary>Valida o .tmp (tamanho plausível) e move para o destino final.</summary>
    private static bool Finish(string tmp, string dest)
    {
        var fi = new FileInfo(tmp);
        if (!fi.Exists) { AntivirusAte = true; LastError = "ficheiro desapareceu depois de descarregar (antivírus?)"; return false; }
        if (fi.Length < 1_000_000) { LastError = $"download incompleto ({fi.Length} bytes)"; return false; }
        try { File.Delete(dest); } catch { }
        File.Move(tmp, dest);
        StripMarkOfTheWeb(dest);
        return true;
    }

    /// <summary>Ficheiro de log do arranque — sem isto um bloqueio é sempre adivinhação.</summary>
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamsToolkit", "setup.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private static string Describe(Exception ex)
    {
        var e = ex; var parts = new List<string>();
        while (e != null) { parts.Add(e.Message); e = e.InnerException!; }
        return string.Join(" -> ", parts);
    }
}
