using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class SettingsView : UserControl
{
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var (code, label) in Lang.Available)
        {
            var rb = new RadioButton
            {
                GroupName = "lang", Content = label, Tag = code,
                IsChecked = code == Lang.Current,
                Style = (Style)FindResource("SegBtn"),
            };
            rb.Checked += (_, _) => { if (!_syncing) Lang.SwitchTo(code); };
            LangGrid.Children.Add(rb);
        }
        Loaded += (_, _) =>
        {
            _syncing = true;
            (ThemeManager.IsLight ? ThemeLight : ThemeDark).IsChecked = true;
            AnimToggle.IsChecked = ParticleField.Enabled;
            StartupToggle.IsChecked = StartupManager.IsEnabled;
            _syncing = false;
        };
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var wantLight = ReferenceEquals(sender, ThemeLight);
        if (wantLight != ThemeManager.IsLight) ThemeManager.Toggle();
    }

    private void Startup_Click(object sender, RoutedEventArgs e) => StartupManager.SetEnabled(StartupToggle.IsChecked == true);

    private void Anim_Click(object sender, RoutedEventArgs e) => ParticleField.Enabled = AnimToggle.IsChecked == true;

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reverter TODOS os ajustes que a app aplicou no Windows?", "Adams Toolkit",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RestoreAllBtn.IsEnabled = false;
        var failed = 0;
        foreach (var t in TweakLibrary.All.Where(t => !t.Id.Contains(':')))
        {
            bool on;
            try { on = t.IsActive(); } catch { on = false; }
            if (!on) continue;
            try { if (await t.Revert() != null) failed++; } catch { failed++; }
        }
        failed += (await GameBoost.RevertAllAsync()).Count;
        failed += (await NetworkOptimizer.RevertAllAsync()).Count;
        RestoreAllBtn.IsEnabled = true;
        MainWindow.Toast(failed == 0 ? "Tudo reposto como estava." : $"Reposto — {failed} ajuste(s) precisam de admin para reverter.");
    }

    private void Discord_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ConfigService.Current.DiscordInvite) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Swallow("abrir Discord", ex); }
    }
}
