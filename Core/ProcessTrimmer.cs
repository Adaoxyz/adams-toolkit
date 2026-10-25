using System.Diagnostics;
using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// "Fechar processos desnecessários": termina programas de fundo conhecidos que
/// não fazem falta enquanto se joga (nuvem, updaters, launchers de outros jogos,
/// Xbox Game Bar, Widgets, Teams…). Lista fechada — nunca toca em processos do
/// Windows, drivers, Discord, Steam, browsers, voz ou no próprio FiveM.
/// Só processos da sessão do utilizador (os serviços/sistema ficam de fora).
/// Não é permanente: voltam no próximo arranque (para isso há "Arranque do Windows").
/// </summary>
public static class ProcessTrimmer
{
    public record Rule(string Exe, string Label, string Group, string Detail = "");

    public class Found
    {
        public Rule Rule { get; init; } = null!;
        public List<Process> Procs { get; } = new();
        public long Bytes { get; set; }
        public string Mb => Bytes > 0 ? $"{Bytes / 1024 / 1024} MB" : "";
        /// <summary>Serviço do Windows (sessão 0) — para-se elevado, não se mata.</summary>
        public string? ServiceName { get; init; }
        public int Count => ServiceName != null ? 1 : Procs.Count;
        /// <summary>Selecionado por defeito na lista.</summary>
        public bool Default { get; init; } = true;
    }

    public const string GroupOther = "Outros programas de fundo";
    public const string GroupServices = "Serviços do Windows";

    // ---------- heurística "outros de fundo" ----------
    // Processos da sessão do utilizador, SEM janela visível e FORA de %WINDIR%
    // (componentes do Windows nunca entram), que não estejam protegidos.
    // Protegidos: o que se usa a jogar — Discord, Steam, FiveM/Rockstar, browsers,
    // voz, Spotify, overlays NVIDIA/AMD, software de periféricos e áudio.
    private static readonly string[] ProtectedPrefixes =
    {
        // o próprio toolkit
        "Adams Toolkit", "AdamsToolkit",
        // jogo
        "FiveM", "GTA5", "GTAV", "Launcher", "SocialClub", "RockstarService", "RockstarSteamHelper",
        "Steam", "steamwebhelper", "GameOverlayUI",
        "Discord", "DiscordPTB", "DiscordCanary",
        // browsers
        "chrome", "firefox", "msedge", "opera", "brave", "vivaldi", "browser",
        // voz / social
        "TeamSpeak", "ts3client", "ts5client", "mumble", "Telegram", "WhatsApp", "Signal", "Spotify",
        "obs64", "obs32", "Streamlabs", "Medal", "NVIDIA", "nvcontainer", "nvsphelper", "NVDisplay",
        "RadeonSoftware", "AMDRSServ", "AMDRSSrcExt", "atieclxx", "atiesrxx",
        // periféricos / RGB / áudio
        "Logi", "lghub", "LCore", "LogiOverlay", "Razer", "RzSynapse", "GameManager",
        "iCUE", "Corsair", "SteelSeries", "HyperX", "NGenuity", "Armoury", "ArmouryCrate", "ROG", "LightingService",
        "MSI", "Dragon", "Wooting", "Glorious", "OpenRGB", "SignalRGB", "GHUB", "ASUS", "GIGABYTE", "RGBFusion",
        "Realtek", "RtkAud", "RAVBg", "RAVCpl", "Nahimic", "SoundBlaster", "Creative", "VoiceMeeter", "audiodg",
        "DTS", "Dolby", "Sonic", "WavesSvc", "Synapse", "SetPoint", "Xbox", "XInput",
        "AutoHotkey", "AutoHotkey64", "AutoHotkeyU64", "Rainmeter", "RTSS", "MSIAfterburner", "HWiNFO",
        "TeamViewer", "AnyDesk", "RustDesk", "Parsec",
        // sistema / segurança (mesmo fora de %WINDIR% ficam de fora)
        "MsMpEng", "SecurityHealth", "NisSrv", "MpDefender", "Malwarebytes", "mbam", "avast", "avg", "Kaspersky",
        "ESET", "ekrn", "egui", "Norton", "McAfee", "Bitdefender", "explorer", "dwm", "sihost", "ctfmon",
        "conhost", "dllhost", "RuntimeBroker", "svchost", "csrss", "winlogon", "wininit", "lsass", "services",
        "fontdrvhost", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "smartscreen", "taskhostw", "WmiPrvSE", "OpenConsole", "WindowsTerminal",
        "powershell", "pwsh", "cmd", "Code", "devenv",
    };

