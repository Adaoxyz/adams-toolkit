using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public class ProgramVM : INotifyPropertyChanged
{
    public InstalledProgram P { get; }
    public ProgramVM(InstalledProgram p) => P = p;

    // ícone real da app, extraído em background (frozen → seguro cross-thread)
    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => P.DisplayName;
    public string Publisher => P.Publisher.Length > 0 ? P.Publisher : "—";
    public string Version => P.Version;
    public string SizeLabel => P.EstimatedSizeKb > 0 ? UninstallerEngine.FormatSize(P.EstimatedSizeKb * 1024) : "—";
    public string DateLabel =>
        P.InstallDate.Length == 8 &&
        DateTime.TryParseExact(P.InstallDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy") : "—";
}

public class AppxVM : INotifyPropertyChanged
{
    public AppxPackage P { get; }
    public AppxVM(AppxPackage p) => P = p;

    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Notify(); }
    }
    private long _size;
    public long Size { get => _size; set { _size = value; Notify(); Notify(nameof(SizeLabel)); } }

    public string Name => P.DisplayName;
    public string Publisher => P.Publisher.Length > 0 ? P.Publisher : P.Name;
    public string Version => P.Version;
    public string SizeLabel => Size > 0 ? UninstallerEngine.FormatSize(Size) : "—";
    public bool Locked => false; // lista já vem filtrada: só removíveis
    public string KindLabel => P.SignatureKind.Equals("Store", StringComparison.OrdinalIgnoreCase) ? "Store" : "App";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class DriveVM
{
    public string Root { get; init; } = "";
    public string Label { get; init; } = "";
    public double UsedPct { get; init; }
    public string FreeLabel { get; init; } = "";
}

public class CrumbVM
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool HasNext { get; init; }
}

public class StorageVM
{
    public StorageEntry E { get; }
    private readonly long _parentTotal;
    public StorageVM(StorageEntry e, long parentTotal) { E = e; _parentTotal = parentTotal; }
    public string Name => E.Name;
    public string Icon => E.IsDir ? "📁" : "📄";
    public string Chevron => E.IsDir ? "❯" : "";
    public string SizeLabel => UninstallerEngine.FormatSize(E.Bytes);
    public double Pct => _parentTotal > 0 ? (double)E.Bytes / _parentTotal : 0;
    public string PctLabel => $"{Pct * 100:0.#}%";
    public double BarWidth => Math.Max(2, 200 * Pct);
    public string Detail => E.IsDir
        ? $"{E.Files:N0} ficheiros" + (E.Denied ? " · ⚠ parte sem permissão" : "")
        : System.IO.Path.GetExtension(E.Name).TrimStart('.').ToUpperInvariant() + " ficheiro";
}

public class LeftoverVM : INotifyPropertyChanged
{
    public LeftoverItem Item { get; }
    public LeftoverVM(LeftoverItem item) => Item = item;

