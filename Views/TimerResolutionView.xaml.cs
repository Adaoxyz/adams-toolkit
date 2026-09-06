using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class TimerResolutionView : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public TimerResolutionView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); if (AppState.IsVisible) _refresh.Start(); AppState.Changed += OnVisibility; };
        Unloaded += (_, _) => { AppState.Changed -= OnVisibility; _refresh.Stop(); };
    }

    // app por trás do jogo: não vale a pena refrescar o que ninguém vê
    private void OnVisibility(bool visible)
    {
        if (visible) { Refresh(); _refresh.Start(); }
        else _refresh.Stop();
    }

    private void Refresh()
    {
        var (min, max, cur) = TimerResolutionService.Query();
        if (cur < 0)
        {
            CurrentText.Text = MinText.Text = MaxText.Text = "N/D";
            return;
        }
        CurrentText.Text = $"{cur:0.000} ms";
        MinText.Text = $"{min:0.000} ms";
        MaxText.Text = $"{max:0.000} ms";

        // timer "no máximo" = atual encostada à melhor resolução do hardware
        var atMax = cur <= max + 0.01;
        CurrentText.Foreground = atMax
            ? (System.Windows.Media.Brush)FindResource("BrSuccess")
            : (System.Windows.Media.Brush)FindResource("BrText");
        StateText.Text = TimerResolutionService.IsApplied
            ? "⚡ Precisão máxima ATIVA — mantém o Adams Toolkit aberto (bandeja conta). Fechar a app repõe o padrão do Windows."
            : "A precisão máxima fica ativa enquanto o Adams Toolkit estiver aberto (podes minimizar para a bandeja). Fechar a app repõe o padrão do Windows instantaneamente.";
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (!TimerResolutionService.SetMaximum())
            MessageBox.Show("Não foi possível alterar a resolução do timer.", "Timer Resolution",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        TimerResolutionService.RestoreDefault();
        Refresh();
    }
}
