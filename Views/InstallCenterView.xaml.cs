using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public class AppCardVM : INotifyPropertyChanged
{
    public AppEntry Entry { get; }

    public AppCardVM(AppEntry entry) => Entry = entry;

    public string Icon => Entry.Icon;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;

    private bool _installed;
    public bool Installed { get => _installed; set { _installed = value; Notify(); Notify(nameof(InstallLabel)); } }

    private bool _downloading;
    public bool Downloading { get => _downloading; set { _downloading = value; Notify(); Notify(nameof(CanInstall)); Notify(nameof(InstallLabel)); } }

    private double _progress;
    public double Progress { get => _progress; set { _progress = value; Notify(); } }

    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; set { _indeterminate = value; Notify(); } }

    private string _statusLabel = "";
    public string StatusLabel { get => _statusLabel; set { _statusLabel = value; Notify(); } }

    public bool CanInstall => !Downloading;

    public string InstallLabel =>
        Downloading ? "A transferir…" :
        Installed ? "Reinstalar" :
        string.IsNullOrEmpty(Entry.DownloadUrl) ? "Obter no site" : "Instalar";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class AppCategoryGroup
{
    public string Category { get; set; } = "";
    public List<AppCardVM> Cards { get; set; } = new();
}

public partial class InstallCenterView : UserControl
{
    private readonly List<AppCardVM> _cards = new();

    public InstallCenterView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_cards.Count == 0) Build(); };
    }

    private void Build()
    {
        _cards.Clear();
        // agrupa por categoria mantendo a ordem de 1ª aparição no catálogo
        var groups = new List<AppCategoryGroup>();
        var byName = new Dictionary<string, AppCategoryGroup>();
        foreach (var app in ConfigService.Current.Apps)
        {
            var card = new AppCardVM(app);
            _cards.Add(card);
            var cat = string.IsNullOrWhiteSpace(app.Category) ? "Outros" : app.Category;
            if (!byName.TryGetValue(cat, out var group))
            {
                group = new AppCategoryGroup { Category = cat };
                byName[cat] = group;
                groups.Add(group);
            }
            group.Cards.Add(card);
        }
        AppsList.ItemsSource = groups;
        RefreshInstalledStates();
        RefreshHistory();
    }

    private void RefreshInstalledStates()
    {
        Task.Run(() =>
        {
            InstallDetector.InvalidateCache();
            foreach (var card in _cards)
            {
                var installed = InstallDetector.IsInstalled(card.Entry);
                Dispatcher.Invoke(() => card.Installed = installed);
            }
        });
    }

    private void RefreshHistory()
    {
        var items = DownloadManager.History.AsEnumerable().Reverse().ToList();
        HistoryList.ItemsSource = items;
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AppCardVM card) return;

        // 1º: instalação silenciosa via winget (pacotes oficiais), se disponível.
        if (!string.IsNullOrEmpty(card.Entry.WingetId) && await WingetInstaller.IsAvailableAsync())
        {
            card.Downloading = true;
            card.Progress = 0;
            card.Indeterminate = true;
            card.StatusLabel = "A instalar via winget…";

            var wingetProgress = new Progress<(double pct, string label)>(p =>
            {
                card.Indeterminate = p.pct < 0;
                if (p.pct >= 0) card.Progress = p.pct;
                card.StatusLabel = p.label;
            });

            var (ok, msg) = await WingetInstaller.InstallAsync(
                card.Entry.WingetId, wingetProgress, CancellationToken.None);

            card.Downloading = false;
            card.Indeterminate = false;
            DownloadManager.AddHistory(card.Entry, $"winget: {card.Entry.WingetId}",
                ok ? "Instalado" : "Falhou (winget)");
            RefreshHistory();

            if (ok)
            {
                card.StatusLabel = msg;
                card.Installed = true;
                InstallDetector.InvalidateCache();
                return;
            }
            card.StatusLabel = $"{msg} — a tentar download direto…";
            // cai para o método clássico abaixo
        }

        // Sem link direto de download → abre a página oficial de download.
        if (string.IsNullOrEmpty(card.Entry.DownloadUrl))
        {
            DownloadManager.OpenUrl(card.Entry.Website);
            return;
        }
        // Alguns "downloadUrl" são páginas (html), não ficheiros — abre no browser.
        if (card.Entry.DownloadUrl.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            card.Entry.DownloadUrl.TrimEnd('/').EndsWith("/download", StringComparison.OrdinalIgnoreCase))
        {
            DownloadManager.OpenUrl(card.Entry.DownloadUrl);
            return;
        }

        card.Downloading = true;
        card.Progress = 0;
        card.StatusLabel = "A ligar…";

        var progress = new Progress<(double pct, string label)>(p =>
        {
            card.Indeterminate = p.pct < 0;
            if (p.pct >= 0) card.Progress = p.pct;
            card.StatusLabel = p.label;
        });

        var path = await DownloadManager.DownloadAsync(card.Entry, progress, CancellationToken.None);

        card.Downloading = false;
        card.Indeterminate = false;
        RefreshHistory();

        if (path == null)
        {
            card.StatusLabel = "Falhou — tenta pelo website oficial.";
            return;
        }

        card.StatusLabel = "Download concluído. A abrir instalador…";
        try { DownloadManager.RunInstaller(path); }
        catch { card.StatusLabel = "Instalador guardado em Downloads."; }
    }

    private void Website_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is AppCardVM card)
            DownloadManager.OpenUrl(card.Entry.Website);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshInstalledStates();

    private void OpenDownloads_Click(object sender, RoutedEventArgs e) =>
        DownloadManager.OpenFolder(DownloadManager.DownloadDir);

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistory();
        HistoryPanel.Visibility = HistoryPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }
}