    private static bool IsProtected(string name) =>
        ProtectedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // ---------- serviços dispensáveis (sessão 0) ----------
    // Só "parar" nesta sessão (voltam no reboot) — nunca desativar, nunca
    // ServiceChecker.Monitored (o servidor exige-os ativos).
    public static readonly (string Name, string Label)[] OptionalServices =
    {
        ("Spooler",            "Fila de impressão"),
        ("PrintNotify",        "Notificações de impressão"),
        ("Fax",                "Fax"),
        ("WSearch",            "Indexação de pesquisa do Windows"),
        ("MapsBroker",         "Mapas offline"),
        ("RetailDemo",         "Modo de demonstração de loja"),
        ("WMPNetworkSvc",      "Partilha de rede do Media Player"),
        ("XblAuthManager",     "Xbox Live — autenticação"),
        ("XblGameSave",        "Xbox Live — saves"),
        ("XboxNetApiSvc",      "Xbox Live — rede"),
        ("XboxGipSvc",         "Xbox — acessórios"),
        ("RemoteRegistry",     "Registo remoto"),
        ("wisvc",              "Windows Insider"),
        ("TrkWks",             "Rastreio de ligações distribuídas"),
        ("dmwappushservice",   "WAP Push (telemetria)"),
        ("DoSvc",              "Otimização de entrega (P2P de updates)"),
        ("WerSvc",             "Relatório de erros do Windows"),
        ("SSDPSRV",            "Descoberta SSDP"),
        ("upnphost",           "Dispositivos UPnP"),
        ("edgeupdate",         "Edge Update"),
        ("edgeupdatem",        "Edge Update (máquina)"),
        ("MicrosoftEdgeElevationService", "Edge — elevação"),
        ("GoogleUpdaterService", "Google Updater"),
        ("GoogleUpdaterInternalService", "Google Updater (interno)"),
        ("gupdate",            "Google Update"),
        ("gupdatem",           "Google Update (máquina)"),
        ("AdobeUpdateService", "Adobe Update"),
        ("AGSService",         "Adobe Genuine Service"),
        ("AGMService",         "Adobe Genuine Monitor"),
        ("BraveElevationService", "Brave — elevação"),
        ("EpicOnlineServices", "Epic Online Services"),
        ("Origin Client Service", "Origin"),
        ("EABackgroundService","EA app (fundo)"),
    };

