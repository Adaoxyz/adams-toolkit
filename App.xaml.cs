using System.Threading;
using System.Windows;
using AdamsToolkit.Core;

namespace AdamsToolkit;

public partial class App : Application
{
    // Uma só instância por utilizador. Nome com o SID/username para não colidir
    // entre contas Windows diferentes no mesmo PC.
    private static Mutex? _singleton;
    private static readonly string MutexName =
        "AdamsToolkit_singleton_" + Environment.UserName;
    public static readonly string ShowEventName =
        "AdamsToolkit_show_" + Environment.UserName;

    // Lançado pelo arranque do Windows (chave Run) — abre escondido na bandeja.
    public static bool StartMinimized { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Modo headless de segurar o timer: corre o loop e NÃO cria janela WPF.
        // (não conta como "instância" — arg próprio, sai antes do mutex)
        if (e.Args.Length > 0 && e.Args[0] == "--hold-timer")
        {
            TimerHoldHost.Run(e.Args);
            Shutdown();
            return;
        }

        // Limpeza agendada (Agendador de Tarefas): headless, limpa e sai.
        // Também sai antes do mutex — não conta como "instância".
        if (e.Args.Length > 0 && e.Args[0] == "--cleanup")
        {
            ScheduledCleanup.RunHeadless();
            Shutdown();
            return;
        }

        // Serviços do Windows (UAC ou tarefa SYSTEM no arranque): repõe arranque
        // automático + inicia, headless. Também sai antes do mutex.
        // Processos/serviços dispensáveis + svchost (UAC): headless, sai antes do mutex.
        if (e.Args.Length > 0 && e.Args[0] == "--trim-sys")
        {
            ProcessTrimmer.RunHeadless(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--pccheck-fix")
        {
            PcCheck.RunHeadlessFix();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--fix-services")
        {
            ServiceChecker.RunHeadless(e.Args);
            Shutdown();
            return;
        }

        StartMinimized = e.Args.Contains("--startup");

        // Já está aberto? Traz a janela existente para a frente e avisa; não abre 2ª.
        // No arranque do Windows sai em silêncio — nada de popups nem roubar foco.
        _singleton = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            // Processo antigo ainda a fechar (Sair na bandeja: driver LHM, timers,
            // tray) ou auto-update a relançar — o mutex só cai daí a 1-3 s. Espera
            // em vez de mandar o "já está aberto" a quem acabou de fechar a app.
            // Aberto pelo user: 8 s; arranque do Windows: 15 s (ninguém está a ver).
            try { isNew = _singleton.WaitOne(TimeSpan.FromSeconds(StartMinimized ? 15 : 8)); }
            catch (AbandonedMutexException) { isNew = true; }
        }
        if (!isNew)
        {
            if (!StartMinimized)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                MessageBox.Show(
                    "O Adams Toolkit já está aberto.\n\nProcura o ícone na barra de tarefas ou na bandeja (a seta ▲ ao lado do relógio).",
                    "Adams Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // v1.76: a atualização corre AQUI, antes de qualquer escolha — quem vai direto ao
        // editor também fica atualizado (antes só o Toolkit verificava, no seu boot).
        // Arranque com o Windows continua a atualizar no boot do Toolkit (sem cartão).
        if (!StartMinimized)
        {
            var splash = new UpdateSplash();
            try
            {
                splash.Show();
                await ConfigService.LoadAsync();
                var updated = await SelfUpdater.TryUpdateAsync(splash.Set);
                if (updated) { splash.Set("A reiniciar…"); await Task.Delay(300); Shutdown(); return; }
            }
            catch (Exception ex) { StartLog("update: " + ex.Message); }
            finally { try { splash.Close(); } catch { } }
        }

        // v1.71: Editor de Clips integrado. Um .mp4/.mkv por argumento ("Abrir com")
        // vai direto ao editor; à mão, o ecrã de escolha (ou a escolha lembrada) decide.
        // Arranque com o Windows ignora tudo isto e nasce na bandeja como sempre.
        var fileArg = e.Args.FirstOrDefault(a => !a.StartsWith("--") && IsVideoFile(a));
        StartLog($"args=[{string.Join(" ", e.Args)}] startup={StartMinimized} file={fileArg ?? "-"}");
        if (fileArg != null) { OpenEditorStandalone(fileArg); return; }

        if (!StartMinimized)
        {
            // pergunta SEMPRE (pedido 2026-08-29: sem "lembrar"); --startup vai direto ao Toolkit
            var ch = new ChooserWindow();
            ch.ShowDialog();
            StartLog($"chooser={ch.Choice ?? "fechou"}");
            if (ch.Choice == null) { Shutdown(); return; }
            if (ch.Choice == StartMode.Editor) { OpenEditorStandalone(null); return; }
        }

        ShowToolkit();
    }

    private static MainWindow? _toolkit;

    /// <summary>Janela principal: cria (login normal) ou traz para a frente. Nunca há duas.</summary>
    public static void ShowToolkit()
    {
        var app = (App)Current;
        if (_toolkit == null)
        {
            _toolkit = new MainWindow();
            app.MainWindow = _toolkit;
            if (StartMinimized) _toolkit.StartInTray();
            else _toolkit.Show();
        }
        else _toolkit.RestoreFromTray();
    }

    // Editor sem Toolkit por trás: fechar o editor = sair (o Toolkit, se for aberto
    // entretanto pelo botão, passa a mandar — o editor deixa de fechar a app).
    private void OpenEditorStandalone(string? file)
    {
        var w = EditorWindow.Open(file);
        MainWindow = w;
        w.Closed += (_, _) => { if (_toolkit == null) Shutdown(); };
    }

    // %AppData%\AdamsToolkit\start.log — só p/ diagnosticar "abre no sítio errado"
    private static void StartLog(string line)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit");
            System.IO.Directory.CreateDirectory(dir);
            var f = System.IO.Path.Combine(dir, "start.log");
            if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 200_000) System.IO.File.Delete(f);
            System.IO.File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{SelfUpdater.CurrentVersion} {line}\n");
        }
        catch { }
    }

    private static bool IsVideoFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".ts";
        }
        catch { return false; }
    }
}
