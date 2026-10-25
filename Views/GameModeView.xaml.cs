using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class GameModeView : UserControl
{
    public GameModeView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Sync(); GameMode.Changed += OnChanged; };
        Unloaded += (_, _) => GameMode.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.Invoke(Sync);

    private void Sync()
    {
        var o = GameMode.Options;
        OptPause.IsChecked = o.PauseLaunchers;
        OptLower.IsChecked = o.LowerBrowsers;
        OptTrim.IsChecked = o.TrimRam;
        OptHigh.IsChecked = o.FiveMHigh;
        OptAuto.IsChecked = o.AutoOff;

        var on = GameMode.Active;
        StateTitle.Text = on ? "● Ligado" : "Desligado";
        StateTitle.Foreground = (System.Windows.Media.Brush)FindResource(on ? "BrSuccess" : "BrText");
        StateText.Text = on
            ? "Os programas pesados estão fora da frente. Bom jogo!"
            : "Liga antes de entrar no servidor.";
        ToggleBtn.Content = on ? "⏹  Desligar" : "🎮  Ativar Modo Jogo";
        HeroCard.BorderBrush = (System.Windows.Media.Brush)FindResource(on ? "BrAccent" : "BrBorder");
        // opções só mudam com o modo desligado (senão o que já foi mexido ficava a meio)
        OptPause.IsEnabled = OptLower.IsEnabled = OptTrim.IsEnabled = OptHigh.IsEnabled = !on;

        var rows = GameMode.Entries.ToList();
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = on ? "Nenhum programa pesado aberto — nada a tirar da frente." : "Nenhum — liga o Modo Jogo para ver o que sai da frente.";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (GameMode.Active) { GameMode.Disable(); MainWindow.Toast("Modo Jogo desligado — tudo voltou ao normal."); }
        else { GameMode.Enable(); MainWindow.Toast("Modo Jogo ligado."); }
    }

    private void Opt_Click(object sender, RoutedEventArgs e)
    {
        var o = GameMode.Options;
        o.PauseLaunchers = OptPause.IsChecked == true;
        o.LowerBrowsers = OptLower.IsChecked == true;
        o.TrimRam = OptTrim.IsChecked == true;
        o.FiveMHigh = OptHigh.IsChecked == true;
        o.AutoOff = OptAuto.IsChecked == true;
        GameMode.SaveOpts();
    }
}
