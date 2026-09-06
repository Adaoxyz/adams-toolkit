using System.ComponentModel;
using System.Management;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public class DriverUpdateVM : INotifyPropertyChanged
{
    public DriverUpdateService.DriverUpdate U { get; }
    public DriverUpdateVM(DriverUpdateService.DriverUpdate u) => U = u;

    public string Title => U.Title;

    public string Icon => U.DriverClass.ToUpperInvariant() switch
    {
        "DISPLAY" => "🖥",
        "NET" => "🌐",
        "MEDIA" or "AUDIO" or "SOUND" => "🔊",
        "USB" => "🔌",
        "BLUETOOTH" => "📶",
        "PRINTER" => "🖨",
        _ => "🧩",
    };

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(U.DriverClass)) parts.Add(U.DriverClass);
            if (!string.IsNullOrWhiteSpace(U.Manufacturer)) parts.Add(U.Manufacturer);
            if (!string.IsNullOrWhiteSpace(U.Date)) parts.Add($"versão de {U.Date}");
            if (U.SizeMb > 0) parts.Add($"{U.SizeMb:0.#} MB");
            return string.Join("  •  ", parts);
        }
    }

    private bool _busy;
    public bool Busy { get => _busy; set { _busy = value; Notify(nameof(CanUpdate)); Notify(nameof(ButtonLabel)); } }

    private bool _locked; // outra instalação a decorrer
    public bool Locked { get => _locked; set { _locked = value; Notify(nameof(CanUpdate)); } }

    public bool CanUpdate => !Busy && !Locked;
    public string ButtonLabel => Busy ? "A instalar…" : "Atualizar";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class GpuVM
{
    public string Name { get; init; } = "";
    public string Details { get; set; } = "";
    public string Url { get; init; } = "";
}

public partial class DriverUpdatesView : UserControl
{
    private readonly List<DriverUpdateVM> _cards = new();
    private bool _installing;
    private bool _gpuLoaded;

    public DriverUpdatesView()
    {
        InitializeComponent();
        Loaded += (_, _) => OnShown();
    }

    private void OnShown()
    {
        if (!_gpuLoaded) { _gpuLoaded = true; _ = LoadGpusAsync(); }
        if (_installing) return;

        if (DriverUpdateService.IsScanning) { _ = AttachScanAsync(); return; }

        var last = DriverUpdateService.Last;
        // cache de 30 min — reabrir a aba não martela o Windows Update
        if (last != null && DateTime.Now - last.When < TimeSpan.FromMinutes(30))
            ShowResult(last);
        else
            _ = StartScanAsync();
    }

    private async Task StartScanAsync(bool keepResult = false)
    {
        ShowScanning(keepResult);
        ShowResult(await DriverUpdateService.ScanAsync());
    }

    private Task AttachScanAsync() => StartScanAsync();

    private void ShowScanning(bool keepResult = false)
    {
        RefreshBtn.IsEnabled = false;
        if (!keepResult) ResultText.Visibility = Visibility.Collapsed;
        InstallPanel.Visibility = Visibility.Collapsed;
        SetStatus(scanning: true);
        UpdatesList.ItemsSource = null;
    }

