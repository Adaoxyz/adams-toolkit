using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>Resultado de uma verificação do diagnóstico. Level: ok | warn | bad.</summary>
public record DiagItem(string Level, string Title, string Detail)
{
    public string Icon => Level switch { "ok" => "✅", "warn" => "⚠️", _ => "⛔" };
}

/// <summary>
/// Boost de FPS "check-safe" para o FiveM.
///
/// REGRA DESTE MÓDULO: só altera definições de desempenho (energia, prioridades,
/// preferências por-executável) — todas reversíveis e todas visíveis no registo.
/// NUNCA apaga rasto do sistema (Prefetch, event logs, USN journal, histórico),
/// nunca desliga o Defender nem serviços de diagnóstico: um PC com essas coisas
/// apagadas é exatamente o que uma verificação de PC procura. Tweaks novos que
/// apaguem provas NÃO entram aqui.
///
/// Mesmo modelo do <see cref="NetworkOptimizer"/>: backup JSON do valor original,
/// boost por sessão de Windows (reinicia o PC → reverte sozinho).
/// </summary>
public static class GameBoost
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "gameboost.json");

    private const string Absent = "<ausente>";
    private const string NoPerm = "sem permissão — reinicia a app como administrador";

    private class OptState
    {
        public Dictionary<string, Dictionary<string, string>> Backup { get; set; } = new();
        public HashSet<string> Applied { get; set; } = new();
        public long BootStamp { get; set; }
    }

    private static OptState _state = Load();

    private static OptState Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<OptState>(File.ReadAllText(StateFile)) ?? new OptState();
        }
        catch (Exception ex) { Log.Swallow("ler estado do boost", ex); }
        return new OptState();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_state));
        }
        // sem estado gravado não há como reverter depois — isto tem de aparecer no log
        catch (Exception ex) { Log.Error("gravar estado do boost falhou (reverter pode não repor tudo)", ex); }
    }

    public static bool IsApplied(string id) => _state.Applied.Contains(id);
    public static bool AnyApplied => _state.Applied.Count > 0;

    private static long CurrentBootStamp()
    {
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return boot.ToUnixTimeSeconds() / 60;
    }

    /// <summary>Igual ao NetworkOptimizer: boost de outro boot do Windows → reverte sozinho.</summary>
    public static Task<bool> StartupCheck { get; private set; } = Task.FromResult(false);

    public static void BeginStartupCheck() => StartupCheck = Task.Run(async () =>
    {
        if (!AnyApplied) return false;
        if (_state.BootStamp == CurrentBootStamp()) return false;
        await RevertAllAsync();
        return true;
    });

    private static void BackupValue(string tweakId, string key, string value)
    {
        if (!_state.Backup.TryGetValue(tweakId, out var d))
            _state.Backup[tweakId] = d = new();
        d.TryAdd(key, value);
    }
    private static Dictionary<string, string> GetBackup(string tweakId) =>
        _state.Backup.TryGetValue(tweakId, out var d) ? d : new();

    // ---------- registry (backup genérico "HIVE|caminho|valor") ----------

    private static RegistryKey RootOf(string hive) =>
        hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    private static string? WriteValue(string tweakId, string hive, string path, string name,
                                      object value, RegistryValueKind kind)
    {
        try
        {
            using var k = RootOf(hive).CreateSubKey(path);
            if (k == null) return $"não consegui abrir {hive}\\{path}";
            var cur = k.GetValue(name);
            BackupValue(tweakId, $"{hive}|{path}|{name}",
                cur == null ? Absent : (kind == RegistryValueKind.DWord ? $"d:{cur}" : $"s:{cur}"));
            k.SetValue(name, value, kind);
            return null;
        }
        catch (UnauthorizedAccessException) { return NoPerm; }
        catch (Exception ex) { return ex.Message; }
    }

    private static void RestoreValues(string tweakId)
    {
        foreach (var (key, val) in GetBackup(tweakId))
        {
            var parts = key.Split('|');
            if (parts.Length != 3) continue;
            try
            {
                using var k = RootOf(parts[0]).OpenSubKey(parts[1], writable: true);
                if (k == null) continue;
                if (val == Absent) k.DeleteValue(parts[2], throwOnMissingValue: false);
                else if (val.StartsWith("d:") && int.TryParse(val[2..], out var iv))
                    k.SetValue(parts[2], iv, RegistryValueKind.DWord);
                else if (val.StartsWith("s:"))
                    k.SetValue(parts[2], val[2..], RegistryValueKind.String);
            }
            catch (Exception ex) { Log.Error($"repor {key} falhou", ex); }
        }
    }

    // ---------- executáveis do FiveM ----------

    /// <summary>Launcher + processo(s) do jogo (FiveM_bXXXX_GTAProcess.exe) desta instalação.</summary>
    private static List<string> FiveMExecutables()
    {
        var list = new List<string>();
        try
        {
            var exe = FiveMLocator.ExePath;
            if (exe != null) list.Add(exe);

            var app = FiveMLocator.AppDataDir;
            if (app != null && Directory.Exists(app))
            {
                foreach (var f in Directory.EnumerateFiles(app, "FiveM*GTAProcess.exe", SearchOption.TopDirectoryOnly))
                    list.Add(f);
            }
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string NoFiveM = "não encontrei o FiveM neste PC — abre-o uma vez e tenta de novo";

    // ---------- catálogo ----------

    public static readonly IReadOnlyList<NetTweak> Tweaks = new List<NetTweak>
    {
        // ===== SEGUROS =====
        new()
        {
            Id = "accesskeys", Name = "Atalhos de acessibilidade do teclado desligados",
            Description = "Desliga os atalhos das Teclas Fixas (Shift 5x), Teclas de Filtro (Shift 8s) e Teclas de Alternância (Num Lock 5s). No meio de um strafe/spam de tecla o Windows abre a janela deles e rouba o foco ao jogo — clássico. Não precisa de admin.",
            Apply = () =>
            {
                // valores REG_SZ do Windows: default 510/126/62 → tirar o bit 4 (hotkey ativa) = 506/122/58
                var err = WriteValue("accesskeys", "HKCU", @"Control Panel\Accessibility\StickyKeys", "Flags", "506", RegistryValueKind.String);
                if (err != null) return Task.FromResult<string?>(err);
                WriteValue("accesskeys", "HKCU", @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String);
                WriteValue("accesskeys", "HKCU", @"Control Panel\Accessibility\ToggleKeys", "Flags", "58", RegistryValueKind.String);
                return Task.FromResult<string?>(null);
            },
            Revert = () => { RestoreValues("accesskeys"); return Task.FromResult<string?>(null); },
        },
        new()
        {
            Id = "gamedvr", Name = "Gravação em segundo plano desligada",
            Description = "Desliga o Game DVR/Xbox Game Bar, que grava o jogo em fundo mesmo sem tu saberes — é a causa clássica de engasgos de meio segundo. Podes continuar a gravar com o que quiseres (ShadowPlay, OBS).",
            Apply = () =>
            {
                var err = WriteValue("gamedvr", "HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                if (err != null) return Task.FromResult<string?>(err);
                WriteValue("gamedvr", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                // política de máquina: bónus, só entra com admin — falhar aqui não estraga o tweak
                WriteValue("gamedvr", "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                return Task.FromResult<string?>(null);
            },
            Revert = () => { RestoreValues("gamedvr"); return Task.FromResult<string?>(null); },
        },
        new()
        {
            Id = "mmcss", Name = "Jogos com prioridade no CPU e na GPU",
            Description = "Diz ao Windows para tratar jogos como tarefa prioritária (perfil MMCSS \"Games\"). Ajuda quando tens Discord, browser e mais coisas abertas ao mesmo tempo. Precisa de admin.",
            Apply = () =>
            {
                const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                var err = WriteValue("mmcss", "HKLM", path, "GPU Priority", 8, RegistryValueKind.DWord);
                if (err != null) return Task.FromResult<string?>(err);
                WriteValue("mmcss", "HKLM", path, "Priority", 6, RegistryValueKind.DWord);
                WriteValue("mmcss", "HKLM", path, "Scheduling Category", "High", RegistryValueKind.String);
                WriteValue("mmcss", "HKLM", path, "SFIO Priority", "High", RegistryValueKind.String);
                return Task.FromResult<string?>(null);
            },
            Revert = () => { RestoreValues("mmcss"); return Task.FromResult<string?>(null); },
        },
        new()
        {
            Id = "gpupref", Name = "FiveM sempre na placa gráfica dedicada",
            Description = "Marca o FiveM como \"Alto desempenho\" nas definições gráficas do Windows. Em portáteis e PCs com gráficos integrados evita que o jogo corra na placa fraca. Não precisa de admin.",
            Apply = () =>
            {
                var exes = FiveMExecutables();
                if (exes.Count == 0) return Task.FromResult<string?>(NoFiveM);
                foreach (var exe in exes)
                {
                    var err = WriteValue("gpupref", "HKCU",
                        @"Software\Microsoft\DirectX\UserGpuPreferences", exe, "GpuPreference=2;", RegistryValueKind.String);
                    if (err != null) return Task.FromResult<string?>(err);
                }
                return Task.FromResult<string?>(null);
            },
            Revert = () => { RestoreValues("gpupref"); return Task.FromResult<string?>(null); },
        },
        new()
        {
            Id = "fso", Name = "Ecrã inteiro sem a camada do Windows",
            Description = "Desativa as \"otimizações de ecrã inteiro\" no FiveM — em muitos PCs tira meio frame de atraso e estabiliza o tempo de frame. Em troca, o alt-tab fica um pouco mais lento. Não precisa de admin.",
            Apply = () =>
            {
                var exes = FiveMExecutables();
                if (exes.Count == 0) return Task.FromResult<string?>(NoFiveM);
                foreach (var exe in exes)
                {
                    var err = WriteValue("fso", "HKCU",
                        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", exe,
                        "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", RegistryValueKind.String);
                    if (err != null) return Task.FromResult<string?>(err);
                }
                return Task.FromResult<string?>(null);
            },
            Revert = () => { RestoreValues("fso"); return Task.FromResult<string?>(null); },
        },
        new()
        {
            Id = "prio", Name = "FiveM com prioridade alta (enquanto a app está aberta)", SessionOnly = true,
            Description = "Põe o processo do FiveM em prioridade alta assim que o abres, e vai reaplicando. Dura só enquanto o Adams Toolkit estiver aberto (fechar para a bandeja mantém).",
            Apply = () => Task.FromResult(StartPriorityWatch()),
            Revert = () => Task.FromResult(StopPriorityWatch()),
        },

        // Nota: a Integridade de Memória / Core Isolation NÃO se mexe aqui (decisão
        // do dono da app) — é proteção do Windows e fica como o utilizador a tem.
    };

    // ---------- apply/revert ----------

    public static async Task<string?> ApplyAsync(NetTweak t)
    {
        var err = await t.Apply();
        if (err == null && !t.SessionOnly)
        {
            _state.Applied.Add(t.Id);
            _state.BootStamp = CurrentBootStamp();
            Save();
        }
        return err;
    }

    public static async Task<string?> RevertAsync(NetTweak t)
    {
        var err = await t.Revert();
        if (err == null) { _state.Applied.Remove(t.Id); _state.Backup.Remove(t.Id); Save(); }
        return err;
    }

    public static async Task<List<(string name, string error)>> RevertAllAsync()
    {
        var errors = new List<(string, string)>();
        foreach (var t in Tweaks.Reverse())
        {
            if (!t.SessionOnly && !IsApplied(t.Id)) continue;
            var err = await RevertAsync(t);
            if (err != null) errors.Add((t.Name, err));
        }
        return errors;
    }

    // ---------- prioridade do processo (sessão) ----------

    private static CancellationTokenSource? _prioCts;

    private static IEnumerable<Process> GameProcesses()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { yield break; }
        foreach (var p in all)
        {
            var match = false;
            try { match = p.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase); }
            catch { }
            if (match) yield return p;
            else p.Dispose();
        }
    }

    private static string? StartPriorityWatch()
    {
        StopPriorityWatch();
        var cts = new CancellationTokenSource();
        _prioCts = cts;
        // ciclo leve: só corre enquanto o tweak está ligado (regra "featherweight")
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var p in GameProcesses())
                {
                    try
                    {
                        if (p.PriorityClass != ProcessPriorityClass.High)
                            p.PriorityClass = ProcessPriorityClass.High;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                try { await Task.Delay(4000, cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, cts.Token);
        return null;
    }

    private static string? StopPriorityWatch()
    {
        _prioCts?.Cancel();
        _prioCts = null;
        foreach (var p in GameProcesses())
        {
            try
            {
                if (p.PriorityClass == ProcessPriorityClass.High)
                    p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    // ---------- diagnóstico ----------

    private static async Task<string> RunPsAsync(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Procura as causas reais de quebras de FPS que não se resolvem com tweaks:
    /// pouca RAM/ficheiro de paginação, FiveM em disco lento, cache do FiveM
    /// gigante, overlays a comer frames.
    /// Só lê — não altera nada.
    /// </summary>
    public static async Task<List<DiagItem>> DiagnoseAsync()
    {
        var items = new List<DiagItem>();

        // ----- RAM -----
        double ramGb = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in s.Get())
                ramGb = Convert.ToDouble(mo["TotalPhysicalMemory"]) / 1073741824.0;
        }
        catch { }
        if (ramGb > 0)
        {
            items.Add(ramGb < 12
                ? new DiagItem("bad", $"Memória RAM: {ramGb:0} GB",
                    "Servidores pesados de FiveM pedem 16 GB. Com esta RAM as quebras vêm de o Windows andar a despejar memória para o disco — é aqui que ganhas mais.")
                : ramGb < 16
                    ? new DiagItem("warn", $"Memória RAM: {ramGb:0} GB",
                        "Chega para servidores normais, mas nos mais carregados vais notar engasgos. Garante que o ficheiro de paginação está grande.")
                    : new DiagItem("ok", $"Memória RAM: {ramGb:0} GB", "Suficiente para qualquer servidor."));
        }

        // ----- ficheiro de paginação -----
        try
        {
            var managed = false;
            using (var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
            {
                if (k?.GetValue("PagingFiles") is string[] pf)
                    managed = pf.Any(l => l.TrimEnd().EndsWith("0 0"));
            }

            double pageMb = 0;
            using (var s = new ManagementObjectSearcher("SELECT AllocatedBaseSize FROM Win32_PageFileUsage"))
                foreach (ManagementObject mo in s.Get())
                    pageMb += Convert.ToDouble(mo["AllocatedBaseSize"]);

            if (managed)
                items.Add(new DiagItem("ok", "Ficheiro de paginação: gerido pelo Windows",
                    $"Atualmente {pageMb / 1024.0:0.#} GB e cresce sozinho — é o que queres para o FiveM."));
            else if (pageMb < 8192)
                items.Add(new DiagItem("bad", $"Ficheiro de paginação: {pageMb / 1024.0:0.#} GB — pequeno",
                    "O FiveM chega a pedir 20–30 GB de memória em servidores grandes. Fixo e pequeno = crashes e quebras. Põe em \"gerido pelo sistema\" ou fixa 16384–32768 MB no SSD."));
            else
                items.Add(new DiagItem("ok", $"Ficheiro de paginação: {pageMb / 1024.0:0.#} GB", "Tamanho suficiente."));
        }
        catch { }

        // ----- disco onde o FiveM está -----
        try
        {
            var root = FiveMLocator.RootDir;
            if (root == null)
            {
                items.Add(new DiagItem("warn", "FiveM não encontrado",
                    "Não localizei a instalação do FiveM — abre-o uma vez e volta a analisar."));
            }
            else
            {
                var letter = Path.GetPathRoot(root)?.TrimEnd('\\', ':').Replace(":", "") ?? "C";
                var media = await RunPsAsync(
                    $"$p=Get-Partition -DriveLetter {letter} -ErrorAction SilentlyContinue; " +
                    "if($p){ (Get-PhysicalDisk -ErrorAction SilentlyContinue | " +
                    "Where-Object DeviceId -eq $p.DiskNumber).MediaType }");
                media = media.Split('\n').FirstOrDefault()?.Trim() ?? "";

                if (media.Equals("HDD", StringComparison.OrdinalIgnoreCase))
                    items.Add(new DiagItem("bad", $"FiveM está num disco mecânico ({letter}:)",
                        "É a maior causa de quebras ao entrar em zonas novas: o jogo carrega modelos enquanto jogas. Mudar o FiveM para SSD resolve mais do que qualquer tweak."));
                else if (media.Length > 0 && !media.Equals("Unspecified", StringComparison.OrdinalIgnoreCase))
                    items.Add(new DiagItem("ok", $"FiveM em {media} ({letter}:)", "Disco rápido — o streaming do jogo não é o problema."));

                // espaço livre
                try
                {
                    var di = new DriveInfo(letter);
                    var freeGb = di.AvailableFreeSpace / 1073741824.0;
                    var pct = di.TotalSize > 0 ? di.AvailableFreeSpace * 100.0 / di.TotalSize : 100;
                    if (freeGb < 20 || pct < 10)
                        items.Add(new DiagItem("warn", $"Só {freeGb:0} GB livres em {letter}:",
                            "Disco quase cheio trava a cache do jogo e o ficheiro de paginação. Liberta espaço."));
                }
                catch { }

                // cache do FiveM
                try
                {
                    var app = FiveMLocator.AppDataDir;
                    var data = app == null ? null : Path.Combine(app, "data");
                    if (data != null && Directory.Exists(data))
                    {
                        long size = 0;
                        foreach (var d in Directory.GetDirectories(data))
                        {
                            if (Path.GetFileName(d).Equals("game-storage", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                                    try { size += new FileInfo(f).Length; } catch { }
                            }
                            catch { }
                        }
                        var gb = size / 1073741824.0;
                        if (gb > 8)
                            items.Add(new DiagItem("warn", $"Cache do FiveM: {gb:0.#} GB",
                                "Cache muito grande e antiga dá texturas partidas e engasgos. Limpa na aba FiveM (Manutenção) — os teus dados de jogo ficam."));
                        else
                            items.Add(new DiagItem("ok", $"Cache do FiveM: {gb:0.#} GB", "Tamanho normal."));
                    }
                }
                catch { }
            }
        }
        catch { }

        // ----- overlays / programas que roubam frames -----
        try
        {
            var known = new (string proc, string label)[]
            {
                ("RTSS", "RivaTuner (RTSS)"),
                ("MSIAfterburner", "MSI Afterburner"),
                ("wallpaper32", "Wallpaper Engine"),
                ("wallpaper64", "Wallpaper Engine"),
                ("Rainmeter", "Rainmeter"),
                ("obs64", "OBS a gravar/transmitir"),
                ("GameBar", "Xbox Game Bar"),
                ("NVIDIA Overlay", "Overlay da NVIDIA"),
                ("GameOverlayUI", "Overlay do Steam"),
            };
            var running = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var hit = known.FirstOrDefault(k => p.ProcessName.Equals(k.proc, StringComparison.OrdinalIgnoreCase));
                    if (hit.label != null && !running.Contains(hit.label)) running.Add(hit.label);
                }
                catch { }
                finally { p.Dispose(); }
            }
            items.Add(running.Count > 0
                ? new DiagItem("warn", "Programas a roubar frames: " + string.Join(", ", running),
                    "Cada overlay a mais são frames a menos e picos de tempo de frame. Deixa só o que usas mesmo enquanto jogas.")
                : new DiagItem("ok", "Sem overlays pesados a correr", "Nada a competir com o jogo."));
        }
        catch { }

        return items;
    }
}
