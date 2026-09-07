using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdamsToolkit.Core;
using AdamsToolkit.Views;

namespace AdamsToolkit;

public partial class MainWindow : Window
{
    private DashboardView? _dashboard;
    private InstallCenterView? _install;
    private FiveMCenterView? _fivem;
    private UninstallerView? _uninstaller;
    private OptimizerView? _optimizer;
    private TimerResolutionView? _timerRes;
    private PcSpecsView? _pcSpecs;
    private ChangelogView? _changelog;
    private OpenSourceView? _openSource;

    public static DiscordUser? CurrentUser { get; private set; }

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExiting;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Load();
        ThemeBtn.Content = ThemeManager.IsLight ? "🌙" : "☀️";
        Loaded += async (_, _) => await BootAsync();
        // Jogo à frente = app suspensa (ver AppState): os monitores desligam-se
        // enquanto a janela não estiver em primeiro plano.
        Activated += (_, _) => { AppState.SetActive(true); SetPriority(false); };
        Deactivated += (_, _) => { AppState.SetActive(false); SetPriority(true); };
        InitTray();
        InitShowSignal();
    }

    // Arranque do Windows (--startup): a janela nunca chega a aparecer — nasce
    // direto na bandeja. Como o Loaded não dispara sem Show(), o boot (update,
    // auto-login, revalidação) é lançado daqui.
    public void StartInTray()
    {
        if (_tray == null) { Show(); return; } // sem bandeja não dá para esconder
        _tray.Visible = true;
        AppState.SetShown(false); // nasce invisível: nenhum monitor arranca
        SetIdleFootprint(true);
        Dispatcher.BeginInvoke(async () => await BootAsync());
    }

    // Quando uma 2ª instância tenta abrir, sinaliza este evento — nós trazemos a
    // janela existente (mesmo que esteja na bandeja) para a frente.
    private System.Threading.EventWaitHandle? _showEvent;
    private void InitShowSignal()
    {
        try
        {
            _showEvent = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, App.ShowEventName);
            var t = new System.Threading.Thread(() =>
            {
                while (_showEvent != null && _showEvent.WaitOne())
                    Dispatcher.Invoke(RestoreFromTray);
            })
            { IsBackground = true };
            t.Start();
        }
        catch { _showEvent = null; }
    }

    // Fechar (X) e minimizar escondem para a bandeja do Windows (a seta ^ em
    // baixo à direita), em silêncio — sem balão nem notificação. A app continua
    // a correr. Sair mesmo só pelo menu da bandeja.
    private void InitTray()
    {
        try
        {
            System.Drawing.Icon icon;
            var res = System.Windows.Application.GetResourceStream(
                new Uri("Assets/app.ico", UriKind.Relative));
            using (var s = res?.Stream) { icon = s != null ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application; }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Abrir Adams Toolkit", null, (_, _) => RestoreFromTray());

            var startupItem = new System.Windows.Forms.ToolStripMenuItem("Iniciar com o Windows")
            {
                CheckOnClick = true,
                Checked = StartupManager.IsEnabled,
            };
            startupItem.CheckedChanged += (_, _) => StartupManager.SetEnabled(startupItem.Checked);
            // preferência pode mudar no dashboard entretanto — refrescar ao abrir o menu
            menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled;
            menu.Items.Add(startupItem);

            menu.Items.Add("Sair", null, (_, _) => ExitApp());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Adams Toolkit",
                Visible = false,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch { _tray = null; }
    }

    // Na bandeja a app tem de ser uma pena: retirar a view do ContentHost dispara
    // o Unloaded dela → todos os timers/sensores/gráficos param (cada view já se
    // desliga no Unloaded). Ao restaurar, a view volta e o Loaded religa tudo.
    private UserControl? _hiddenView;

    private void HideToTray()
    {
        if (_tray == null) { ExitApp(); return; } // sem bandeja não há para onde esconder
        if (ContentHost?.Content is UserControl v)
        {
            _hiddenView = v;
            ContentHost.Content = null;
        }
        Hide();
        _tray.Visible = true;
        AppState.SetShown(false);
        // Sensores fora: fecha a LibreHardwareMonitor (descarrega o driver de
        // kernel e liberta a RAM dela). Volta a abrir sozinha na 1ª leitura.
        Task.Run(HardwareMonitor.Shutdown);
        SystemMonitor.ReleaseCounters();
        SetIdlePolling(true);
        SetIdleFootprint(true);
    }

    public void RestoreFromTray()
    {
        AppState.SetShown(true);
        SetIdlePolling(false);
        SetIdleFootprint(false);
        if (_hiddenView != null)
        {
            ShowView(_hiddenView);
            _hiddenView = null;
        }
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // traz para a frente
        if (_tray != null) _tray.Visible = false;
    }

    // Bandeja = prioridade abaixo do normal (nunca rouba CPU a jogos) + devolve a
    // RAM não usada ao Windows (o working set no Gestor de Tarefas fica mínimo).
    private static void SetIdleFootprint(bool idle)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.PriorityClass = idle ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            if (idle)
            {
                GC.Collect(2, GCCollectionMode.Optimized);
                SetProcessWorkingSetSize(p.Handle, -1, -1);
            }
        }
        catch { }
    }

    // Alt-tab para o jogo = prioridade abaixo do normal (sem mexer na RAM: cortar
    // o working set a cada alt-tab só provocava falhas de página ao voltar).
    private static void SetPriority(bool background)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr handle, nint min, nint max);

    private void ExitApp()
    {
        _reallyExiting = true;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        System.Windows.Application.Current.Shutdown();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        ThemeBtn.Content = ThemeManager.IsLight ? "🌙" : "☀️";
    }

    private bool _booted;
    private async Task BootAsync()
    {
        // StartInTray já pode ter arrancado o boot; abrir a janela depois
        // (Loaded) não o deve repetir.
        if (_booted) return;
        _booted = true;

        VersionText.Text = $"v{SelfUpdater.CurrentVersion}";
        SelfUpdater.CleanupOldVersion();
        StartupManager.Sync(); // aplica preferência "iniciar com o Windows" (regista ou remove)
        _ = Task.Run(ScheduledCleanup.Sync); // re-escreve a tarefa semanal se estiver ligada (caminho do exe)
        NetworkOptimizer.BeginStartupCheck(); // boost é por sessão: PC reiniciou → reverte sozinho
        GameBoost.BeginStartupCheck();        // idem para o boost de FPS
        // 2026-09-07 (dono: "retira o Snap Tap do aplicativo, desativa de vez"):
        // a funcionalidade saiu da app. Nao ha nada para restaurar no arranque e
        // apaga-se o ficheiro de estado que ficou nos PCs que a chegaram a ligar,
        // para nao restar nada no disco nem num screenshare.
        try
        {
            var snapCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "AdamsToolkit", "snaptap.json");
            if (File.Exists(snapCfg)) File.Delete(snapCfg);
        }
        catch { /* em uso ou sem permissoes: nao vale a pena chatear o utilizador */ }
        KeyboardRepeat.RestoreOnBoot();       // FilterKeys WASD idem
        TimerResolutionService.SetMaximum(); // pedido do dono: timer 0.5 ms sempre que a app está aberta (filho morre com a app)

        var login = new LoginView();
        login.LoginSucceeded += OnLoginSucceeded;
        LoginHost.Content = login;

        await ConfigService.LoadAsync();

        // auto-update: se houver versão nova, troca o exe e reinicia
        var updated = await SelfUpdater.TryUpdateAsync(msg => Dispatcher.Invoke(() =>
        {
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateText.Text = msg;
        }));
        if (updated)
        {
            Application.Current.Shutdown();
            return;
        }
        UpdateBanner.Visibility = Visibility.Collapsed;

        await login.TryAutoLoginAsync();
    }

    private void OnLoginSucceeded(DiscordUser user)
    {
        CurrentUser = user;
        UserNameText.Text = user.DisplayName;
        try
        {
            AvatarBrush.ImageSource = new BitmapImage(new Uri(user.AvatarUrl));
        }
        catch { }

        LoginHost.Content = null;
        LoginHost.Visibility = Visibility.Collapsed;
        MainArea.Visibility = Visibility.Visible;
        if (ChangelogUnseen())
        {
            // versão nova (ou 1ª instalação): mostra as Novidades uma vez,
            // para o jogador ver o que mudou em linguagem simples
            MarkChangelogSeen();
            NavChangelog.IsChecked = true;
        }
        else ShowView(GetDashboard());
        StartRevalidation();
    }

    // ---- Novidades: engrenagem no topo + abertura automática pós-update ----

    private static readonly string SeenVersionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdamsToolkit", "seen-version.txt");

    private static bool ChangelogUnseen()
    {
        try { return File.ReadAllText(SeenVersionFile).Trim() != SelfUpdater.CurrentVersion; }
        catch { return true; }
    }

    private static void MarkChangelogSeen()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeenVersionFile)!);
            File.WriteAllText(SeenVersionFile, SelfUpdater.CurrentVersion);
        }
        catch { }
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (MainArea.Visibility != Visibility.Visible) return; // antes do login não há navegação
        NavChangelog.IsChecked = true;
    }

    // Verificação contínua: a cada 10 min confirma no backend que o jogador
    // continua no Discord Shame & Adão; se não, bloqueia na hora.
    // (Erros de rede não bloqueiam — só uma resposta negativa do servidor.)
    private DispatcherTimer? _revalidateTimer;
    private DispatcherTimer? _presenceTimer;

    private void StartRevalidation()
    {
        _revalidateTimer?.Stop();
        _revalidateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _revalidateTimer.Tick += async (_, _) =>
        {
            var token = DiscordAuth.LoadSession();
            if (token == null) return;
            var r = await DiscordAuth.ValidateAsync(token);
            if (r.User != null) DiscordAuth.SaveOfflineSnapshot(r.User); // renova a graça offline
            else if (!r.NetworkError) LockOut();
        };
        _revalidateTimer.Start();

        // Presença: ping leve a cada 60s enquanto a app está aberta —
        // alimenta o "🟢 A usar agora" do painel staff no Discord.
        _presenceTimer?.Stop();
        _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _presenceTimer.Tick += async (_, _) =>
        {
            var token = DiscordAuth.LoadSession();
            if (token != null) await DiscordAuth.PingAsync(token);
        };
        _presenceTimer.Start();
        SetIdlePolling(!AppState.IsVisible);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var token = DiscordAuth.LoadSession();
            if (token != null) await DiscordAuth.PingAsync(token);
        });
    }

    // Na bandeja a presença passa de 60s para 5 min: continua a marcar "a usar
    // agora" no painel staff, mas quase não acorda a rede nem o processo.
    private void SetIdlePolling(bool idle)
    {
        if (_presenceTimer == null) return;
        _presenceTimer.Interval = idle ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60);
    }

    private void LockOut()
    {
        DiscordAuth.ClearOfflineSnapshot(); // revogado: sem entrada offline
        _revalidateTimer?.Stop();
        _presenceTimer?.Stop();
        CurrentUser = null;
        MainArea.Visibility = Visibility.Collapsed;
        LoginHost.Visibility = Visibility.Visible;

        var login = new LoginView();
        login.LoginSucceeded += OnLoginSucceeded;
        LoginHost.Content = login;
        login.ShowRevoked();
    }

    // ---- navegação ----

    private DashboardView GetDashboard() => _dashboard ??= new DashboardView();

    private System.Windows.Controls.RadioButton? _lastNav;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ContentHost == null) return; // durante InitializeComponent
        // Editor de Clips é uma janela à parte (v1.71): abre-a e devolve a seleção ao item anterior
        if (ReferenceEquals(sender, NavEditor))
        {
            EditorWindow.Open();
            var back = _lastNav ?? NavDashboard;
            Dispatcher.BeginInvoke(() => back.IsChecked = true);
            return;
        }
        _lastNav = sender as System.Windows.Controls.RadioButton;
        UserControl view = sender switch
        {
            _ when ReferenceEquals(sender, NavInstall) => _install ??= new InstallCenterView(),
            _ when ReferenceEquals(sender, NavFiveM) => _fivem ??= new FiveMCenterView(),
            _ when ReferenceEquals(sender, NavUninstaller) => _uninstaller ??= new UninstallerView(),
            _ when ReferenceEquals(sender, NavOptimizer) => _optimizer ??= new OptimizerView(),
            _ when ReferenceEquals(sender, NavTimer) => _timerRes ??= new TimerResolutionView(),
            _ when ReferenceEquals(sender, NavPc) => _pcSpecs ??= new PcSpecsView(),
            _ when ReferenceEquals(sender, NavChangelog) => _changelog ??= new ChangelogView(),
            _ when ReferenceEquals(sender, NavOpenSource) => _openSource ??= new OpenSourceView(),
            _ => GetDashboard(),
        };
        ShowView(view);
    }

    // Usado pelas Ações Rápidas do dashboard: marca o item da sidebar
    // (o Checked trata da navegação e da view em cache).
    public void NavigateTo(string key)
    {
        switch (key)
        {
            case "optimizer": NavOptimizer.IsChecked = true; break;
            case "fivem": NavFiveM.IsChecked = true; break;
            case "install": NavInstall.IsChecked = true; break;
            case "pc": NavPc.IsChecked = true; break;
            case "opensource": NavOpenSource.IsChecked = true; break;
            case "editor": EditorWindow.Open(); break;
        }
    }

    private void ShowView(UserControl view)
    {
        view.RenderTransform = new TranslateTransform();
        ContentHost.Content = view;
        if (FindResource("ViewFadeIn") is Storyboard sb)
            sb.Begin(view);
    }

    // ---- chrome ----

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed && WindowState == WindowState.Normal)
            DragMove();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            RootBorder.CornerRadius = new CornerRadius(14);
            RootBorder.BorderThickness = new Thickness(1);
            MaximizeBtn.Content = "🗖";
        }
        else
        {
            WindowState = WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(0); // sem cantos nos limites do ecrã
            RootBorder.BorderThickness = new Thickness(0);
            MaximizeBtn.Content = "🗗";
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Minimizar (botão, taskbar ou Win+M) = segundo plano na bandeja, em silêncio.
        if (WindowState == WindowState.Minimized && _tray != null) HideToTray();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 / fecho do sistema também vai para a bandeja, a não ser que
        // seja saída real (menu Sair) ou a app esteja a desligar.
        if (!_reallyExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Core.HardwareMonitor.Shutdown(); // descarrega driver de kernel + apaga o .sys
        base.OnClosing(e);
    }
}