    public string Display => Item.Display;
    public string SizeLabel => Item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue
        ? "" : UninstallerEngine.FormatSize(Item.SizeBytes);
    public string KindIcon => Item.Kind switch
    {
        LeftoverKind.Directory => "📁",
        LeftoverKind.File => "📄",
        LeftoverKind.Shortcut => "🔗",
        LeftoverKind.RegistryKey => "🧾",
        LeftoverKind.RegistryValue => "▶️",
        _ => "❔",
    };

    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; Notify(); } }

    private string _error = "";
    public string Error { get => _error; set { _error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class UninstallerView : UserControl
{
    private List<ProgramVM> _all = new();
    private List<AppxVM> _appx = new();
    private bool _appxLoaded;
    private CancellationTokenSource? _appxCts;
    private List<LeftoverVM> _leftovers = new();
    private bool _busy;
    private CancellationTokenSource? _iconCts;

    public UninstallerView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_all.Count == 0) _ = LoadAsync(); };
    }

    private ProgramVM? Selected => ProgramsList.SelectedItem as ProgramVM;
    private AppxVM? SelectedAppx => AppxList.SelectedItem as AppxVM;
    private bool AppxMode => TabAppx?.IsChecked == true;
    private bool StorageMode => TabStorage?.IsChecked == true;

    // ---------- armazenamento ----------

    private string _storagePath = "";
    private readonly Stack<string> _storageHistory = new();
    private CancellationTokenSource? _storageCts;
    private List<StorageVM> _storage = new();

    private void LoadDrives()
    {
        DriveList.ItemsSource = StorageScanner.Drives().Select(d => new DriveVM
        {
            Root = d.root, Label = d.label,
            UsedPct = d.total > 0 ? 100.0 * (d.total - d.free) / d.total : 0,
            FreeLabel = $"{UninstallerEngine.FormatSize(d.free)} livres de {UninstallerEngine.FormatSize(d.total)}",
        }).ToList();
    }

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string root) _ = ScanStorageAsync(root);
    }

    private async Task ScanStorageAsync(string path, bool pushHistory = true)
    {
        _storageCts?.Cancel();
        var cts = _storageCts = new CancellationTokenSource();
        if (pushHistory && _storagePath.Length > 0 && !string.Equals(_storagePath, path, StringComparison.OrdinalIgnoreCase))
            _storageHistory.Push(_storagePath);
        StorageBackBtn.IsEnabled = _storageHistory.Count > 0;
        _storagePath = path;
        StoragePathText.Visibility = Visibility.Collapsed;
        BuildBreadcrumb(path);
        StorageStateText.Text = "A calcular tamanhos… (a primeira vez pode demorar)";
        StorageUpBtn.IsEnabled = Directory.GetParent(path) != null;
        StorageOpenBtn.IsEnabled = true;
        StorageList.ItemsSource = null;
        var prog = new Progress<int>(p => { if (!cts.IsCancellationRequested) StorageStateText.Text = $"A calcular… {p}%"; });
        List<StorageEntry> entries;
        try { entries = await StorageScanner.ScanAsync(path, cts.Token, prog); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        var total = entries.Sum(x => x.Bytes);
        _storage = entries.Select(x => new StorageVM(x, total)).ToList();
        StorageList.ItemsSource = _storage;
        StorageStateText.Text = $"{UninstallerEngine.FormatSize(total)} em {entries.Count(x => x.IsDir)} pastas e {entries.Count(x => !x.IsDir)} ficheiros — clica numa pasta para entrar, ⬆ Voltar para subir";
        EmptyText.Visibility = Visibility.Collapsed;
    }

    private void Storage_Selected(object sender, SelectionChangedEventArgs e)
    {
        var vm = StorageList.SelectedItem as StorageVM;
        if (vm == null) { ActionBar.Visibility = Visibility.Collapsed; return; }
        if (vm.E.IsDir)
        {
            // 1 clique numa pasta = entrar (como um explorador de espaço)
            ActionBar.Visibility = Visibility.Collapsed;
            _ = ScanStorageAsync(vm.E.Path);
            return;
        }
        ActionBar.Visibility = Visibility.Visible;
        SelectedName.Text = vm.Name + "  ·  " + vm.SizeLabel;
        StatusText.Text = vm.E.Path;
        UninstallBtn.Content = "📁  Mostrar no Explorador";
        UninstallBtn.IsEnabled = true;
    }

    private void Storage_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (StorageList.SelectedItem is StorageVM { E.IsDir: true } vm) _ = ScanStorageAsync(vm.E.Path);
    }

    private void StorageAction()
    {
        if (StorageList.SelectedItem is not StorageVM vm) return;
        if (vm.E.IsDir) _ = ScanStorageAsync(vm.E.Path);
        else try { Process.Start("explorer.exe", $"/select,\"{vm.E.Path}\""); } catch { }
    }

    private void BuildBreadcrumb(string path)
    {
        var parts = new List<CrumbVM>();
        var root = System.IO.Path.GetPathRoot(path) ?? path;
        var rel = path.Length > root.Length ? path[root.Length..].Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var cur = root;
        parts.Add(new CrumbVM { Name = root.TrimEnd('\\'), Path = root, HasNext = rel.Length > 0 });
        for (var i = 0; i < rel.Length; i++)
        {
            cur = System.IO.Path.Combine(cur, rel[i]);
            parts.Add(new CrumbVM { Name = rel[i], Path = cur, HasNext = i < rel.Length - 1 });
        }
        Breadcrumb.ItemsSource = parts;
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string p && !string.Equals(p, _storagePath, StringComparison.OrdinalIgnoreCase))
            _ = ScanStorageAsync(p);
    }

    private void StorageBack_Click(object sender, RoutedEventArgs e)
    {
        if (_storageHistory.Count == 0) return;
        var prev = _storageHistory.Pop();
        _ = ScanStorageAsync(prev, pushHistory: false);
    }

    private void StorageUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_storagePath);
        if (parent != null) _ = ScanStorageAsync(parent.FullName);
    }

    private void StorageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_storagePath.Length > 0) try { Process.Start("explorer.exe", $"\"{_storagePath}\""); } catch { }
    }

    // ---------- separadores ----------

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (ProgramsList == null || AppxList == null) return;
        var appx = AppxMode; var storage = StorageMode;
        ProgramsList.Visibility = !appx && !storage ? Visibility.Visible : Visibility.Collapsed;
        AppxList.Visibility = appx ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
        ColHeader.Visibility = storage ? Visibility.Collapsed : Visibility.Visible;
        SearchInput.IsEnabled = !storage;
        StorageList.SelectedItem = null;
        ActionBar.Visibility = Visibility.Collapsed;
        ProgramsList.SelectedItem = null; AppxList.SelectedItem = null;
        LeftoversPanel.Visibility = Visibility.Collapsed;
        SearchInput.ToolTip = appx ? "Pesquisar apps do Windows" : "Pesquisar programas";
        ColLast.Text = appx ? "TIPO" : "INSTALADO";
        if (storage)
        {
            AdminBanner.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Vê o que ocupa mais espaço no disco — pastas maiores primeiro. Só lê, não apaga nada.";
            if (DriveList.ItemsSource == null) LoadDrives();
            if (_storagePath.Length > 0) StoragePathText.Text = _storagePath;
            return;
        }
        if (appx)
        {
            AdminBanner.Visibility = Visibility.Collapsed;
            if (!_appxLoaded) _ = LoadAppxAsync(); else ApplyFilter();
        }
        else
        {
            AdminBanner.Visibility = UninstallerEngine.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilter();
            SubtitleText.Text = ProgramsSubtitle();
        }
    }

    private string ProgramsSubtitle()
    {
        var totalKb = _all.Sum(v => v.P.EstimatedSizeKb);
        return $"{_all.Count} programas instalados • {UninstallerEngine.FormatSize(totalKb * 1024)} " +
               "• programas instalados por fora (setup .exe/.msi) — remove e caça o que deixam para trás";
    }

    // ---------- apps do Windows (Store / Appx) ----------

    private async Task LoadAppxAsync()
    {
        SubtitleText.Text = "A carregar apps do Windows… (PowerShell)";
        EmptyText.Visibility = Visibility.Collapsed;
        var pk = await AppxUninstaller.EnumerateAsync();
        _appx = pk.Select(p => new AppxVM(p)).ToList();
        _appxLoaded = true;
        ApplyFilter();
        SubtitleText.Text = AppxSubtitle();
        LoadAppxExtrasInBackground();
    }

    private string AppxSubtitle() => _appx.Count == 0
        ? "Sem apps do Windows removíveis (ou PowerShell bloqueado)."
        : $"{_appx.Count} apps do Windows removíveis • Store e pré-instaladas (Xbox, Cortana, Clipchamp, Teams…) — o que faz parte do sistema não aparece";

    private void LoadAppxExtrasInBackground()
    {
        _appxCts?.Cancel();
        var cts = _appxCts = new CancellationTokenSource();
        var snapshot = _appx.ToList();
        Task.Run(() =>
        {
            foreach (var vm in snapshot)
            {
                if (cts.Token.IsCancellationRequested) return;
                var icon = AppxUninstaller.LoadLogo(vm.P);
                long size = 0;
                try { if (vm.P.InstallLocation.Length > 0) size = UninstallerEngine.DirSize(vm.P.InstallLocation); } catch { }
                if (cts.Token.IsCancellationRequested) return;
                Dispatcher.Invoke(() => { if (icon != null) vm.Icon = icon; vm.Size = size; });
            }
        }, cts.Token);
    }

    private void Appx_Selected(object sender, SelectionChangedEventArgs e)
    {
        var vm = SelectedAppx;
        ActionBar.Visibility = vm == null ? Visibility.Collapsed : Visibility.Visible;
        if (vm == null) return;
        SelectedName.Text = vm.Name;
        StatusText.Text = vm.Locked
            ? "Faz parte do Windows — não pode ser removida (o próprio Windows recusa)."
            : vm.P.PackageFullName;
        UninstallBtn.Content = "🧹  Remover app";
        UninstallBtn.IsEnabled = !vm.Locked;
    }

    private async Task RemoveAppxFlowAsync()
    {
        if (_busy || SelectedAppx is not { } vm || vm.Locked) return;
        if (MessageBox.Show(
                $"Remover \"{vm.Name}\"?\n\nA app desaparece do menu Iniciar. Se o Windows a tiver instalado para todos os utilizadores, vai pedir administrador (UAC). Podes voltar a instalá-la pela Microsoft Store.",
                "Remover app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _busy = true;
        UninstallBtn.IsEnabled = false;
        try
        {
            StatusText.Text = "A remover…";
            var err = await AppxUninstaller.RemoveAsync(vm.P);

            // fonte de verdade = o Windows: re-lista e vê se o pacote ainda lá está
            StatusText.Text = "A confirmar…";
            var fresh = await AppxUninstaller.EnumerateAsync();
            var still = fresh.Any(p => p.PackageFullName == vm.P.PackageFullName ||
                                       p.PackageFamilyName == vm.P.PackageFamilyName);
            _appx = fresh.Select(p => new AppxVM(p)).ToList();
            ApplyFilter();
            LoadAppxExtrasInBackground();
            ActionBar.Visibility = Visibility.Collapsed;
            AppxList.SelectedItem = null;

            if (!still)
            {
                SubtitleText.Text = $"✅ {vm.Name} removida • " + AppxSubtitle();
                return;
            }
            var msg = err ?? "O Windows não removeu o pacote.";
            SubtitleText.Text = $"✗ {vm.Name}: {msg}";
            MessageBox.Show($"{vm.Name} continua instalada.\n\n{msg}\n\nDetalhes em %AppData%\\AdamsToolkit\\appx.log",
                "Não foi possível remover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
    }

    // ---------- carregar / filtrar ----------

    private async Task LoadAsync()
    {
        SubtitleText.Text = "A carregar programas…";
        var programs = await Task.Run(UninstallerEngine.Enumerate);
        _all = programs.Select(p => new ProgramVM(p)).ToList();
        ApplyFilter();

        if (!AppxMode)
        {
            SubtitleText.Text = ProgramsSubtitle();
            AdminBanner.Visibility = UninstallerEngine.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
        }
        LoadIconsInBackground();
    }

    /// <summary>Extrai os ícones reais em background, um a um, sem bloquear a lista.</summary>
    private void LoadIconsInBackground()
    {
        _iconCts?.Cancel();
        var cts = _iconCts = new CancellationTokenSource();
        var snapshot = _all.ToList();
        Task.Run(() =>
        {
            foreach (var vm in snapshot)
            {
                if (cts.Token.IsCancellationRequested) return;
                if (vm.Icon != null) continue;
                var icon = UninstallerEngine.ExtractIcon(vm.P); // frozen → set direto
                if (icon != null && !cts.Token.IsCancellationRequested)
                    Dispatcher.Invoke(() => vm.Icon = icon);
            }
        }, cts.Token);
    }

    private void ApplyFilter()
    {
        var q = SearchInput.Text.Trim();
        if (StorageMode) return;
        if (AppxMode)
        {
            var apps = q.Length == 0
                ? _appx
                : _appx.Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                   v.P.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                   v.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            AppxList.ItemsSource = apps;
            if (_appxLoaded) SubtitleText.Text = AppxSubtitle();
            EmptyText.Text = "Nenhuma app encontrada.";
            EmptyText.Visibility = apps.Count == 0 && _appxLoaded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        EmptyText.Text = "Nenhum programa encontrado.";
        var items = q.Length == 0
            ? _all
            : _all.Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              v.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ProgramsList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (StorageMode) { LoadDrives(); if (_storagePath.Length > 0) _ = ScanStorageAsync(_storagePath); return; }
        if (AppxMode) _ = LoadAppxAsync(); else _ = LoadAsync();
    }

    private void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerEngine.RestartAsAdmin())
            Application.Current.Shutdown();
    }

    // ---------- seleção ----------

    private void Program_Selected(object sender, SelectionChangedEventArgs e)
    {
        var vm = Selected;
        ActionBar.Visibility = vm == null ? Visibility.Collapsed : Visibility.Visible;
        if (vm == null) return;

        SelectedName.Text = vm.Name;
        StatusText.Text = vm.P.HasUninstaller ? vm.P.RegistryPath : "Sem desinstalador registado — usa a remoção forçada.";
        UninstallBtn.Content = vm.P.HasUninstaller ? "🧹  Desinstalar" : "🧹  Remover (forçado)";
        UninstallBtn.IsEnabled = true;
    }

    // ---------- desinstalar (fluxo automático estilo Revo: 1 botão faz tudo) ----------

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (StorageMode) { StorageAction(); return; }
        if (AppxMode) _ = RemoveAppxFlowAsync(); else _ = UninstallFlowAsync();
    }

    private async Task UninstallFlowAsync()
    {
        if (_busy || Selected is not { } vm) return;
        var force = !vm.P.HasUninstaller;

        var msg = force
            ? $"Remover \"{vm.Name}\"?\n\nEste programa não tem desinstalador — a pasta, os restos e a entrada no registry são apagados diretamente."
            : $"Desinstalar \"{vm.Name}\"?\n\nO desinstalador oficial corre primeiro; no fim os restos (pastas, atalhos, registry) são limpos automaticamente.";
        if (MessageBox.Show(msg, "Desinstalar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _busy = true;
        try
        {
            if (!force)
            {
                StatusText.Text = "A correr o desinstalador oficial…";
                try
                {
                    await UninstallerEngine.UninstallAsync(vm.P, quiet: false);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Falhou a arrancar o desinstalador: {ex.Message}";
                    return;
                }

                // wizard cancelado / falhou → programa ainda instalado; NÃO apagar nada
                if (UninstallerEngine.UninstallKeyExists(vm.P))
                {
                    StatusText.Text = "O desinstalador não terminou (cancelado?) — nada foi apagado.";
                    return;
                }
            }

            // varre tudo à raiz: Program Files, AppData, ProgramData, Start Menu,
            // Desktop, registry Software e autoruns
            StatusText.Text = "A procurar tudo o que ficou no PC…";
            var items = await Task.Run(() => force
                ? UninstallerEngine.ScanForForcedRemoval(vm.P)
                : UninstallerEngine.ScanLeftovers(vm.P));

            if (items.Count == 0)
            {
                StatusText.Text = $"✅ {vm.Name} desinstalado — não deixou nada para trás.";
                MessageBox.Show("Desinstalação limpa — não ficaram restos no PC. 👌",
                    "Restos", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // aviso ao dono: mostra tudo o que foi encontrado, pré-selecionado,
            // e só apaga quando ele carregar em "Remover tudo do PC" e confirmar
            ShowLeftovers(vm, items, afterUninstall: !force);
            StatusText.Text = $"{items.Count} itens encontrados — revê a lista e confirma para remover tudo do PC.";
        }
        finally
        {
            _busy = false;
            _ = LoadAsync(); // a lista mudou
        }
    }

    private static bool NeedsAdmin(IEnumerable<LeftoverItem> items)
    {
        if (UninstallerEngine.IsAdmin()) return false;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return items.Any(i =>
            i.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ||
            (pf.Length > 0 && i.Path.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) ||
            (pf86.Length > 0 && i.Path.StartsWith(pf86, StringComparison.OrdinalIgnoreCase)) ||
            (pd.Length > 0 && i.Path.StartsWith(pd, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------- restos ----------

    private void ShowLeftovers(ProgramVM vm, List<LeftoverItem> items, bool afterUninstall)
    {
        if (items.Count == 0)
        {
            MessageBox.Show(afterUninstall
                    ? "Desinstalação limpa — não ficaram restos no PC. 👌"
                    : "Não foram encontrados restos para este programa.",
                "Restos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _leftovers = items.Select(i => new LeftoverVM(i)).ToList();
        LeftoversList.ItemsSource = _leftovers;
        LeftoversTitle.Text = $"Restos de {vm.Name}";
        var fsBytes = items.Sum(i => i.SizeBytes);
        var regCount = items.Count(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue);
        LeftoversSubtitle.Text =
            $"{items.Count} itens ({UninstallerEngine.FormatSize(fsBytes)} em disco, {regCount} no registry). " +
            "Isto é tudo o que ficou no PC — desmarca o que quiseres manter e carrega em Remover tudo do PC.";
        LeftoversPanel.Visibility = Visibility.Visible;
    }

    private void LeftoversSelectAll_Click(object sender, RoutedEventArgs e) =>
        _leftovers.ForEach(l => l.IsChecked = true);

    private void LeftoversSelectNone_Click(object sender, RoutedEventArgs e) =>
        _leftovers.ForEach(l => l.IsChecked = false);

    private void LeftoversClose_Click(object sender, RoutedEventArgs e) =>
        LeftoversPanel.Visibility = Visibility.Collapsed;

    private async void LeftoversDelete_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _leftovers.Where(l => l.IsChecked).ToList();
        if (chosen.Count == 0) return;

        // itens em Program Files / ProgramData / HKLM precisam de admin — avisa antes
        // de falhar item a item com "sem permissão"
        if (NeedsAdmin(chosen.Select(l => l.Item)))
        {
            var r = MessageBox.Show(
                "Alguns dos itens escolhidos estão em zonas protegidas (Program Files / registry do sistema) " +
                "e precisam de administrador para serem apagados.\n\nReiniciar a app como administrador agora?",
                "Permissões", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                if (UninstallerEngine.RestartAsAdmin())
                    Application.Current.Shutdown();
                return; // UAC cancelado → não tenta apagar (ia falhar)
            }
            // "Não" → segue mesmo assim; itens protegidos mostram o erro individual
        }

        if (MessageBox.Show(
                $"Apagar definitivamente {chosen.Count} itens?\n\nFicheiros vão para o vazio (não para a Reciclagem) " +
                "e as chaves de registry são removidas. Esta ação não pode ser anulada.",
                "Confirmar limpeza", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DeleteLeftoversBtn.IsEnabled = false;
        var failed = 0;
        foreach (var l in chosen)
        {
            var (ok, error) = await Task.Run(() => UninstallerEngine.DeleteLeftover(l.Item));
            if (ok) { l.Error = ""; }
            else { l.Error = error; failed++; }
        }
        DeleteLeftoversBtn.IsEnabled = true;

        // remove da lista o que foi apagado com sucesso
        _leftovers = _leftovers.Where(l => !l.IsChecked || l.HasError).ToList();
        LeftoversList.ItemsSource = _leftovers;

        if (_leftovers.Count == 0)
        {
            LeftoversPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Limpeza concluída — {chosen.Count} itens removidos.";
        }
        else
        {
            LeftoversSubtitle.Text = failed > 0
                ? $"{failed} itens falharam (vê o erro em cada um). Dica: reinicia a app como administrador."
                : "Itens restantes por confirmar.";
        }
        _ = LoadAsync();
    }
}
