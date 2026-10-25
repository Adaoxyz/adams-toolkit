using System.Diagnostics;
using System.Windows;

namespace AdamsToolkit.Core;

/// <summary>
/// Desliga e repõe a interface do Windows (explorer.exe): ambiente de trabalho,
/// barra de tarefas, bandeja e as janelas do Explorador de Ficheiros.
///
/// Porquê: o explorer.exe e tudo o que ele carrega (ícones do ambiente de
/// trabalho, miniaturas, barra de tarefas, widgets, ícones da bandeja) continuam
/// a desenhar e a acordar a GPU enquanto jogas. Com ele desligado sobra RAM e o
/// compositor tem menos uma janela para compor — em PCs fracos dá alguns FPS e,
/// sobretudo, menos picos de tempo de frame.
///
/// PORQUÊ O VIGIA: matar o explorer.exe não chega. O Winlogon tem
/// AutoRestartShell=1 de origem e volta a abrir a shell 1–2 segundos depois —
/// a interface "piscava" e voltava sozinha (relato do dono, 20-09-2026). A saída
/// óbvia seria pôr esse valor a 0 no registo, mas isso é uma alteração
/// PERMANENTE ao sistema, precisa de administrador e, se a app morresse a meio,
/// ficava lá. Em vez disso há um ciclo leve que volta a fechar a shell sempre que
/// o Winlogon a reabre — enquanto o utilizador não carregar em "Repor interface".
///
/// REGRA DESTE MÓDULO: não altera nada no registo nem no disco. É só parar e
/// voltar a arrancar um processo — qualquer reinício do PC repõe tudo sozinho.
/// O jogo, o Discord e esta app não são afetados: só morre a interface.
///
/// Rede de segurança (o utilizador NUNCA pode ficar sem forma de voltar atrás):
///  • a app repõe o explorer sozinha ao fechar (<see cref="InstallExitGuard"/>);
///  • com a interface desligada a janela não se esconde para a bandeja — a
///    bandeja não existe, ficaria inalcançável (ver MainWindow.HideToTray).
/// </summary>
public static class ExplorerControl
{
    /// <summary>Intervalo do vigia. Curto o bastante para a shell não chegar a desenhar.</summary>
    private const int WatchMs = 500;

    private static CancellationTokenSource? _watchCts;

    /// <summary>Fomos nós que desligámos — só então repomos automaticamente.</summary>
    public static bool TurnedOffByUs => _watchCts != null;

    /// <summary>Há interface do Windows a correr neste momento?</summary>
    public static bool IsRunning
    {
        get
        {
            try { return Shells().Count > 0; }
            catch { return true; } // na dúvida assume que sim — não bloqueia a UI
        }
    }

    /// <summary>Processos explorer.exe desta sessão (os do nosso utilizador).</summary>
    private static List<Process> Shells()
    {
        var list = new List<Process>();
        Process[] all;
        try { all = Process.GetProcessesByName("explorer"); }
        catch { return list; }

        var mine = Process.GetCurrentProcess().SessionId;
        foreach (var p in all)
        {
            var keep = false;
            try { keep = p.SessionId == mine; }
            catch { }
            if (keep) list.Add(p);
            else p.Dispose();
        }
        return list;
    }

    /// <summary>Fecha todas as shells desta sessão. Devolve quantas apanhou.</summary>
    private static int KillShells()
    {
        var shells = Shells();
        foreach (var p in shells)
        {
            try { p.Kill(); } catch (Exception ex) { Log.Swallow("fechar explorer.exe", ex); }
        }
        foreach (var p in shells)
        {
            try { p.WaitForExit(3000); } catch { }
            try { p.Dispose(); } catch { }
        }
        return shells.Count;
    }

    // ---------- desligar ----------

    /// <summary>
    /// Fecha a interface e MANTÉM-NA fechada até <see cref="TurnOnAsync"/>.
    /// Devolve null se correu bem, ou a mensagem de erro.
    /// </summary>
    public static async Task<string?> TurnOffAsync()
    {
        // o vigia arranca ANTES do primeiro kill: se o Winlogon for rápido a
        // reabrir, já está lá quem volte a fechar.
        StartWatch();

        await Task.Run(KillShells);

        // confirma que fica mesmo fechada (o vigia tem de aguentar o Winlogon)
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(WatchMs);
            if (!IsRunning) continue;
            // ainda viva à 6ª ronda = o vigia não está a dar conta
            if (i == 5)
            {
                StopWatch();
                return "não consigo manter a interface fechada — o Windows reabre-a mais depressa do que eu a fecho";
            }
        }

        Log.Info("interface do Windows desligada (vigia ativo)", "explorer");
        return null;
    }

    private static void StartWatch()
    {
        StopWatch();
        var cts = new CancellationTokenSource();
        _watchCts = cts;
        // ciclo leve, no mesmo espírito do GameBoost: só corre enquanto está ligado.
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { if (IsRunning) KillShells(); }
                catch (Exception ex) { Log.Swallow("vigia do explorer", ex); }
                try { await Task.Delay(WatchMs, cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, cts.Token);
    }

    private static void StopWatch()
    {
        var cts = _watchCts;
        _watchCts = null;
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }

    // ---------- repor ----------

    /// <summary>Volta a abrir a interface. Devolve null se correu bem.</summary>
    public static async Task<string?> TurnOnAsync()
    {
        StopWatch(); // PRIMEIRO: senão o vigia mata a shell que vamos abrir
        await Task.Delay(WatchMs); // deixa a última ronda do vigia terminar

        if (!IsRunning)
        {
            try
            {
                // o explorer.exe arranca sempre na sessão/integridade do utilizador,
                // mesmo lançado por uma app elevada — é o comportamento da shell.
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("repor a interface do Windows falhou", ex);
                return "não consegui abrir o explorer.exe: " + ex.Message
                     + " — faz Ctrl+Shift+Esc → Ficheiro → Executar nova tarefa → explorer.exe";
            }
        }

        // a barra de tarefas demora 1–3 s a desenhar; confirma antes de dizer que está feito
        for (var i = 0; i < 20 && !IsRunning; i++) await Task.Delay(250);

        Log.Info("interface do Windows reposta", "explorer");
        return IsRunning
            ? null
            : "pedi ao Windows para abrir a interface mas ela ainda não apareceu — espera uns segundos";
    }

    /// <summary>
    /// Repõe a interface SEM esperar, para caminhos de saída da app. Fechar o
    /// Adams Toolkit nunca pode deixar o PC sem barra de tarefas.
    /// </summary>
    public static void RestoreIfNeeded()
    {
        if (_watchCts == null) return;
        StopWatch();
        try
        {
            // dá tempo à última ronda do vigia de morrer antes de abrir a shell
            Thread.Sleep(WatchMs);
            if (!IsRunning)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            Log.Info("interface reposta à saída da app", "explorer");
        }
        catch (Exception ex) { Log.Error("repor a interface à saída falhou", ex); }
    }

    /// <summary>Liga a rede de segurança à saída da aplicação. Chamar uma vez, no arranque.</summary>
    public static void InstallExitGuard()
    {
        try
        {
            Application.Current.Exit += (_, _) => RestoreIfNeeded();
            // sessão do Windows a terminar (encerrar/reiniciar/logoff)
            Application.Current.SessionEnding += (_, _) => RestoreIfNeeded();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreIfNeeded();
        }
        catch (Exception ex) { Log.Swallow("instalar a rede de segurança do explorer", ex); }
    }
}
