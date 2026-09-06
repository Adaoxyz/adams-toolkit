using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// "Estou pronto para um PC check?" — verifica que TUDO o que a staff lê está ativo e intacto
/// (Prefetch, SysMain, Event Log sem limpezas, BAM, Windows Search, Defender, DiagTrack,
/// Recentes, Timeline, USN journal) e que não há ferramentas de limpeza instaladas.
/// Só LÊ. O "Reparar" liga serviços/registry — nunca apaga nada (regra da app).
/// </summary>
public static class PcCheck
{
    public enum Level { Ok, Warn, Bad }
    public sealed record Item(Level Level, string Title, string Detail, bool Fixable);

    public static List<Item> Run()
    {
        var list = new List<Item>();
        var admin = UninstallerEngine.IsAdmin();

        // --- Prefetch ---
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            var pf = (k?.GetValue("EnablePrefetcher") as int?) ?? 3;
            var sf = (k?.GetValue("EnableSuperfetch") as int?) ?? 3;
            int files = -1;
            try { files = Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"), "*.pf").Count(); } catch { }
            if (pf == 0) list.Add(new(Level.Bad, "Prefetch desligado", "EnablePrefetcher=0 — a staff vê isto como esconder que programas correram.", true));
            else if (files == 0) list.Add(new(Level.Bad, "Pasta Prefetch vazia", "Prefetch está ligado mas não há ficheiros .pf — foi limpa há pouco. Não voltes a limpar; enche sozinha com o uso.", false));
            else list.Add(new(Level.Ok, "Prefetch ativo", files > 0 ? $"{files} registos (.pf) presentes." : "ligado (pasta só legível como admin).", false));
            if (sf == 0) list.Add(new(Level.Warn, "Superfetch desligado no registry", "EnableSuperfetch=0.", true));
        }
        catch { }

        // --- serviços que o servidor/staff exigem ---
        var build = Environment.OSVersion.Version.Build;
        foreach (var (key, display, detail) in ServiceChecker.Monitored)
        {
            var (state, _) = ServiceChecker.Query(key);
            var running = state == SvcState.Running;
            // SgrmBroker: a Microsoft desativou-o de fábrica no Win11 22H2+ (build 22621) e removeu-o no 24H2 — não é sinal de nada
            if (!running && key == "SgrmBroker" && (build >= 22621 || state == SvcState.Missing))
            { list.Add(new(Level.Ok, "SgrmBroker desativado pelo Windows", "nesta versão do Windows 11 vem desligado de fábrica — a staff sabe disso.", false)); continue; }
            list.Add(new(running ? Level.Ok : Level.Bad, $"{display} {(running ? "a correr" : "parado")}", detail + (running ? "" : " — precisa de estar ativo."), !running));
        }
        foreach (var (key, name, why) in new[] {
            ("WSearch", "Windows Search", "índice de ficheiros — usado para ver o que existiu no PC"),
            ("WinDefend", "Windows Defender", "histórico de proteção"),
            ("Schedule", "Agendador de Tarefas", "histórico de tarefas"),
        })
        {
            var (state, _) = ServiceChecker.Query(key);
            var running = state == SvcState.Running;
            list.Add(new(running ? Level.Ok : Level.Warn, $"{name} {(running ? "a correr" : "parado")}", why + (running ? "." : " — liga-o antes do check."), !running));
        }

        // --- Event Log limpo recentemente? (1102 Security / 104 System+Application) ---
        try
        {
            var cleared = new List<string>();
            foreach (var (log, id) in new[] { ("Security", 1102), ("System", 104), ("Application", 104) })
            {
                try
                {
                    var q = new EventLogQuery(log, PathType.LogName, $"*[System[(EventID={id}) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
                    using var r = new EventLogReader(q);
                    var ev = r.ReadEvent();
                    if (ev != null) cleared.Add($"{log} ({ev.TimeCreated:dd/MM HH:mm})");
                }
                catch (UnauthorizedAccessException) { if (log == "Security" && !admin) { /* sem admin não dá para ler Security */ } }
                catch { }
            }
            if (cleared.Count > 0) list.Add(new(Level.Bad, "Registos de eventos limpos nos últimos 30 dias", string.Join(", ", cleared) + " — a staff vê a limpeza (evento 1102/104). Não há como desfazer; não repitas.", false));
            else list.Add(new(Level.Ok, "Registos de eventos intactos", admin ? "sem limpezas nos últimos 30 dias (Security, System, Application)." : "sem limpezas em System/Application (Security só como admin).", false));
        }
        catch { }

        // --- BAM (Background Activity Moderator) ---
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings");
            var n = k?.GetSubKeyNames().Length ?? 0;
            var (st, _) = ServiceChecker.Query("bam");
            if (k == null) list.Add(new(Level.Bad, "BAM sem registos", "chave bam\\State\\UserSettings não existe — foi apagada ou o driver bam está desativado.", false));
            else list.Add(new(Level.Ok, "BAM ativo", $"{n} utilizador(es) com registos.", false));
        }
        catch { }

        // --- Recentes / Timeline / PowerShell ---
        try
        {
            var recent = Directory.EnumerateFileSystemEntries(Environment.GetFolderPath(Environment.SpecialFolder.Recent)).Count();
            list.Add(new(recent > 0 ? Level.Ok : Level.Warn, recent > 0 ? "Itens recentes presentes" : "Itens recentes vazios", recent > 0 ? $"{recent} entradas." : "pasta Recent vazia — parece limpa.", false));
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
            var pua = k?.GetValue("PublishUserActivities") as int?;
            if (pua == 0) list.Add(new(Level.Warn, "Histórico de atividade desligado por política", "PublishUserActivities=0.", true));
            else list.Add(new(Level.Ok, "Histórico de atividade ativo", "sem política a bloquear.", false));
        }
        catch { }

        // --- USN journal (só admin) ---
        if (admin)
        {
            try
            {
                var psi = new ProcessStartInfo("fsutil", "usn queryjournal C:") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                // só o exit code (o texto vem na língua do Windows); ≠0 = journal inexistente/apagado
                if (p.ExitCode == 0) list.Add(new(Level.Ok, "USN journal ativo (C:)", "registo de alterações de ficheiros presente.", false));
                else list.Add(new(Level.Bad, "USN journal desativado (C:)", "foi apagado ou nunca existiu — «Reparar» cria-o de novo (fsutil usn createjournal). O histórico anterior não volta.", true));
            }
            catch { }
        }
        else list.Add(new(Level.Warn, "USN journal não verificado", "abre a app como admin para verificar.", false));

        // --- ferramentas de limpeza instaladas ---
        try
        {
            var found = new List<string>();
            var names = new[] { "CCleaner", "BleachBit", "PrivaZer", "Wise Disk Cleaner", "Privacy Eraser", "Glary", "Advanced SystemCare", "Eraser", "Disk Wipe", "Prefetch Cleaner", "USN Journal", "Event Log Explorer" };
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using var k = root.OpenSubKey(sub); if (k == null) continue;
                    foreach (var s in k.GetSubKeyNames())
                    {
                        using var e = k.OpenSubKey(s); var dn = e?.GetValue("DisplayName") as string; if (dn == null) continue;
                        if (names.Any(n => dn.Contains(n, StringComparison.OrdinalIgnoreCase)) && !found.Contains(dn)) found.Add(dn);
                    }
                }
            if (found.Count > 0) list.Add(new(Level.Warn, "Ferramentas de limpeza instaladas", string.Join(", ", found) + " — a staff pergunta porquê. Desinstala antes do check e não limpes nada.", false));
            else list.Add(new(Level.Ok, "Sem ferramentas de limpeza", "nenhum limpador conhecido instalado.", false));
        }
        catch { }

