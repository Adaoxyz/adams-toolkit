using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public class TweakVM : INotifyPropertyChanged
{
    public NetTweak T { get; }
    public TweakVM(NetTweak t)
    {
        T = t;
        _isChecked = !t.Aggressive && !t.Beta; // seguros pré-selecionados; agressivos e beta opt-in
    }

    public string Name => T.Name;
    public string Description => T.Description;
    public Visibility BetaVis => T.Beta ? Visibility.Visible : Visibility.Collapsed;

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; Notify(); } }

    private bool _canEdit = true;
    public bool CanEdit { get => _canEdit; set { _canEdit = value; Notify(); } }

    private string _statusText = "";
    public string StatusText { get => _statusText; set { _statusText = value; Notify(); } }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; Notify(); } }

    public void SetStatus(string text, string kind) // kind: ok | err | none
    {
        StatusText = text;
        StatusBrush = kind switch
        {
            "ok" => (Brush)Application.Current.Resources["BrSuccess"],
            "err" => (Brush)Application.Current.Resources["BrDanger"],
            _ => (Brush)Application.Current.Resources["BrMuted"],
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class StartupAppVM : INotifyPropertyChanged
{
    private readonly StartupAppsManager.StartupApp _app;
    private readonly Action<string> _onError;
    public StartupAppVM(StartupAppsManager.StartupApp app, bool isAdmin, Action<string> onError)
    {
        _app = app;
        _onError = onError;
        _isChecked = app.Enabled;
        CanEdit = !app.Machine || isAdmin;
    }

    public string Name => _app.Name;
    public string Command => _app.Command;
    public string SourceText => _app.SourceLabel + (_app.Machine ? " 🛡" : "");
    public bool CanEdit { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            var err = StartupAppsManager.SetEnabled(_app, value);
            if (err != null)
            {
                _onError($"{Name}: {err}");
                // registo recusou → repõe o interruptor no ecrã (fora do ciclo do binding)
                Application.Current.Dispatcher.BeginInvoke(() =>
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))));
                return;
            }
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Uma linha do diagnóstico de quebras.</summary>
public class DiagVM
{
    private readonly DiagItem _d;
    public DiagVM(DiagItem d) => _d = d;

    public string Icon => _d.Icon;
    public string Title => _d.Title;
    public string Detail => _d.Detail;
    public Brush TitleBrush => _d.Level switch
    {
        "ok" => (Brush)Application.Current.Resources["BrText"],
        "warn" => (Brush)Application.Current.Resources["BrAccent"],
        _ => (Brush)Application.Current.Resources["BrDanger"],
    };
}

/// <summary>Uma linha de "processos desnecessários" (um exe, N instâncias).</summary>
public class TrimVM : INotifyPropertyChanged
{
    public ProcessTrimmer.Found F { get; }
    public TrimVM(ProcessTrimmer.Found f) => F = f;

    public string Title => F.Count > 1 ? $"{F.Rule.Label}  ×{F.Count}" : F.Rule.Label;
    public string Detail => F.ServiceName != null
        ? $"{F.Rule.Group}  •  {F.Rule.Exe}  •  {F.Rule.Detail}"
        : F.Rule.Detail.Length > 0 ? $"{F.Rule.Group}  •  {F.Rule.Detail}"
        : $"{F.Rule.Group}  •  {F.Rule.Exe}.exe";
    public string Mb => F.Mb;

    private bool _sel;
    public TrimVM(ProcessTrimmer.Found f, bool selected) { F = f; _sel = selected; }
    public bool Selected
    {
        get => _sel;
        set { _sel = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class OptimizerView : UserControl
{
    private readonly List<TweakVM> _tweaks;
    private readonly List<TweakVM> _fps;
    private bool _busy;
    private bool _suppressToggle; // muda IsChecked por código sem disparar apply/revert
    private bool _suppressSched;

    public OptimizerView()
    {
        InitializeComponent();
        _tweaks = NetworkOptimizer.Tweaks.Select(t => new TweakVM(t)).ToList();
        SafeList.ItemsSource = _tweaks.Where(v => !v.T.Aggressive).ToList();
        AggroList.ItemsSource = _tweaks.Where(v => v.T.Aggressive).ToList();

        _fps = GameBoost.Tweaks.Select(t => new TweakVM(t)).ToList();
        FpsList.ItemsSource = _fps;

        Loaded += async (_, _) =>
        {
            AdminBanner.Visibility = UninstallerEngine.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
            _suppressSched = true;
            SchedCheck.IsChecked = ScheduledCleanup.IsEnabled;
            _suppressSched = false;
            _ = LoadStartupAppsAsync();
            var autoReverted = await NetworkOptimizer.StartupCheck; // espera o auto-revert pós-reboot
            SyncFromState();
            if (autoReverted)
                BoostState.Text = "O PC foi reiniciado — o boost desligou-se. Ativa outra vez quando fores jogar.";


            var fpsReverted = await GameBoost.StartupCheck;
            SyncFpsFromState();
            SyncVfx();
            SyncSvcHost();
            SyncShell();
            if (fpsReverted)
                FpsState.Text = "O PC foi reiniciado — o boost de FPS desligou-se. Ativa outra vez quando fores jogar.";
        };
    }

    // ---------- processos desnecessários ----------

    private List<TrimVM> _trim = new();

    private async void TrimScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        TrimScanBtn.IsEnabled = false;
        TrimKillBtn.Visibility = Visibility.Collapsed;
        TrimState.Text = "A procurar…";

        var total = ProcessTrimmer.TotalRunning();
        var found = await ProcessTrimmer.ScanAsync();
        _trim = found.Select(f => new TrimVM(f, f.Default)).ToList();
        TrimList.ItemsSource = _trim;
        TrimSelectAllBtn.Visibility = _trim.Any(t => !t.Selected) ? Visibility.Visible : Visibility.Collapsed;

        var n = found.Sum(f => f.Count);
        var mb = found.Sum(f => f.Bytes) / 1024 / 1024;
        var others = found.Count(f => f.Rule.Group == ProcessTrimmer.GroupOther);
        var svcs = found.Count(f => f.ServiceName != null);
        if (n == 0)
            TrimState.Text = $"✓ {total} processos a correr e nada dispensável — o resto é Windows, drivers ou coisas que usas.";
        else
        {
            TrimState.Text = $"{total} processos a correr — {n} dispensáveis ({mb} MB de RAM)."
                + (others > 0 ? $" \"{ProcessTrimmer.GroupOther}\" ({others}) vêm desligados: liga os que não reconheces." : "")
                + (svcs > 0 ? $" {svcs} serviços param só nesta sessão (1 pedido de administrador)." : "")
                + " Depois carrega em Fechar.";
            TrimKillBtn.Content = $"Fechar {_trim.Where(t => t.Selected).Sum(t => t.F.Count)}";
            TrimKillBtn.Visibility = Visibility.Visible;
        }

        TrimScanBtn.IsEnabled = true;
        _busy = false;
    }

    private async void TrimKill_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var targets = _trim.Where(t => t.Selected).Select(t => t.F).ToList();
        if (targets.Count == 0) { TrimState.Text = "Nada selecionado."; return; }

        _busy = true;
        TrimKillBtn.IsEnabled = false;
        TrimScanBtn.IsEnabled = false;
        TrimState.Text = "A fechar…";

        var (closed, failed, bytes) = await ProcessTrimmer.KillAsync(targets);
        var left = ProcessTrimmer.TotalRunning();
        TrimState.Text = failed == 0
            ? $"✓ {closed} processos fechados, {bytes / 1024 / 1024} MB libertados — agora {left} a correr. Voltam no próximo arranque; para os travar de vez vê o Arranque do Windows."
            : $"{closed} fechados, {failed} não deixaram (protegidos ou já a sair) — agora {left} a correr.";

        _trim = _trim.Where(t => !t.Selected).ToList();
        TrimList.ItemsSource = _trim;
        TrimKillBtn.Visibility = _trim.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrimKillBtn.Content = $"Fechar {_trim.Sum(t => t.F.Count)}";
        TrimSelectAllBtn.Visibility = _trim.Any(t => !t.Selected) ? Visibility.Visible : Visibility.Collapsed;
        TrimKillBtn.IsEnabled = true;
        TrimScanBtn.IsEnabled = true;
        _busy = false;
    }

    private void TrimSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _trim) t.Selected = true;
        TrimKillBtn.Content = $"Fechar {_trim.Sum(t => t.F.Count)}";
        TrimSelectAllBtn.Visibility = Visibility.Collapsed;
    }

    // ---------- juntar svchost ----------

    private bool _svcHostSyncing;

    private void SyncSvcHost()
    {
        _svcHostSyncing = true;
        SvcHostToggle.IsChecked = ProcessTrimmer.SvcHostMerged;
        _svcHostSyncing = false;
    }

    private async void SvcHost_Changed(object sender, RoutedEventArgs e)
    {
        if (_svcHostSyncing) return;
        var on = SvcHostToggle.IsChecked == true;
        SvcHostToggle.IsEnabled = false;
        SvcHostState.Text = "Aceita o pedido de administrador…";
        var ok = await ProcessTrimmer.SetSvcHostMergedAsync(on);
        SyncSvcHost();
        SvcHostState.Text = !ok ? "Cancelado — precisa de permissões de administrador."
            : on == ProcessTrimmer.SvcHostMerged
                ? (on ? "✓ Ativo — reinicia o PC para os serviços ficarem agrupados (−30 a −50 processos)."
                      : "Reposto o valor original — aplica ao reiniciar.")
                : "Não ficou aplicado (política de grupo ou registo protegido).";
        SvcHostToggle.IsEnabled = true;
    }

    /// <summary>Reflete o estado persistido do boost de FPS.</summary>
    private void SyncFpsFromState()
    {
        var active = GameBoost.AnyApplied;
        _suppressToggle = true;
        FpsBtn.IsChecked = active;
        _suppressToggle = false;

        FpsBtn.Content = active ? "Desativar FPS" : "Ativar FPS";
        FpsState.Text = active ? "Boost de FPS ATIVO" : "Boost de FPS desligado";
        foreach (var vm in _fps)
        {
            var applied = !vm.T.SessionOnly && GameBoost.IsApplied(vm.T.Id);
            if (applied) { vm.IsChecked = true; vm.SetStatus("✓ ativo", "ok"); }
            vm.CanEdit = !active;
        }
    }

    /// <summary>Reflete o estado persistido (app pode ter sido fechada com o boost ligado).</summary>
    private void SyncFromState()
    {
        var active = NetworkOptimizer.AnyApplied;
        _suppressToggle = true;
        BoostBtn.IsChecked = active;
        _suppressToggle = false;

        BoostBtn.Content = active ? "Desativar boost" : "Ativar boost";
        BoostState.Text = active ? "Boost ATIVO" : "Boost desligado";
        foreach (var vm in _tweaks)
        {
            var applied = !vm.T.SessionOnly && NetworkOptimizer.IsApplied(vm.T.Id);
            if (applied) { vm.IsChecked = true; vm.SetStatus("✓ ativo", "ok"); }
            vm.CanEdit = !active;
        }
    }

    private void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerEngine.RestartAsAdmin())
            Application.Current.Shutdown();
    }

    // ---------- toggle ----------

    private async void Boost_On(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _busy) return;

        var chosen = _tweaks.Where(v => v.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            _suppressToggle = true; BoostBtn.IsChecked = false; _suppressToggle = false;
            BoostState.Text = "Seleciona pelo menos um tweak.";
            return;
        }

        var dropsNet = chosen.Any(v => v.T.Id == "intmod");
        var msg = $"Ativar o boost com {chosen.Count} tweaks?\n\nTudo fica guardado e o botão Desativar restaura o PC como estava.";
        if (dropsNet) msg += "\n\n⚠ O \"interrupt moderation\" reinicia a placa de rede — a internet cai 1–2 segundos.";
        if (MessageBox.Show(msg, "Ativar boost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _suppressToggle = true; BoostBtn.IsChecked = false; _suppressToggle = false;
            return;
        }

        _busy = true;
        BoostBtn.IsEnabled = false;
        foreach (var vm in _tweaks) vm.CanEdit = false;

        // ping "antes" (se ainda não foi medido nesta sessão)
        if (PingBefore.Text == "—")
        {
            BoostState.Text = "A medir ping antes…";
            var before = await NetworkOptimizer.PingServerAsync();
            PingBefore.Text = before.Summary;
        }

        var failed = 0;
        foreach (var vm in chosen)
        {
            BoostState.Text = $"A aplicar: {vm.Name}…";
            vm.SetStatus("a aplicar…", "none");
            var err = await NetworkOptimizer.ApplyAsync(vm.T);
            if (err == null) vm.SetStatus("✓ ativo", "ok");
            else { vm.SetStatus($"✗ {err}", "err"); failed++; }
        }

        // re-teste (com folga se a placa reiniciou)
        BoostState.Text = "A medir ping depois…";
        if (dropsNet) await Task.Delay(3000);
        var after = await NetworkOptimizer.PingServerAsync();
        PingAfter.Text = after.Summary;
        PingHint.Text = "O ganho real vê-se no jitter/picos, não na média — testa dentro do jogo.";

        _busy = false;
        BoostBtn.IsEnabled = true;
        BoostBtn.Content = "Desativar boost";
        BoostState.Text = failed == 0
            ? $"Boost ATIVO — {chosen.Count} tweaks aplicados."
            : $"Boost ATIVO — {chosen.Count - failed} ok, {failed} falharam (vê os erros).";
    }

    private async void Boost_Off(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _busy) return;

        _busy = true;
        BoostBtn.IsEnabled = false;
        BoostState.Text = "A restaurar as definições originais…";

        var errors = await NetworkOptimizer.RevertAllAsync();

        foreach (var vm in _tweaks)
        {
            vm.CanEdit = true;
            var err = errors.FirstOrDefault(x => x.name == vm.Name);
            if (err.name != null) vm.SetStatus($"✗ {err.error}", "err");
            else vm.SetStatus("", "none");
        }

        _busy = false;
        BoostBtn.IsEnabled = true;
        BoostBtn.Content = "Ativar boost";
        BoostState.Text = errors.Count == 0
            ? "Boost desligado — tudo restaurado."
            : $"Boost desligado — {errors.Count} itens falharam a restaurar.";
        PingAfter.Text = "—";
    }

    // ---------- boost de FPS ----------

    private async void Fps_On(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _busy) return;

        var chosen = _fps.Where(v => v.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            _suppressToggle = true; FpsBtn.IsChecked = false; _suppressToggle = false;
            FpsState.Text = "Seleciona pelo menos um ajuste.";
            return;
        }

        var msg = $"Ativar o boost de FPS com {chosen.Count} ajustes?\n\n" +
                  "Nada é apagado do PC — só definições de desempenho, todas guardadas e revertidas pelo botão Desativar.";
        if (MessageBox.Show(msg, "Ativar boost de FPS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _suppressToggle = true; FpsBtn.IsChecked = false; _suppressToggle = false;
            return;
        }

        _busy = true;
        FpsBtn.IsEnabled = false;
        foreach (var vm in _fps) vm.CanEdit = false;

        var failed = 0;
        foreach (var vm in chosen)
        {
            FpsState.Text = $"A aplicar: {vm.Name}…";
            vm.SetStatus("a aplicar…", "none");
            var err = await GameBoost.ApplyAsync(vm.T);
            if (err == null) vm.SetStatus("✓ ativo", "ok");
            else { vm.SetStatus($"✗ {err}", "err"); failed++; }
        }

        _busy = false;
        FpsBtn.IsEnabled = true;
        FpsBtn.Content = "Desativar FPS";
        FpsState.Text = failed == 0
            ? $"Boost de FPS ATIVO — {chosen.Count} ajustes aplicados."
            : $"Boost de FPS ATIVO — {chosen.Count - failed} ok, {failed} falharam (vê os erros).";
    }

    private async void Fps_Off(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _busy) return;

        _busy = true;
        FpsBtn.IsEnabled = false;
        FpsState.Text = "A restaurar as definições originais…";

        var errors = await GameBoost.RevertAllAsync();

        foreach (var vm in _fps)
        {
            vm.CanEdit = true;
            var err = errors.FirstOrDefault(x => x.name == vm.Name);
            if (err.name != null) vm.SetStatus($"✗ {err.error}", "err");
            else vm.SetStatus("", "none");
        }

        _busy = false;
        FpsBtn.IsEnabled = true;
        FpsBtn.Content = "Ativar FPS";
        FpsState.Text = errors.Count == 0
            ? "Boost de FPS desligado — tudo restaurado."
            : $"Boost de FPS desligado — {errors.Count} itens falharam a restaurar.";
    }

    // ---------- interface do Windows (explorer.exe) ----------

    /// <summary>Reflete o estado real da shell (pode ter sido fechada/aberta por fora).</summary>
    private void SyncShell()
    {
        var off = ExplorerControl.TurnedOffByUs;
        ShellOffBtn.IsEnabled = !off;
        ShellOnBtn.IsEnabled = off;
        ShellState.Text = off
            ? $"Interface do Windows DESLIGADA — {MainWindow.PanicHotkeyName} traz esta janela de volta. Fica assim até carregares em «Repor interface»."
            : "Interface do Windows ligada";
    }

    private async void ShellOff_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // O atalho de emergência tem de ficar de pé ANTES de matar a shell: sem
        // barra de tarefas e sem Alt+Tab (que morre com o explorer.exe), é a única
        // forma de voltar a esta janela.
        var hotkeyOk = MainWindow.SetPanicHotkey(true);

        var msg = "Fechar a interface do Windows?\n\n" +
                  "Vão desaparecer o ambiente de trabalho, a barra de tarefas e a bandeja. " +
                  "As janelas do Explorador de Ficheiros abertas fecham-se — grava o que tiveres a meio.\n\n";
        msg += hotkeyOk
            ? $"PARA VOLTAR A ESTA JANELA: {MainWindow.PanicHotkeyName}.\n" +
              "Funciona mesmo com o jogo em ecrã inteiro. Atenção: sem a interface do Windows o Alt+Tab também deixa de funcionar, por isso decora este atalho.\n\n" +
              "O botão «Repor interface» traz tudo de volta. Fechar o Adams Toolkit ou reiniciar o PC também repõe."
            : $"⚠ NÃO consegui registar o atalho {MainWindow.PanicHotkeyName} — outra app já o está a usar.\n\n" +
              "Sem ele e sem barra de tarefas podes ficar sem forma de voltar a esta janela (o Alt+Tab também morre com a interface). " +
              "Fecha a app que está a usar esse atalho e tenta outra vez, ou avança sabendo que para recuperar terás de usar o Gestor de Tarefas (Ctrl+Shift+Esc).";

        var icon = hotkeyOk ? MessageBoxImage.Warning : MessageBoxImage.Exclamation;
        if (MessageBox.Show(msg, "Desligar a interface do Windows",
                MessageBoxButton.YesNo, icon) != MessageBoxResult.Yes)
        {
            if (hotkeyOk) MainWindow.SetPanicHotkey(false);
            return;
        }

        _busy = true;
        ShellOffBtn.IsEnabled = false;
        ShellOnBtn.IsEnabled = false;
        ShellState.Text = "A fechar a interface…";

        var err = await ExplorerControl.TurnOffAsync();

        SyncShell();
        if (err != null)
        {
            ShellState.Text = "✗ " + err;
            MainWindow.SetPanicHotkey(false); // não ficou desligada — não segurar o atalho
        }
        else if (hotkeyOk)
        {
            ShellState.Text = $"Interface DESLIGADA — {MainWindow.PanicHotkeyName} traz esta janela de volta.";
        }
        _busy = false;
    }

    private async void ShellOn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        ShellOffBtn.IsEnabled = false;
        ShellOnBtn.IsEnabled = false;
        ShellState.Text = "A repor a interface… (a barra de tarefas demora 1–3 segundos)";

        var err = await ExplorerControl.TurnOnAsync();
        if (err == null) MainWindow.SetPanicHotkey(false); // interface de volta: liberta Ctrl+Alt+A

        SyncShell();
        if (err != null) ShellState.Text = "✗ " + err;
        _busy = false;
    }

    // ---------- diagnóstico ----------


    // ---------- limpeza de temporários ----------

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CleanBtn.IsEnabled = false;

        var res = await Task.Run(() => CleanupEngine.CleanAll(name =>
            Dispatcher.Invoke(() => CleanState.Text = $"A limpar: {name}…")));
        CleanState.Text = "A esvaziar a Reciclagem…";
        var binOk = await Task.Run(CleanupEngine.EmptyRecycleBin);

        var freed = res.Bytes switch
        {
            >= 1073741824 => $"{res.Bytes / 1073741824.0:0.0} GB",
            >= 1048576 => $"{res.Bytes / 1048576.0:0} MB",
            _ => $"{res.Bytes / 1024.0:0} KB",
        };
        var txt = $"✓ Libertados {freed} ({res.Files} ficheiros).";
        if (res.Skipped > 0) txt += $" {res.Skipped} em uso ficaram — é normal.";
        txt += binOk ? " Reciclagem esvaziada." : " Reciclagem: nada para esvaziar.";
        if (!UninstallerEngine.IsAdmin())
            txt += " Sem admin, a pasta Temp do Windows pode não ter sido limpa.";
        CleanState.Text = txt;

        CleanBtn.IsEnabled = true;
        _busy = false;
    }

    // ---------- limpeza agendada ----------

    private async void Sched_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSched) return;
        var want = SchedCheck.IsChecked == true;
        SchedCheck.IsEnabled = false;
        var ok = await Task.Run(() => ScheduledCleanup.SetEnabled(want));
        SchedCheck.IsEnabled = true;
        if (!ok)
        {
            _suppressSched = true;
            SchedCheck.IsChecked = !want;
            _suppressSched = false;
            CleanState.Text = "Não foi possível alterar a limpeza agendada (Agendador de Tarefas indisponível).";
        }
    }

    // ---------- ponto de restauro ----------

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        RestoreBtn.IsEnabled = false;
        RestoreState.Text = "A pedir permissão de administrador e a criar… (pode demorar 1–2 minutos)";
        var (ok, msg) = await RestorePointService.CreateAsync("Adams Toolkit — pedido manual");
        RestoreState.Text = ok ? $"✅ {msg}" : $"⚠️ {msg}";
        RestoreBtn.IsEnabled = true;
    }

    // ---------- efeitos visuais do Windows ----------

    private void SyncVfx()
    {
        try { VfxState.Text = "Atual: " + VisualEffectsService.ModeLabel(VisualEffectsService.GetMode()); }
        catch { VfxState.Text = ""; }
    }

    // v1.69: em vez de aplicar presets à força (o «Melhor desempenho» deixava a caixa
    // de pesquisa do Win11 preta em alguns PCs), abre-se a janela nativa e o utilizador escolhe.
    private void VfxOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("SystemPropertiesPerformance.exe") { UseShellExecute = true });
            VfxState.Text = "Janela aberta — depois de fechares, volta aqui para veres o estado atualizado.";
        }
        catch (Exception ex)
        {
            VfxState.Text = "Não consegui abrir: " + ex.Message;
        }
    }

    // ---------- programas no arranque ----------

    private async Task LoadStartupAppsAsync()
    {
        var isAdmin = UninstallerEngine.IsAdmin();
        var apps = await Task.Run(StartupAppsManager.List);
        var vms = apps.Select(a => new StartupAppVM(a, isAdmin,
            err => Dispatcher.Invoke(() => StartupHint.Visibility = Visibility.Visible))).ToList();
        StartupList.ItemsSource = vms;
        StartupEmpty.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartupHint.Visibility = !isAdmin && vms.Any(v => !v.CanEdit)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartupRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadStartupAppsAsync();

    // ---------- ping manual ----------

    private async void TestPing_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        var target = BoostBtn.IsChecked == true ? PingAfter : PingBefore;
        target.Text = "a testar…";
        var r = await NetworkOptimizer.PingServerAsync();
        target.Text = r.Summary;
        _busy = false;
    }
}