    private void SetStatus(bool scanning = false, bool upToDate = false, bool found = false, bool error = false)
    {
        ScanningPanel.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        UpToDatePanel.Visibility = upToDate ? Visibility.Visible : Visibility.Collapsed;
        FoundPanel.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowResult(DriverUpdateService.ScanResult r)
    {
        RefreshBtn.IsEnabled = true;
        _cards.Clear();

        if (!r.Ok)
        {
            // Falha na verificação = estado neutro. Nunca lista nem propõe nada.
            SetStatus(error: true);
            ErrorText.Text = r.Error ?? "O Windows Update não respondeu.";
            UpdatesList.ItemsSource = null;
            return;
        }

        if (r.Updates.Count == 0)
        {
            SetStatus(upToDate: true);
            UpToDateSub.Text = $"Nenhuma atualização de driver encontrada. Última verificação: {r.When:HH:mm}.";
            UpdatesList.ItemsSource = null;
            return;
        }

        SetStatus(found: true);
        FoundTitle.Text = r.Updates.Count == 1
            ? "1 atualização de driver disponível"
            : $"{r.Updates.Count} atualizações de driver disponíveis";
        foreach (var u in r.Updates) _cards.Add(new DriverUpdateVM(u));
        UpdatesList.ItemsSource = _cards.ToList();

    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _ = StartScanAsync();
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_cards.ToList());

    private async void UpdateOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DriverUpdateVM card)
            await InstallAsync(new List<DriverUpdateVM> { card });
    }

    private async Task InstallAsync(List<DriverUpdateVM> targets)
    {
        if (_installing || targets.Count == 0) return;
        foreach (var c in _cards) c.Locked = true;
        foreach (var c in targets) { c.Busy = true; c.Locked = false; }
        await InstallIdsAsync(targets.Select(t => t.U.Id).ToList());
        foreach (var c in _cards) { c.Busy = false; c.Locked = false; }
    }

    /// <summary>Instalação por UpdateID — usada pela lista do WU e pelo inventário.</summary>
    private async Task<bool> InstallIdsAsync(List<string> ids)
    {
        if (_installing || ids.Count == 0) return false;
        _installing = true;
        RefreshBtn.IsEnabled = false;
        UpdateAllBtn.IsEnabled = false;

        ResultText.Visibility = Visibility.Collapsed;
        InstallPanel.Visibility = Visibility.Visible;
        InstallBar.IsIndeterminate = true;
        InstallLabel.Text = "A preparar…";

        var progress = new Progress<(double pct, string label)>(p =>
        {
            InstallBar.IsIndeterminate = p.pct < 0;
            if (p.pct >= 0) InstallBar.Value = p.pct;
            InstallLabel.Text = p.label;
        });

        var (ok, msg, _) = await DriverUpdateService.InstallAsync(ids, progress);

        InstallPanel.Visibility = Visibility.Collapsed;
        UpdateAllBtn.IsEnabled = true;
        RefreshBtn.IsEnabled = true;
        _installing = false;

        ResultText.Visibility = Visibility.Visible;
        ResultText.Text = ok ? $"✅ {msg}" : $"⚠️ {msg}  (detalhes em %AppData%\\AdamsToolkit\\logs)";
        ResultText.Foreground = (Brush)FindResource(ok ? "BrSuccess" : "BrDanger");

        // re-verifica para a lista refletir o estado real pós-instalação
        if (ok) await StartScanAsync(keepResult: true);
        return ok;
    }

    // ---- GPU + verificação NVIDIA (API oficial) ----

    private string _nvidiaDownloadUrl = "";

    private async Task LoadGpusAsync()
    {
        string nvName = "", nvWmiVer = "";
        bool hasNvidia = false, hasAmd = false, hasIntel = false;

        var gpus = await Task.Run(() =>
        {
            var list = new List<GpuVM>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    var name = mo["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var ver = mo["DriverVersion"]?.ToString() ?? "?";
                    var date = "";
                    try
                    {
                        var raw = mo["DriverDate"]?.ToString();
                        if (!string.IsNullOrEmpty(raw))
                            date = ManagementDateTimeConverter.ToDateTime(raw).ToString("dd/MM/yyyy");
                    }
                    catch { }

                    var lower = name.ToLowerInvariant();
                    var isNv = lower.Contains("nvidia") || lower.Contains("geforce");
                    if (isNv) { hasNvidia = true; if (nvName.Length == 0) { nvName = name; nvWmiVer = ver; } }
                    if (lower.Contains("amd") || lower.Contains("radeon")) hasAmd = true;
                    if (lower.Contains("intel")) hasIntel = true;

                    var url =
                        isNv ? "https://www.nvidia.com/en-us/drivers/" :
                        lower.Contains("amd") || lower.Contains("radeon") ? "https://www.amd.com/en/support/download/drivers.html" :
                        lower.Contains("intel") ? "https://www.intel.com/content/www/us/en/support/detect.html" :
                        "https://support.microsoft.com/windows";

                    // versão NVIDIA em formato humano (560.94) quando conseguirmos derivar
                    var shown = ver;
                    if (isNv) shown = NvidiaDriverCheck.ToUserVersion(ver) ?? ver;

                    list.Add(new GpuVM
                    {
                        Name = name,
                        Details = date.Length > 0 ? $"Driver {shown}  •  {date}" : $"Driver {shown}",
                        Url = url,
                    });
                }

                // CPU também conta para as ferramentas de chipset
                try
                {
                    using var cpu = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor");
                    foreach (var mo in cpu.Get())
                    {
                        var man = mo["Manufacturer"]?.ToString() ?? "";
                        if (man.Contains("Intel", StringComparison.OrdinalIgnoreCase)) hasIntel = true;
                        if (man.Contains("AMD", StringComparison.OrdinalIgnoreCase)) hasAmd = true;
                    }
                }
                catch { }
            }
            catch { /* WMI indisponível → card fica escondido */ }
            return list;
        });

        if (hasNvidia) await CheckNvidiaAsync(nvName, nvWmiVer);
    }

    private async Task CheckNvidiaAsync(string name, string wmiVer)
    {
        var r = await NvidiaDriverCheck.CheckAsync(name, wmiVer);
        if (r == null) return; // sem confirmação da NVIDIA → sem afirmações

        if (r.IsNewer)
        {
            _nvidiaDownloadUrl = r.DownloadUrl;
            GpuUpdTitle.Text = $"Driver NVIDIA {r.Latest} disponível";
            GpuUpdSub.Text = $"Instalado: {r.Installed}  •  WHQL, download direto dos servidores oficiais NVIDIA";
            GpuUpdateCard.Visibility = Visibility.Visible;
        }
    }

    private void GpuUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_nvidiaDownloadUrl.Length > 0) DownloadManager.OpenUrl(_nvidiaDownloadUrl);
    }
}
