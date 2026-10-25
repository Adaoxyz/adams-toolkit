using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class AjustesView : UserControl
{
    /// <summary>Linha da lista — um retrato do ajuste no momento do refresh.</summary>
    public class Row
    {
        public LibTweak T { get; init; } = null!;
        public string Name => T.Name;
        public string Description => T.Description;
        public string Category => T.Category;
        public bool Recommended => T.Recommended;
        public bool Active { get; init; }
        public bool Idle { get; init; } = true;
        public string RiskText => T.RiskMedium ? "Risco médio" : "Risco baixo";
        public string RiskSuffix => T.RiskMedium ? " (risco médio)" : "";
        public Brush RiskBrush { get; init; } = Brushes.White;
        public string Note => T.NeedsReboot ? "↻ só conta depois de reiniciar" : T.Lifetime ?? "";
        public bool HasNote => Note.Length > 0;
        public string StateText => Active ? "● Ativo" : "○ Inativo";
        public bool CanApply => Idle && !Active;
        public bool CanRevert => Idle && Active;
        public Brush StateBrush { get; init; } = Brushes.White;
        public string? Status { get; init; }
        public bool HasStatus => !string.IsNullOrEmpty(Status);
    }

    private readonly Dictionary<string, string> _errors = new();
    private bool _busy;

    public AjustesView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            AdminBanner.Visibility = UninstallerEngine.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
            await TweakLibrary.RefreshSlowStateAsync(); // Core Parking vem do powercfg (processo) — só ao abrir
            Refresh();
        };
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private string _cat = "", _risk = "";

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.IsChecked == true)
        {
            if (rb.GroupName == "cat") _cat = rb.Tag as string ?? "";
            else _risk = rb.Tag as string ?? "";
        }
        if (IsLoaded) Refresh();
    }

    private void Refresh()
    {
        var q = SearchInput.Text.Trim();
        var rows = TweakLibrary.All
            .Where(t => _cat == "" || t.Category == _cat)
            .Where(t => _risk == "" || (_risk == "mid") == t.RiskMedium)
            .Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t =>
            {
                var active = SafeActive(t);
                return new Row
                {
                    T = t, Active = active, Idle = !_busy,
                    RiskBrush = Res(t.RiskMedium ? "BrWarn" : "BrSuccess"),
                    StateBrush = Res(active ? "BrSuccess" : "BrMuted"),
                    Status = _errors.GetValueOrDefault(t.Id),
                };
            }).ToList();
        List.ItemsSource = rows;

        var all = TweakLibrary.All;
        var on = all.Count(SafeActive);
        var pending = all.Count(t => t.Recommended && !SafeActive(t));
        CountText.Text = $"{rows.Count} de {all.Count} ajustes · {on} ativos · " +
            (pending == 0 ? "todos os recomendados já estão ativos" : $"{pending} recomendados por ativar") +
            "  ·  🛡 nenhum ajuste apaga registos, histórico ou cache — seguro para PC check.";
        RecBtn.IsEnabled = pending > 0 && !_busy;
    }

    private static bool SafeActive(LibTweak t)
    {
        try { return t.IsActive(); } catch { return false; }
    }

    private async Task<string?> RunAsync(LibTweak t, bool apply)
    {
        string? err;
        try { err = apply ? await t.Apply() : await t.Revert(); }
        catch (Exception ex) { err = ex.Message; }
        if (err == null) _errors.Remove(t.Id);
        else { _errors[t.Id] = "⚠ " + err; Log.Warn($"ajuste {t.Id} ({(apply ? "aplicar" : "reverter")}): {err}"); }
        return err;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => _ = Run(sender, apply: true);
    private void Revert_Click(object sender, RoutedEventArgs e) => _ = Run(sender, apply: false);
    private void Toggle_Click(object sender, RoutedEventArgs e) =>
        _ = Run(sender, apply: !((sender as FrameworkElement)?.DataContext as Row)?.Active ?? false);

    private async Task Run(object sender, bool apply)
    {
        if (_busy || sender is not FrameworkElement { DataContext: Row row }) return;
        if (apply && row.T.RiskMedium)
        {
            var ok = MessageBox.Show($"{row.T.Name}\n\n{row.T.Description}\n\nEste ajuste é de risco médio. Aplicar?",
                "Adams Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) { Refresh(); return; }
        }
        _busy = true;
        Refresh();
        var err = await RunAsync(row.T, apply);
        _busy = false;
        Refresh();
        if (err == null) MainWindow.Toast(apply ? $"Aplicado: {row.T.Name}" : $"Revertido: {row.T.Name}");
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerEngine.RestartAsAdmin()) Application.Current.Shutdown();
    }

    // ---------- aplicar recomendados ----------

    private List<LibTweak> _pending = new();

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        _pending = TweakLibrary.All.Where(t => t.Recommended && !SafeActive(t)).ToList();
        if (_pending.Count == 0) return;
        ConfirmIntro.Text = $"Vão ser ativados {_pending.Count} ajustes. Todos se revertem aqui com 1 clique.";
        ConfirmList.ItemsSource = _pending.Select(t => new Row { T = t, RiskBrush = Res(t.RiskMedium ? "BrWarn" : "BrSuccess") }).ToList();
        ConfirmStatus.Text = UninstallerEngine.IsAdmin() ? "" : "Sem admin, os ajustes de sistema ficam por aplicar (aparecem com aviso na lista).";
        ConfirmOk.IsEnabled = ConfirmCancel.IsEnabled = true;
        ConfirmLayer.Visibility = Visibility.Visible;
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    private async void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOk.IsEnabled = ConfirmCancel.IsEnabled = false;
        _busy = true;
        try
        {
            if (RestoreCheck.IsChecked == true)
            {
                ConfirmStatus.Text = "A criar ponto de restauro… (aceita o pedido de administrador)";
                var (ok, msg) = await RestorePointService.CreateAsync("Adams Toolkit - antes dos ajustes recomendados");
                if (!ok && MessageBox.Show($"Não foi possível criar o ponto de restauro:\n{msg}\n\nContinuar mesmo assim?",
                        "Adams Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    ConfirmLayer.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            var failed = 0;
            for (var i = 0; i < _pending.Count; i++)
            {
                ConfirmStatus.Text = $"A aplicar {i + 1}/{_pending.Count} — {_pending[i].Name}…";
                if (await RunAsync(_pending[i], apply: true) != null) failed++;
            }
            ConfirmLayer.Visibility = Visibility.Collapsed;
            if (failed > 0)
                MessageBox.Show($"{_pending.Count - failed} ajustes aplicados, {failed} falharam — o motivo aparece em cada um na lista.",
                    "Adams Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
