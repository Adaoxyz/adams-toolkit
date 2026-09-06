using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class FiveMCenterView : UserControl
{
    // caminhos reais desta máquina, resolvidos pelo FiveMLocator (registry → default)
    private static string? FiveMExe => FiveMLocator.ExePath;
    private static string? FiveMDir => FiveMLocator.AppDataDir;

    private readonly System.Windows.Threading.DispatcherTimer _cl2Timer = new()
    { Interval = TimeSpan.FromSeconds(4) };
    private DateTime _cl2NoticeUntil = DateTime.MinValue;   // não pisar mensagens recentes
    private DateTime _cl2StartingUntil = DateTime.MinValue; // janela "a arrancar…" após clicar Abrir

    public FiveMCenterView()
    {
        InitializeComponent();
        _cl2Timer.Tick += (_, _) => RefreshCl2State();
        Loaded += (_, _) =>
        {
            var links = ConfigService.Current.FiveMLinks;
            LinksList.ItemsSource = links;
            LinksCountText.Text = $"{links?.Count ?? 0} atalhos";
            FiveMStateText.Text = FiveMExe != null
                ? $"Instalado ✓  ·  {FiveMExe}" : "Não encontrado — instala no Install Center";
            RefreshReshadeState();
            RefreshCl2State();
            SyncSnapFromState();
            SyncKr();
            if (AppState.IsVisible) _cl2Timer.Start();
            AppState.Changed += OnVisibility;
        };
        Unloaded += (_, _) => { AppState.Changed -= OnVisibility; _cl2Timer.Stop(); };
    }

    // ---------- Snap Tap ----------

    private bool _suppressSnap;

    private void SyncSnapFromState()
    {
        var c = SnapTap.Cfg;
        _suppressSnap = true;
        SnapAD.IsChecked = c.AD; SnapWS.IsChecked = c.WS; SnapOnlyFiveM.IsChecked = c.OnlyFiveM;
        _suppressSnap = false;
        SnapBtn.Content = SnapTap.IsActive ? "Desativar Snap Tap" : "Ativar Snap Tap";
        SnapBtn.Style = (Style)FindResource(SnapTap.IsActive ? "GhostButton" : "PrimaryButton");
        SnapState.Text = SnapTap.IsActive ? SnapActiveText() : "Snap Tap desligado";
    }

    private static string SnapActiveText()
    {
        var c = SnapTap.Cfg;
        var pares = (c.AD ? "A/D" : "") + (c.AD && c.WS ? " + " : "") + (c.WS ? "W/S" : "");
        return $"Snap Tap ATIVO em {pares}" + (c.OnlyFiveM ? " · só com o FiveM em foco" : " · em todo o lado") +
               " · fica ligado enquanto a app estiver aberta (bandeja conta)";
    }

    private void Snap_OptChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSnap) return;
        var c = SnapTap.Cfg;
        c.AD = SnapAD.IsChecked == true; c.WS = SnapWS.IsChecked == true; c.OnlyFiveM = SnapOnlyFiveM.IsChecked == true;
        if (!c.AD && !c.WS)
        {
            _suppressSnap = true; SnapAD.IsChecked = true; _suppressSnap = false; c.AD = true;
        }
        SnapTap.Save();
        if (SnapTap.IsActive) SnapState.Text = SnapActiveText();
    }

    private void Snap_Toggle(object sender, RoutedEventArgs e)
    {
        if (SnapTap.IsActive)
        {
            SnapTap.Stop();
            SnapTap.Cfg.Enabled = false; SnapTap.Save();
        }
        else
        {
            var err = SnapTap.Start();
            if (err != null) { SnapState.Text = "✗ " + err; return; }
            SnapTap.Cfg.Enabled = true; SnapTap.Save();
        }
        SyncSnapFromState();
    }

    // ---------- repetição do teclado ----------

    private bool _krInit;

    private void SyncKr()
    {
        var (d, r) = KeyboardRepeat.Current();
        _krInit = true;
        KrDelay.Value = d; KrRepeat.Value = r;
        _krInit = false;
        Kr_Changed(null!, null!);
        KrState.Text = KeyboardRepeat.IsActive
            ? $"Repetição ATIVA em W/A/S/D — atraso {d} ms · repete a cada {r} ms ({(r > 0 ? 1000 / r : 0)}/s) · só com o FiveM em foco · fica ligado com a app aberta (bandeja conta)"
            : "Desligado — teclado 100% original do PC (padrão do Windows: 500 ms de atraso, ~33 ms de repetição)";
    }

    private void Kr_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (KrDelayText == null || KrRepeatText == null) return;
        KrDelayText.Text = $"{(int)KrDelay.Value} ms";
        var r = (int)KrRepeat.Value;
        KrRepeatText.Text = $"{r} ms";
    }

    private void Kr_Apply(object sender, RoutedEventArgs e)
    {
        var err = KeyboardRepeat.Apply((int)KrDelay.Value, (int)KrRepeat.Value);
        if (err != null) { KrState.Text = "✗ " + err; return; }
        SyncKr();
        KrState.Text += " ✓";
    }

    private void Kr_Reset(object sender, RoutedEventArgs e)
    {
        var err = KeyboardRepeat.RestoreDefault();
        if (err != null) { KrState.Text = "✗ " + err; return; }
        SyncKr();
    }

    // O estado do -cl2 vem de uma consulta WMI aos processos: cara demais para
    // andar a correr com o FiveM à frente e a app esquecida por trás.
    private void OnVisibility(bool visible)
    {
        if (visible) { RefreshCl2State(); _cl2Timer.Start(); }
        else _cl2Timer.Stop();
    }

    /// <summary>2ª instância (-cl2) do FiveM a correr? (via linha de comandos, WMI)</summary>
    private static bool IsCl2Running()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name LIKE 'FiveM%'");
            foreach (var mo in s.Get())
                if ((mo["CommandLine"] as string ?? "").Contains("-cl2", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    // 3 estados verificados: desligado → nada; a arrancar → só na janela pós-clique;
    // ligado → 🟢 (processo -cl2 confirmado via WMI)
    private async void RefreshCl2State()
    {
        if (DateTime.Now < _cl2NoticeUntil) return;
        var running = await Task.Run(IsCl2Running);
        if (DateTime.Now < _cl2NoticeUntil) return; // notice chegou durante o check
        if (running)
        {
            _cl2StartingUntil = DateTime.MinValue;
            SetCl2Text("🟢 2º FiveM ligado.");
        }
        else if (DateTime.Now < _cl2StartingUntil) SetCl2Text("2º FiveM a arrancar…");
        else SetCl2Text(""); // desligado: não aparece nada
    }

    private void SetCl2Text(string t)
    {
        Cl2StateText.Text = t;
        Cl2StateText.Visibility = t.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cl2Notice(string msg, int seconds = 8)
    {
        SetCl2Text(msg);
        _cl2NoticeUntil = DateTime.Now.AddSeconds(seconds);
    }

    private void RefreshReshadeState()
    {
        var installed = ReshadeInstaller.IsInstalled;
        ReshadeBadge.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        ReshadeBtn.Content = installed ? "Reinstalar" : "Instalar";
    }

    private async void InstallReshade_Click(object sender, RoutedEventArgs e)
    {
        if (!ReshadeInstaller.FiveMPresent)
        {
            ReshadeStatusText.Text = "Pasta do FiveM não encontrada — instala e abre o FiveM primeiro.";
            ReshadeStatusText.Visibility = Visibility.Visible;
            return;
        }

        ReshadeBtn.IsEnabled = false;
        ReshadeStatusText.Visibility = Visibility.Collapsed;
        ReshadeProgressPanel.Visibility = Visibility.Visible;
        ReshadeProgress.Value = 0;

        var progress = new Progress<(double pct, string label)>(p =>
        {
            ReshadeProgress.Value = p.pct;
            ReshadeProgressText.Text = p.label;
        });

        var (ok, message) = await ReshadeInstaller.InstallAsync(progress, CancellationToken.None);

        ReshadeProgressPanel.Visibility = Visibility.Collapsed;
        ReshadeStatusText.Text = message;
        ReshadeStatusText.Visibility = Visibility.Visible;
        ReshadeBtn.IsEnabled = true;
        RefreshReshadeState();
    }

    private void OpenFiveM_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (FiveMExe != null) DownloadManager.RunInstaller(FiveMExe);
        else FiveMStateText.Text = "FiveM não encontrado — instala no Install Center";
    }

    // ---------- segundo FiveM (-cl2) ----------

    private void CreateCl2Shortcut_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var exe = FiveMExe;
        if (exe == null) { Cl2StateText.Text = "FiveM não encontrado — instala no Install Center."; return; }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnkPath = System.IO.Path.Combine(desktop, "FiveM 2.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = exe;
            lnk.Arguments = "-cl2";
            lnk.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
            lnk.IconLocation = exe + ",0";
            lnk.Description = "Segundo FiveM (multi-conta)";
            lnk.Save();
            Cl2Notice("✅ Atalho \"FiveM 2\" criado no ambiente de trabalho — abre sempre a 2ª instância.");
        }
        catch (Exception ex)
        {
            Cl2Notice($"Falhou a criar o atalho: {ex.Message}");
        }
    }

    private async void OpenCl2_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var exe = FiveMExe;
        if (exe == null) { Cl2Notice("FiveM não encontrado — instala no Install Center."); return; }
        if (await Task.Run(IsCl2Running)) { SetCl2Text("🟢 2º FiveM ligado."); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-cl2")
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
            });
            _cl2StartingUntil = DateTime.Now.AddSeconds(45); // janela de arranque; o refresh troca p/ 🟢 ou limpa
            SetCl2Text("2º FiveM a arrancar…");
        }
        catch (Exception ex)
        {
            Cl2Notice($"Falhou a abrir: {ex.Message}");
        }
    }

    private void OpenFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (FiveMDir != null) DownloadManager.OpenFolder(FiveMDir);
        else FiveMStateText.Text = "Pasta FiveM ainda não existe";
    }

    private async void ClearCache_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ClearCacheBtn.IsEnabled = false;
        CacheStatusText.Text = "A limpar cache…";
        CacheStatusText.Visibility = Visibility.Visible;

        var (_, message) = await Task.Run(FiveMLocator.ClearCache);

        CacheStatusText.Text = message;
        ClearCacheBtn.IsEnabled = true;
    }

    // ---------- serviços do sistema ----------

    public class ServiceRow
    {
        public string Key { get; init; } = "";
        public string StartName { get; init; } = "";
        public string Display { get; init; } = "";
        public string Detail { get; init; } = "";
        public SvcState State { get; init; }
        public bool AutoStart { get; init; }
        public string StatusText { get; init; } = "";
        public System.Windows.Media.Brush? StatusBrush { get; init; }
        public string ActionText { get; init; } = "";
        public Visibility ActionVisibility { get; init; } = Visibility.Collapsed;
    }

    // evita que o Sync programático da checkbox dispare o handler
    private bool _guardSyncing;

    private async void CheckServices_Click(object sender, RoutedEventArgs e)
    {
        CheckServicesBtn.IsEnabled = false;
        ServicesStatusText.Visibility = Visibility.Collapsed;
        await RefreshServices();
        CheckServicesBtn.IsEnabled = true;
        CheckServicesBtn.Content = "Atualizar";
    }

    private async Task RefreshServices()
    {
        // query em background; rows (com brushes) construídas na UI thread
        var states = await Task.Run(() => ServiceChecker.Monitored
            .Select(m => (m.Key, m.Display, m.Detail,
                          Q: ServiceChecker.Query(m.Key),
                          Auto: ServiceChecker.StartsWithWindows(m.Key)))
            .ToList());
        var guard = await Task.Run(() => ServiceChecker.GuardInstalled);

        _guardSyncing = true;
        ServicesGuardCheck.IsChecked = guard;
        _guardSyncing = false;

        ServicesList.ItemsSource = states.Select(s => new ServiceRow
        {
            Key = s.Key,
            StartName = s.Q.StartName,
            Display = s.Display,
            Detail = s.Detail,
            State = s.Q.State,
            AutoStart = s.Auto,
            StatusText = s.Q.State switch
            {
                // ativo mas com arranque Manual/trigger = volta a estar parado
                // no próximo reboot; é isto que fazia o utilizador reativar
                // sempre à mão
                SvcState.Running  => s.Auto ? "🟢 Ativo" : "🟠 Só esta sessão",
                SvcState.Stopped  => "🟡 Parado",
                SvcState.Disabled => "🔴 Desativado",
                _                 => "— Não existe",
            },
            StatusBrush = s.Q.State switch
            {
                SvcState.Running  => (s.Auto ? TryFindResource("BrSuccess") : TryFindResource("BrAccent")) as System.Windows.Media.Brush,
                SvcState.Disabled => TryFindResource("BrDanger") as System.Windows.Media.Brush,
                SvcState.Stopped  => TryFindResource("BrAccent") as System.Windows.Media.Brush,
                _                 => TryFindResource("BrMuted") as System.Windows.Media.Brush,
            },
            ActionText = "Ativar",
            ActionVisibility = s.Q.State is SvcState.Disabled or SvcState.Stopped
                               || (s.Q.State == SvcState.Running && !s.Auto)
                ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        ServicesList.Visibility = Visibility.Visible;
    }

    private async void EnableService_Click(object sender, RoutedEventArgs e) =>
        await ApplyServices("Serviços");

    private async void EnableAllServices_Click(object sender, RoutedEventArgs e) =>
        await ApplyServices("Serviços");

    /// <summary>
    /// Um só pedido de administrador põe todos os serviços em arranque
    /// automático e inicia-os (e mantém/instala a proteção se estiver marcada).
    /// </summary>
    private async Task ApplyServices(string label)
    {
        EnableAllServicesBtn.IsEnabled = false;
        ServicesStatusText.Text = $"{label}: a ativar… aceita o pedido de administrador.";
        ServicesStatusText.Visibility = Visibility.Visible;

        var guard = ServicesGuardCheck.IsChecked == true;
        var ok = await ServiceChecker.ApplyAsync(guard);

        ServicesStatusText.Text = ok
            ? (guard
                ? "Feito — arranque automático reposto e proteção de arranque ativa."
                : "Feito — arranque automático reposto; ficam ligados depois de reiniciar.")
            : "Cancelado — precisa de permissões de administrador.";
        EnableAllServicesBtn.IsEnabled = true;
        await RefreshServices();
    }

    private async void ServicesGuard_Changed(object sender, RoutedEventArgs e)
    {
        if (_guardSyncing) return;
        var on = ServicesGuardCheck.IsChecked == true;

        ServicesStatusText.Text = on
            ? "A instalar a proteção de arranque… aceita o pedido de administrador."
            : "A remover a proteção de arranque… aceita o pedido de administrador.";
        ServicesStatusText.Visibility = Visibility.Visible;

        var ok = on ? await ServiceChecker.ApplyAsync(true)
                    : await ServiceChecker.RemoveGuardAsync();

        ServicesStatusText.Text = ok
            ? (on ? "Proteção ativa — os serviços são reaplicados em cada arranque do Windows."
                  : "Proteção removida.")
            : "Cancelado — precisa de permissões de administrador.";
        await RefreshServices();
    }

    private void OpenCfx_Click(object sender, System.Windows.RoutedEventArgs e) =>
        DownloadManager.OpenUrl("https://cfx.re");

    private void Link_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is LinkItem link)
            DownloadManager.OpenUrl(link.Url);
    }

    // ---------- PC check (v1.83) ----------
    public sealed record PcCheckRow(string Icon, string Title, string Detail, System.Windows.Media.Brush Brush);

    private async void PcCheck_Click(object sender, RoutedEventArgs e) => await RunPcCheckAsync();

    // separado do handler para o «Reparar» poder ESPERAR pela nova verificação
    // antes de escrever o resultado por baixo (um async void devolvia logo no
    // primeiro await e o texto da reparação era apagado pelo resumo do check).
    private async Task RunPcCheckAsync()
    {
        PcCheckBtn.IsEnabled = false; PcCheckBtn.Content = "A verificar…";
        var items = await Task.Run(PcCheck.Run);
        var ok = (System.Windows.Media.Brush)FindResource("BrSuccess");
        var warn = (System.Windows.Media.Brush)FindResource("BrWarn");
        var bad = (System.Windows.Media.Brush)FindResource("BrDanger");
        PcCheckList.ItemsSource = items
            .OrderBy(i => i.Level == PcCheck.Level.Bad ? 0 : i.Level == PcCheck.Level.Warn ? 1 : 2)
            .Select(i => new PcCheckRow(i.Level == PcCheck.Level.Ok ? "✅" : i.Level == PcCheck.Level.Warn ? "⚠️" : "❌", i.Title, i.Detail,
                                        i.Level == PcCheck.Level.Ok ? ok : i.Level == PcCheck.Level.Warn ? warn : bad)).ToList();
        var nBad = items.Count(i => i.Level == PcCheck.Level.Bad); var nWarn = items.Count(i => i.Level == PcCheck.Level.Warn);
        PcCheckSummary.Text = nBad == 0 && nWarn == 0 ? "✅ Tudo em ordem — podes ir a PC check descansado."
            : nBad == 0 ? $"⚠️ {nWarn} aviso(s). Nada grave, mas lê em baixo."
            : $"❌ {nBad} problema(s) e {nWarn} aviso(s). Repara o que der (botão) e não limpes nada.";
        PcCheckSummary.Visibility = PcCheckList.Visibility = Visibility.Visible;
        PcCheckFixBtn.Visibility = items.Any(i => i.Fixable) ? Visibility.Visible : Visibility.Collapsed;
        PcCheckBtn.IsEnabled = true; PcCheckBtn.Content = "Verificar de novo";
    }

    private async void PcCheckFix_Click(object sender, RoutedEventArgs e)
    {
        PcCheckFixBtn.IsEnabled = false; PcCheckFixBtn.Content = "A reparar…";
        PcCheck.ClearFixReport(); // não mostrar o relatório da vez anterior
        var ok = await ProcessTrimmer.RunElevatedPublicAsync("--pccheck-fix");
        PcCheckFixBtn.IsEnabled = true; PcCheckFixBtn.Content = "Reparar";
        if (!ok) { PcCheckSummary.Text = "Precisa de admin (UAC recusado)."; return; }
        await Task.Delay(800);
        await RunPcCheckAsync();

        // o que a reparação conseguiu (e o que não conseguiu, com o motivo).
        // Antes falhava tudo em silêncio: clicava-se em Reparar, a lista voltava
        // igual e ninguém sabia porquê.
        var report = PcCheck.ReadFixReport();
        var failed = report.Where(l => l.StartsWith("FALHA|")).ToList();
        var fixedN = report.Count(l => l.StartsWith("OK|"));
        if (report.Count == 0) return;
        var txt = failed.Count == 0
            ? $"🔧 Reparação: {fixedN} item(s) tratados."
            : $"🔧 Reparação: {fixedN} tratados, {failed.Count} não deram:\n"
              + string.Join("\n", failed.Select(l => { var p2 = l.Split('|'); return "• " + p2[1] + " — " + (p2.Length > 2 ? p2[2] : "sem detalhe"); }));
        PcCheckSummary.Text += "\n\n" + txt;
    }
}