    // exe sem ".exe", case-insensitive
    private static readonly Rule[] Rules =
    {
        // nuvem / sincronização
        new("OneDrive", "OneDrive", "Nuvem"),
        new("OneDriveStandaloneUpdater", "OneDrive (updater)", "Nuvem"),
        new("Dropbox", "Dropbox", "Nuvem"),
        new("DropboxUpdate", "Dropbox (updater)", "Nuvem"),
        new("iCloudServices", "iCloud", "Nuvem"),
        new("iCloudDrive", "iCloud Drive", "Nuvem"),
        new("iCloudPhotos", "iCloud Fotos", "Nuvem"),
        new("ApplePhotoStreams", "iCloud Fotos (stream)", "Nuvem"),
        new("AppleMobileDeviceProcess", "Apple Mobile Device", "Nuvem"),
        new("GoogleDriveFS", "Google Drive", "Nuvem"),

        // updaters que ficam residentes
        new("MicrosoftEdgeUpdate", "Edge Update", "Updaters"),
        new("GoogleUpdate", "Google Update", "Updaters"),
        new("GoogleCrashHandler", "Google Crash Handler", "Updaters"),
        new("GoogleCrashHandler64", "Google Crash Handler", "Updaters"),
        new("jusched", "Java Update Scheduler", "Updaters"),
        new("AdobeARM", "Adobe Reader Update", "Updaters"),
        new("AdobeUpdateService", "Adobe Update Service", "Updaters"),

        // Adobe Creative Cloud em fundo
        new("Creative Cloud", "Adobe Creative Cloud", "Adobe"),
        new("Adobe Desktop Service", "Adobe Desktop Service", "Adobe"),
        new("AdobeIPCBroker", "Adobe IPC Broker", "Adobe"),
        new("AdobeNotificationClient", "Adobe Notificações", "Adobe"),
        new("CCXProcess", "Adobe CCX", "Adobe"),
        new("CoreSync", "Adobe CoreSync", "Adobe"),
        new("AdobeCollabSync", "Adobe Collab Sync", "Adobe"),
        new("Adobe CEF Helper", "Adobe CEF Helper", "Adobe"),

        // Microsoft "extras"
        new("ms-teams", "Microsoft Teams", "Microsoft"),
        new("Teams", "Microsoft Teams (clássico)", "Microsoft"),
        new("msteams", "Microsoft Teams", "Microsoft"),
        new("Widgets", "Widgets do Windows", "Microsoft"),
        new("WidgetService", "Widgets do Windows (serviço)", "Microsoft"),
        new("PhoneExperienceHost", "Ligação ao Telemóvel", "Microsoft"),
        new("YourPhone", "Ligação ao Telemóvel", "Microsoft"),
        new("Skype", "Skype", "Microsoft"),
        new("SkypeApp", "Skype", "Microsoft"),
        new("SkypeBackgroundHost", "Skype (fundo)", "Microsoft"),
        new("Copilot", "Copilot", "Microsoft"),
        new("GameBar", "Xbox Game Bar", "Microsoft"),
        new("GameBarFTServer", "Xbox Game Bar (FT)", "Microsoft"),
        new("XboxPcApp", "App Xbox", "Microsoft"),
        new("XboxPcAppFT", "App Xbox (FT)", "Microsoft"),
        new("XboxAppServices", "Serviços da app Xbox", "Microsoft"),

        // launchers de outros jogos (não são precisos para o FiveM)
        new("EpicGamesLauncher", "Epic Games Launcher", "Launchers"),
        new("EpicWebHelper", "Epic Games (web helper)", "Launchers"),
        new("RiotClientServices", "Riot Client", "Launchers"),
        new("RiotClientUx", "Riot Client (UI)", "Launchers"),
        new("RiotClientUxRender", "Riot Client (render)", "Launchers"),
        new("UbisoftConnect", "Ubisoft Connect", "Launchers"),
        new("upc", "Ubisoft Connect", "Launchers"),
        new("UplayWebCore", "Ubisoft Connect (web)", "Launchers"),
        new("EADesktop", "EA app", "Launchers"),
        new("EABackgroundService", "EA app (fundo)", "Launchers"),
        new("Origin", "Origin", "Launchers"),
        new("OriginWebHelperService", "Origin (web helper)", "Launchers"),
        new("Battle.net", "Battle.net", "Launchers"),
        new("GalaxyClient", "GOG Galaxy", "Launchers"),
        new("GalaxyClientHelper", "GOG Galaxy (helper)", "Launchers"),

        // overlays / eye-candy que comem frames
        new("Overwolf", "Overwolf", "Overlays"),
        new("OverwolfBrowser", "Overwolf (browser)", "Overlays"),
        new("OverwolfHelper", "Overwolf (helper)", "Overlays"),
        new("OverwolfHelper64", "Overwolf (helper)", "Overlays"),
        new("wallpaper32", "Wallpaper Engine", "Overlays"),
        new("wallpaper64", "Wallpaper Engine", "Overlays"),
    };

    private static readonly Dictionary<string, Rule> ByExe =
        Rules.ToDictionary(r => r.Exe, r => r, StringComparer.OrdinalIgnoreCase);

    /// <summary>Total de processos visíveis (o número que o Gestor de Tarefas mostra, aprox.).</summary>
    public static int TotalRunning()
    {
        try { return Process.GetProcesses().Length; } catch { return 0; }
    }

