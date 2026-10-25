using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdamsToolkit.Core;
using AdamsToolkit.Views;

namespace AdamsToolkit;

public partial class MainWindow : Window
{
    // 5 páginas na barra lateral; o resto vive dentro delas como separadores (TabHostView)
    private TabHostView? _home, _optimize, _game, _tools, _settingsPage;

    public static DiscordUser? CurrentUser { get; private set; }

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExiting;

    /// <summary>A janela viva (só há uma). Serve o atalho de emergência.</summary>
    private static MainWindow? _instance;

    public MainWindow()
    {
        _instance = this;
        InitializeComponent();
        ThemeManager.Load();
        Loaded += async (_, _) => await BootAsync();
        SourceInitialized += (_, _) => WindowChromeHelper.Apply(this);
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
            var openItem = menu.Items.Add(Lang.T("Abrir Adams Toolkit"), null, (_, _) => RestoreFromTray());

            var startupItem = new System.Windows.Forms.ToolStripMenuItem(Lang.T("Iniciar com o Windows"))
            {
                CheckOnClick = true,
                Checked = StartupManager.IsEnabled,
            };
            startupItem.CheckedChanged += (_, _) => StartupManager.SetEnabled(startupItem.Checked);
            // preferência pode mudar nas Definições entretanto — refrescar ao abrir o menu
            menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled;
            menu.Items.Add(startupItem);
            var exitItem = menu.Items.Add(Lang.T("Sair"), null, (_, _) => ExitApp());
            // bandeja é WinForms (não passa pelo gancho dos TextBlocks): troca à mão
            Lang.Changed += () => { openItem.Text = Lang.T("Abrir Adams Toolkit"); startupItem.Text = Lang.T("Iniciar com o Windows"); exitItem.Text = Lang.T("Sair"); };

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

    // ---------- atalho de emergência (Ctrl+Alt+A) ----------
    //
    // Com a interface do Windows desligada não há barra de tarefas NEM Alt+Tab: o
    // seletor de janelas do Windows 10/11 é desenhado pelo próprio explorer.exe e
    // morre com ele. Sem isto, quem minimizasse a janela ficava sem forma nenhuma
    // de lá voltar (relato do dono, 20-09-2026).
    //
    // O atalho é registado SÓ enquanto a interface está desligada — fora disso não
    // rouba Ctrl+Alt+A a mais ninguém.

    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x0A11;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private const uint VkA = 0x41;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _hotkeyHook;

    /// <summary>Nome do atalho, para as mensagens na UI ficarem sempre coerentes.</summary>
    public const string PanicHotkeyName = "Ctrl+Alt+A";

    /// <summary>
    /// Liga/desliga o atalho de emergência. Devolve false se o Windows recusou
    /// (outra app já tem Ctrl+Alt+A) — quem chama TEM de avisar antes de desligar
    /// a interface, senão o utilizador fica sem saída.
    /// </summary>
    public static bool SetPanicHotkey(bool on) => _instance?.SetPanicHotkeyCore(on) ?? false;

    private bool SetPanicHotkeyCore(bool on)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();
            var hwnd = helper.Handle;

            if (!on)
            {
                if (_hotkeyHook != null)
                {
                    UnregisterHotKey(hwnd, HotkeyId);
                    _hotkeyHook.RemoveHook(HotkeyProc);
                    _hotkeyHook = null;
                }
                return true;
            }

            if (_hotkeyHook != null) return true; // já ligado
            if (!RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkA))
            {
                Log.Warn($"não consegui registar {PanicHotkeyName} (outra app já o usa?)");
                return false;
            }
            _hotkeyHook = HwndSource.FromHwnd(hwnd);
            _hotkeyHook?.AddHook(HotkeyProc);
            Log.Info($"atalho de emergência {PanicHotkeyName} ligado", "explorer");
            return _hotkeyHook != null;
        }
        catch (Exception ex)
        {
            Log.Error("atalho de emergência falhou", ex);
            return false;
        }
    }

    private IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;
        handled = true;
        try { RestoreFromTray(); } catch (Exception ex) { Log.Swallow("atalho de emergência", ex); }
        return IntPtr.Zero;
    }

    // Na bandeja a app tem de ser uma pena: retirar a view do ContentHost dispara
    // o Unloaded dela → todos os timers/sensores/gráficos param (cada view já se
    // desliga no Unloaded). Ao restaurar, a view volta e o Loaded religa tudo.
    private UserControl? _hiddenView;

    private void HideToTray()
    {
        // Interface do Windows desligada por nós = não há bandeja onde pousar. Esconder
        // aqui deixava a janela sem ícone nenhum para voltar; minimiza-se apenas, que o
        // Alt+Tab continua a chegar-lhe.
        if (Core.ExplorerControl.TurnedOffByUs)
        {
            WindowState = WindowState.Minimized;
            return;
        }
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
        Crash.Silent = false; // a partir de agora há alguém a olhar para o ecrã
        AppState.SetShown(true);
        SetIdlePolling(false);
        SetIdleFootprint(false);
        if (_hiddenView != null)
        {
            _firstView = true; // voltar da bandeja não é "mudar de página": sem transição
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
        OverlayService.Shutdown();
        GameMode.Disable("(app a fechar)"); // nada fica pausado depois de sair
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        System.Windows.Application.Current.Shutdown();
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
        GameMode.RestoreLeftovers();          // app rebentou com o Modo Jogo ligado? retoma o que ficou pausado
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
            ShowAbout(changelog: true);
        }
        else ShowView(Page(NavDashboard), NavDashboard);
        Toast($"Bem-vindo, {user.DisplayName}");
        StartRevalidation();
        OverlayService.Apply(); // overlay ligado da última vez volta sozinho
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

    private void ShowAbout(bool changelog)
    {
        Go(NavSettings, "about");
        _settingsPage?.Get<AboutView>("about")?.SelectTab(changelog ? "changelog" : "opensource");
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
        OverlayService.Shutdown();
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
    //
    // 5 entradas na barra lateral (pedido do dono: "muita opção, organiza") e o resto
    // como separadores dentro de cada uma.

    private RadioButton? _lastNav;

    private TabHostView Page(RadioButton nav)
    {
        TabHostView Make(params TabHostView.Tab[] tabs)
        {
            var t = new TabHostView(tabs);
            t.TabChanged += label => { if (ReferenceEquals(ContentHost.Content, t)) UpdateCrumb(nav, label); };
            return t;
        }
        TabHostView.Tab T(string key, string label, Func<UserControl> create) => new(key, label, create);

        if (ReferenceEquals(nav, NavAjustes))
            return _optimize ??= Make(T("ajustes", "Ajustes", () => new AjustesView()), T("optimizer", "Boost", () => new OptimizerView()),
                                      T("gamemode", "Modo Jogo", () => new GameModeView()), T("timer", "Timer", () => new TimerResolutionView()));
        if (ReferenceEquals(nav, NavFiveM))
            return _game ??= Make(T("fivem", "FiveM", () => new FiveMCenterView()), T("overlay", "Overlay", () => new OverlayView()));
        if (ReferenceEquals(nav, NavInstall))
            return _tools ??= Make(T("rede", "Rede", () => new RedeView()), T("install", "Instalar", () => new InstallCenterView()),
                                   T("uninstall", "Desinstalar", () => new UninstallerView()));
        if (ReferenceEquals(nav, NavSettings))
            return _settingsPage ??= Make(T("settings", "Definições", () => new SettingsView()), T("about", "Sobre", () => new AboutView()));
        return _home ??= Make(T("home", "Visão geral", () => new DashboardView()), T("pc", "O meu PC", () => new PcSpecsView()),
                              T("diag", "Diagnóstico", () => new DiagnosticoView()));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ContentHost == null || sender is not RadioButton nav) return; // durante InitializeComponent
        // Editor de Clips: entrada própria na barra, abre a janela dele e devolve a seleção
        if (ReferenceEquals(nav, NavEditor))
        {
            EditorWindow.Open();
            var back = _lastNav ?? NavDashboard;
            Dispatcher.BeginInvoke(() => back.IsChecked = true);
            return;
        }
        _lastNav = nav;
        ShowView(Page(nav), nav);
    }

    /// <summary>Abre uma página e, opcionalmente, um separador dela.</summary>
    private void Go(RadioButton nav, string? tab = null)
    {
        if (nav.IsChecked != true) nav.IsChecked = true;
        if (tab != null) Page(nav).Select(tab);
    }

    // Ações rápidas, diagnóstico, etc.: chave → página + separador
    public void NavigateTo(string key)
    {
        switch (key)
        {
            case "ajustes" or "optimizer" or "gamemode" or "timer": Go(NavAjustes, key); break;
            case "fivem" or "overlay": Go(NavFiveM, key); break;
            case "rede" or "install": Go(NavInstall, key); break;
            case "pc" or "diag": Go(NavDashboard, key); break;
            case "settings": Go(NavSettings, "settings"); break;
            case "opensource": ShowAbout(changelog: false); break;
            case "editor": EditorWindow.Open(); break;
        }
    }

    // nome + subtítulo de cada página (breadcrumb no topo e transição ao entrar)
    private (string title, string sub) PageInfo(RadioButton? nav) => nav switch
    {
        _ when ReferenceEquals(nav, NavAjustes) => ("Otimizar", "Ajustes · Boost · Modo Jogo · Timer"),
        _ when ReferenceEquals(nav, NavFiveM) => ("Jogo", "FiveM · Overlay"),
        _ when ReferenceEquals(nav, NavInstall) => ("Ferramentas", "Rede · Instalar · Desinstalar"),
        _ when ReferenceEquals(nav, NavSettings) => ("Definições", "Preferências · Sobre"),
        _ => ("Início", "Visão geral · O meu PC · Diagnóstico"),
    };

    private void UpdateCrumb(RadioButton? nav, string tab) => PageCrumb.Text = $"{PageInfo(nav).title} · {tab}";

    private void ShowView(UserControl view, RadioButton? nav = null)
    {
        view.RenderTransform = new TranslateTransform();
        ContentHost.Content = view;
        if (FindResource("ViewFadeIn") is Storyboard sb)
            sb.Begin(view);

        var (title, sub) = PageInfo(nav ?? _lastNav);
        // textos em PT: o gancho do Lang traduz e guarda o original (troca de idioma ao vivo)
        PageCrumb.Text = view is TabHostView th ? $"{title} · {th.CurrentLabel}" : title;
        PlayTransition(title, sub);
        Lang.Apply(view); // view em cache pode ter sido vista noutro idioma
    }

    // Igual ao NX: o nome da página aparece ao centro e some (~0.6 s). Só opacidade —
    // nada de desfoque, que pesa em PCs fracos.
    private bool _firstView = true;
    private void PlayTransition(string title, string sub)
    {
        if (_firstView) { _firstView = false; return; } // a 1ª página (após login) entra sem isto
        TransTitle.Text = title;
        TransSub.Text = sub;
        TransLayer.Visibility = Visibility.Visible;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.Completed += (_, _) => TransLayer.Visibility = Visibility.Collapsed;
        TransLayer.BeginAnimation(OpacityProperty, a);
    }

    // ---- avisos rápidos (canto inferior direito) ----

    /// <summary>Aviso curto que some sozinho em ~3 s. Seguro chamar de qualquer view.</summary>
    public static void Toast(string text) => _instance?.Dispatcher.Invoke(() => _instance.ShowToast(text));

    private void ShowToast(string text)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BrBg"), // opaco: lê-se por cima de qualquer coisa
            BorderBrush = (Brush)FindResource("BrActiveBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 380,
            Opacity = 0,
            Child = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("BrText") },
        };
        ToastHost.Children.Add(card);
        while (ToastHost.Children.Count > 3) ToastHost.Children.RemoveAt(0);
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3600))));
        a.Completed += (_, _) => ToastHost.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, a);
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
