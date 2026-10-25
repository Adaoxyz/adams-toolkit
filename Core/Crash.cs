using System.Windows;

namespace AdamsToolkit.Core;

/// <summary>
/// Rede de segurança para exceções que ninguém apanhou.
///
/// Sem isto, qualquer erro num `async void` (há dezenas — todos os cliques das
/// views são assim) fazia a janela desaparecer do ecrã sem aviso, sem log e sem
/// nada que se pudesse pedir ao jogador. "Fechou-se sozinha" não é um bug que se
/// consiga corrigir. Agora: fica no toolkit.log, a app continua de pé, e quem
/// quiser leva os registos num zip para o ticket.
/// </summary>
public static class Crash
{
    /// <summary>Modos headless (--cleanup, --trim-sys…): regista, nunca mostra janelas.</summary>
    public static bool Silent { get; set; }

    private static int _dialogs;
    private static DateTime _firstDialogUtc;

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Crash("interface", e.Exception);
            Report(e.Exception);
            e.Handled = true; // a app fica de pé; o estranho é morrer sem explicação
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "desconhecido");
            Log.Crash(e.IsTerminating ? "fatal" : "fundo", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Crash("task", e.Exception);
            e.SetObserved(); // uma Task esquecida não derruba o processo
        };
    }

    /// <summary>
    /// Mostra o erro uma vez (ou duas), não vinte: um erro que se repete num timer
    /// transformava a app numa metralhadora de caixas de diálogo.
    /// </summary>
    private static void Report(Exception ex)
    {
        if (Silent) return;

        var now = DateTime.UtcNow;
        if (_dialogs == 0 || (now - _firstDialogUtc) > TimeSpan.FromMinutes(5))
        {
            _dialogs = 0;
            _firstDialogUtc = now;
        }
        if (++_dialogs > 2) return; // a partir daqui só o log

        try
        {
            var r = MessageBox.Show(
                "A app apanhou um erro e continua aberta, mas alguma coisa pode não ter ficado feita.\n\n" +
                $"Detalhe: {ex.GetType().Name}: {ex.Message}\n\n" +
                "Queres guardar os registos no Ambiente de trabalho para enviar à staff?",
                "Adams Toolkit — erro", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r == MessageBoxResult.Yes) SaveLogs(showOk: true);
        }
        catch (Exception ex2) { Log.Swallow("mostrar diálogo de erro", ex2); }
    }

    /// <summary>Zip dos registos no Ambiente de trabalho + Explorador aberto por cima dele.</summary>
    public static void SaveLogs(bool showOk)
    {
        var zip = Log.ExportZip();
        if (zip == null)
        {
            if (!Silent)
                MessageBox.Show("Não consegui guardar os registos. Estão em %AppData%\\AdamsToolkit.",
                    "Adams Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Swallow("abrir o Explorador nos registos", ex); }

        if (showOk && !Silent)
            MessageBox.Show($"Registos guardados em:\n{zip}\n\nArrasta este ficheiro para o ticket.",
                "Adams Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
