using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class DiagnosticoView : UserControl
{
    private bool _ran;

    public DiagnosticoView()
    {
        InitializeComponent();
        // 1ª visita analisa sozinho; depois só quando o jogador pede (PowerShell/WMI pesam)
        Loaded += async (_, _) => { if (!_ran) await RunAsync(); };
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        _ran = true;
        RunBtn.IsEnabled = false;
        StatusText.Text = "A analisar o PC…";
        try
        {
            var items = await PcDiagnosis.RunAsync();
            List.ItemsSource = items.Select(i => new DiagVM(i)).ToList();
            var bad = items.Count(i => i.Level == "bad");
            var warn = items.Count(i => i.Level == "warn");
            StatusText.Text = $"{items.Count} verificações · {bad} graves · {warn} avisos";

            var worst = items.FirstOrDefault();
            VerdictCard.Visibility = Visibility.Visible;
            if (worst == null || worst.Level == "ok")
            {
                VerdictIcon.Text = "✅";
                VerdictTitle.Text = "Nada de grave a travar o teu PC";
                VerdictText.Text = "Se ainda tens quebras, o mais provável é o servidor ou a ligação — vê o ping na aba Rede.";
                GameModeBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                VerdictIcon.Text = worst.Level == "bad" ? "⛔" : "⚠️";
                VerdictTitle.Text = $"O que mais te trava: {worst.Title}";
                VerdictText.Text = worst.Detail;
                // RAM/CPU/arranque/overlays: o Modo Jogo é a resposta direta
                GameModeBtn.Visibility = worst.Title.StartsWith("RAM") || worst.Title.StartsWith("Processador") ||
                                         worst.Title.StartsWith("Memória RAM") || worst.Title.Contains("programas")
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível analisar: " + ex.Message;
        }
        finally { RunBtn.IsEnabled = true; }
    }

    private void GameMode_Click(object sender, RoutedEventArgs e) =>
        (Application.Current.MainWindow as MainWindow)?.NavigateTo("gamemode");
}