        return list;
    }

    // ---------- reparação ----------

    /// <summary>Relatório da última reparação (uma linha por item). Escrito pela instância elevada.</summary>
    private static string FixLogPath => Path.Combine(ConfigService.DataDir, "pccheck-fix.txt");

    /// <summary>Apaga o relatório antigo (antes de uma nova reparação).</summary>
    public static void ClearFixReport()
    {
        try { File.Delete(FixLogPath); } catch { }
    }

    /// <summary>Lê o relatório da última reparação (vazio se nunca correu).</summary>
    public static List<string> ReadFixReport()
    {
        try { return File.ReadAllLines(FixLogPath).Where(l => l.Length > 0).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Repara o que é reparável: serviços + registry do Prefetch/Timeline + USN journal. Elevado.
    /// Escreve um relatório em <see cref="FixLogPath"/> — sem isto a reparação falhava em
    /// silêncio e o utilizador via a mesma lista de problemas sem perceber porquê.
    /// </summary>
    public static void RunHeadlessFix()
    {
        var log = new List<string>();
        void Ok(string what) => log.Add("OK|" + what);
        void Fail(string what, string why) => log.Add("FALHA|" + what + "|" + why);

        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            k.SetValue("EnablePrefetcher", 3, RegistryValueKind.DWord);
            k.SetValue("EnableSuperfetch", 3, RegistryValueKind.DWord);
            Ok("Prefetch/Superfetch ligados");
        }
        catch (Exception ex) { Fail("Prefetch", ex.Message); }

        try
        {
            // liga mesmo o histórico de atividade (Timeline): 1 explícito passa o check e não fica no default incerto
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
            k.SetValue("PublishUserActivities", 1, RegistryValueKind.DWord);
            k.SetValue("UploadUserActivities", 1, RegistryValueKind.DWord);
            Ok("Histórico de atividade ligado");
        }
        catch (Exception ex) { Fail("Histórico de atividade", ex.Message); }

        // USN journal em C: (idempotente: se já existir, o fsutil só ajusta tamanhos)
        try
        {
            var psi = new ProcessStartInfo("fsutil", "usn createjournal m=1000000000 a=100000000 C:")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p != null)
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode == 0) Ok("USN journal (C:)"); else Fail("USN journal (C:)", "fsutil devolveu " + p.ExitCode);
            }
        }
        catch (Exception ex) { Fail("USN journal (C:)", ex.Message); }

        // Defender: quando está parado é quase sempre uma POLÍTICA a desligá-lo
        // (DisableAntiSpyware & companhia) ou um antivírus de terceiros. O serviço
        // é protegido — `sc start WinDefend` dá "Acesso negado" mesmo elevado —
        // por isso primeiro tiram-se as políticas e só depois se tenta arrancar.
        try
        {
            var removed = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender", writable: true))
                foreach (var v in new[] { "DisableAntiSpyware", "DisableAntiVirus" })
                    if (k?.GetValue(v) != null) { k.DeleteValue(v, false); removed++; }
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", writable: true))
                foreach (var v in new[] { "DisableRealtimeMonitoring", "DisableBehaviorMonitoring", "DisableOnAccessProtection", "DisableScanOnRealtimeEnable" })
                    if (k?.GetValue(v) != null) { k.DeleteValue(v, false); removed++; }
            if (removed > 0) Ok($"Políticas que desligavam o Defender removidas ({removed})");
        }
        catch (Exception ex) { Fail("Políticas do Defender", ex.Message); }

        // serviços: sc config start= auto (passa pelo SCM) + arranque, com motivo em caso de falha
        foreach (var (svc, label) in new[]
        {
            ("WSearch",    "Windows Search"),
            ("WinDefend",  "Windows Defender"),
            ("Schedule",   "Agendador de Tarefas"),
            ("bam",        "BAM"),
            ("SgrmBroker", "SgrmBroker"),
        })
        {
            var (state, realName) = ServiceChecker.Query(svc);
            if (state == SvcState.Missing) { Fail(label, "serviço não existe nesta versão do Windows"); continue; }
            if (state == SvcState.Running) { Ok(label + " já estava a correr"); continue; }

            ServiceChecker.ScConfigAuto(svc);
            var why = ServiceChecker.StartWithReason(realName);
            if (why == null) { Ok(label + " ligado"); continue; }

            // o Defender é processo protegido: nem elevado o SCM deixa arrancar
            if (svc == "WinDefend")
                Fail(label, "o Windows bloqueia o arranque manual do Defender. Desliga a Proteção contra Adulteração (Segurança do Windows › Proteção contra vírus › Gerir definições), desinstala o antivírus de terceiros e reinicia o PC.");
            else
                Fail(label, why);
        }

        ServiceChecker.RunHeadless(new[] { "--fix-services" });

        try { File.WriteAllLines(FixLogPath, log); } catch { }
    }
}
