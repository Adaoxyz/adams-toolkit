using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

/// <summary>Sobre: versão + Novidades e Código aberto (que antes eram itens da barra lateral).</summary>
public partial class AboutView : UserControl
{
    private ChangelogView? _changelog;
    private OpenSourceView? _openSource;

    public AboutView()
    {
        InitializeComponent();
        NameText.Text = "Adams Toolkit";
        VerText.Text = $"v{SelfUpdater.CurrentVersion} · ATL PVP";
        Show();
    }

    public void SelectTab(string tab)
    {
        if (tab == "opensource") TabOpenSource.IsChecked = true;
        else TabChangelog.IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e) { if (TabHost != null) Show(); }

    private void Show() =>
        TabHost.Content = TabOpenSource.IsChecked == true
            ? _openSource ??= new OpenSourceView()
            : _changelog ??= new ChangelogView();
}