    public static Task<List<Found>> ScanAsync() => Task.Run(() =>
    {
        var map = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        int mySession;
        try { mySession = Process.GetCurrentProcess().SessionId; } catch { mySession = -1; }

        Process[] all;
        try { all = Process.GetProcesses(); } catch { return new List<Found>(); }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var myPid = Environment.ProcessId;
        var monitored = new HashSet<string>(ServiceChecker.Monitored.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var p in all)
        {
            try
            {
                // só a sessão do utilizador — serviços (sessão 0) ficam de fora
                if (mySession >= 0 && p.SessionId != mySession) { p.Dispose(); continue; }
                if (p.Id == myPid) { p.Dispose(); continue; }

                if (!ByExe.TryGetValue(p.ProcessName, out var rule))
                {
                    // heurística "outros de fundo"
                    if (IsProtected(p.ProcessName)) { p.Dispose(); continue; }
                    if (p.MainWindowHandle != IntPtr.Zero) { p.Dispose(); continue; } // tem janela = está a ser usado
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(path)) { p.Dispose(); continue; }          // sem acesso = sistema, fora
                    if (path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                    rule = new Rule(p.ProcessName, p.ProcessName, GroupOther, path);
                }

                if (!map.TryGetValue(rule.Exe, out var f))
                    map[rule.Exe] = f = new Found { Rule = rule, Default = rule.Group != GroupOther };
                f.Procs.Add(p);
                try { f.Bytes += p.WorkingSet64; } catch { }
            }
            catch { try { p.Dispose(); } catch { } }
        }

        // serviços dispensáveis a correr (parar = 1 pedido de administrador)
        try
        {
            var running = System.ServiceProcess.ServiceController.GetServices()
                .Where(sc => { try { return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running; } catch { return false; } })
                .ToDictionary(sc => sc.ServiceName, sc => sc, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, label) in OptionalServices)
            {
                if (monitored.Contains(name)) continue;
                var sc = running.TryGetValue(name, out var v) ? v
                       : running.Values.FirstOrDefault(x => x.ServiceName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase));
                if (sc == null) continue;
                map["svc:" + sc.ServiceName] = new Found
                {
                    Rule = new Rule(sc.ServiceName, label, GroupServices, "serviço · para só nesta sessão"),
                    ServiceName = sc.ServiceName,
                };
            }
        }
        catch { }

        return map.Values
            .OrderBy(f => f.Rule.Group == GroupOther ? 1 : f.Rule.Group == GroupServices ? 2 : 0)
            .ThenBy(f => f.Rule.Group)
            .ThenByDescending(f => f.Bytes)
            .ToList();
    });

    /// <summary>Fecha os processos escolhidos. Devolve (fechados, falhados, bytes libertados).</summary>
    public static Task<(int closed, int failed, long bytes)> KillAsync(IEnumerable<Found> targets) => Task.Run(() =>
    {
        int closed = 0, failed = 0; long bytes = 0;
        var list = targets.ToList();

        var svcs = list.Where(f => f.ServiceName != null).Select(f => f.ServiceName!).ToList();
        if (svcs.Count > 0)
        {
            var ok = RunElevatedAsync("--trim-sys services-stop " + string.Join(",", svcs)).GetAwaiter().GetResult();
            if (ok) closed += svcs.Count; else failed += svcs.Count;
        }

        foreach (var f in list.Where(f => f.ServiceName == null))
        {
            foreach (var p in f.Procs)
            {
                try
                {
                    if (IsCritical(p)) { failed++; continue; }
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    // heurística "outros": sem árvore (só o próprio processo)
                    p.Kill(entireProcessTree: f.Rule.Group != GroupOther);
                    if (p.WaitForExit(2000)) { closed++; bytes += ws; }
                    else failed++;
                }
                catch (InvalidOperationException) { closed++; } // já tinha saído
                catch { failed++; }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        return (closed, failed, bytes);
    });

    // ---------- juntar svchost (SvcHostSplitThresholdInKB) ----------
    // Windows divide os serviços em dezenas de svchost.exe quando há >3.5GB RAM.
    // Pôr o limiar acima da RAM instalada volta a agrupá-los (−30 a −50 processos).
    // Precisa reiniciar. Reversível: guardamos o valor original.
    private const string SvcHostKey = @"SYSTEM\CurrentControlSet\Control";
    private const string SvcHostVal = "SvcHostSplitThresholdInKB";
    private const uint SvcHostDefault = 0x5CC00; // 380000 KB (default do Windows)
    private static string SvcHostBackup => Path.Combine(ConfigService.DataDir, "svchost-split.txt");

    // Matar um processo marcado "critical" pelo Windows dá BSOD. Nunca o fazemos:
    // verificamos antes de cada Kill (Win 8.1+; se a API falhar, assumimos crítico).
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(IntPtr hProcess, out bool critical);

    private static bool IsCritical(Process p)
    {
        try { return !IsProcessCritical(p.Handle, out var c) || c; }
        catch { return true; }
    }

    public static bool SvcHostMerged
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SvcHostKey);
                var v = k?.GetValue(SvcHostVal);
                if (v is int i) return unchecked((uint)i) >= 0x40000000; // ≥1TB = juntado
                return false;
            }
            catch { return false; }
        }
    }

    public static Task<bool> SetSvcHostMergedAsync(bool on) =>
        RunElevatedAsync("--trim-sys " + (on ? "svchost-on" : "svchost-off"));

    public static Task<bool> RunElevatedPublicAsync(string args) => RunElevatedAsync(args);

    private static async Task<bool> RunElevatedAsync(string args)
    {
        if (UninstallerEngine.IsAdmin())
        {
            // já elevado: rotear o arg como o App.xaml.cs faz no arranque elevado.
            // RunHeadless só trata "--trim-sys …" — --pccheck-fix/--fix-services têm de ir aos seus handlers.
            var a = args.Split(' ');
            await Task.Run(() =>
            {
                switch (a[0])
                {
                    case "--pccheck-fix": PcCheck.RunHeadlessFix(); break;
                    case "--fix-services": ServiceChecker.RunHeadless(a); break;
                    default: RunHeadless(a); break;
                }
            });
            return true;
        }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (p == null) return false;
            await p.WaitForExitAsync();
            return true;
        }
        catch { return false; } // UAC recusado
    }

    /// <summary>Modo headless (--trim-sys …), já elevado. Sem UI, falha em silêncio.</summary>
    public static void RunHeadless(string[] args)
    {
        if (args.Length < 2) return;
        switch (args[1])
        {
            case "services-stop":
            case "services-start":
                if (args.Length < 3) return;
                foreach (var name in args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(name);
                        if (args[1] == "services-stop")
                        {
                            if (!sc.CanStop) continue;
                            if (sc.Status is System.ServiceProcess.ServiceControllerStatus.Stopped or System.ServiceProcess.ServiceControllerStatus.StopPending) continue;
                            sc.Stop();
                            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                        }
                        else
                        {
                            if (sc.Status is System.ServiceProcess.ServiceControllerStatus.Running or System.ServiceProcess.ServiceControllerStatus.StartPending) continue;
                            sc.Start();
                            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                        }
                    }
                    catch (Exception ex) { Log.Error($"parar/arrancar serviço {name} falhou", ex); }
                }
                break;

            case "svchost-on":
            case "svchost-off":
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SvcHostKey, writable: true);
                    if (k == null) return;
                    if (args[1] == "svchost-on")
                    {
                        var cur = k.GetValue(SvcHostVal);
                        if (!File.Exists(SvcHostBackup))
                            File.WriteAllText(SvcHostBackup, cur is int ci ? unchecked((uint)ci).ToString() : SvcHostDefault.ToString());
                        k.SetValue(SvcHostVal, unchecked((int)0xFFFFFFFFu), Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    else
                    {
                        uint orig = SvcHostDefault;
                        try { if (File.Exists(SvcHostBackup) && uint.TryParse(File.ReadAllText(SvcHostBackup).Trim(), out var o)) orig = o; } catch { }
                        k.SetValue(SvcHostVal, unchecked((int)orig), Microsoft.Win32.RegistryValueKind.DWord);
                        try { File.Delete(SvcHostBackup); }
                        catch (Exception ex) { Log.Swallow("apagar backup do svchost", ex); }
                    }
                }
                // mexe no limite de agrupamento do svchost: falhar em silêncio deixava
                // o PC num estado que ninguém conseguia explicar depois
                catch (Exception ex) { Log.Error($"svchost {args[1]} falhou", ex); }
                break;
        }
    }
}
