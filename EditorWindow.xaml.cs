using System.Windows;
using System.Windows.Input;
using AdamsToolkit.Core;
using AdamsToolkit.Views;

namespace AdamsToolkit;

/// <summary>
/// Janela do Editor de Clips (v1.71, ex-Adams Clips). Vive à parte da janela principal:
/// abre sozinha (ecrã de escolha / ficheiro de vídeo por argumento) ou a partir da sidebar.
/// FFmpeg é descarregado na 1ª abertura; até lá o editor não é sequer construído.
/// </summary>
public partial class EditorWindow : Window
{
    private static EditorWindow? _instance;
    private EditorView? _editor;
    private string? _pendingFile;
    private CancellationTokenSource? _setupCts;

    /// <summary>Uma janela só: abre (ou traz para a frente) e opcionalmente carrega um ficheiro.</summary>
    public static EditorWindow Open(string? file = null)
    {
        if (_instance == null)
        {
            _instance = new EditorWindow();
            _instance.Closed += (_, _) => _instance = null;
            _instance._pendingFile = file;
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            if (file != null) _ = _instance.LoadAsync(file);
        }
        return _instance;
    }

    public static bool IsOpen => _instance != null;

    private EditorWindow()
    {
        ThemeManager.Load(); // aberto sem o Toolkit: carrega o tema (caixas semi-transparentes, claro/escuro)
        InitializeComponent();
        VersionText.Text = $"v{SelfUpdater.CurrentVersion}";
        Loaded += async (_, _) => await EnsureReadyAsync();
        Closing += (_, _) => { _setupCts?.Cancel(); _editor?.Shutdown(); };
        // v1.97: janela opaca (sem AllowsTransparency) — ver nota no XAML. Cantos redondos pedidos ao
        // DWM (Win11; no Win10 ficam direitos, sem Clip = sem superfície intermédia a cada frame).
        SourceInitialized += (_, _) => WindowChromeHelper.Apply(this);
        StateChanged += (_, _) =>
        {
            var max = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(max ? 0 : 14);
            RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
            TitleBar.CornerRadius = new CornerRadius(max ? 0 : 13, max ? 0 : 13, 0, 0);
            MaximizeBtn.Content = max ? "🗗" : "🗖";
        };
    }

    private async Task EnsureReadyAsync()
    {
        if (FfmpegManager.IsReady) { Mount(); return; }
        SetupPanel.Visibility = Visibility.Visible;
        RetryBtn.Visibility = Visibility.Collapsed;
        _setupCts = new CancellationTokenSource();
        try
        {
            var prog = new Progress<(double pct, string msg)>(p => { SetupBar.Value = p.pct; SetupText.Text = p.msg; });
            await FfmpegManager.EnsureAsync(prog, _setupCts.Token);
            SetupPanel.Visibility = Visibility.Collapsed;
            Mount();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetupText.Text = "Não deu: " + ex.Message;
            RetryBtn.Visibility = Visibility.Visible;
        }
    }

    private void Mount()
    {
        if (_editor != null) return;
        Task.Run(VideoEditor.CleanupProxies);
        _editor = new EditorView();
        Host.Content = _editor;
        _editor.Focus();
        if (_pendingFile != null) { var f = _pendingFile; _pendingFile = null; _ = LoadAsync(f); }
    }

    private async Task LoadAsync(string file)
    {
        if (_editor == null) { _pendingFile = file; return; }
        try { await _editor.OpenAsync(file); } catch (Exception ex) { EditorPaths.LogCrash(ex); }
    }

    private async void Retry_Click(object s, RoutedEventArgs e) => await EnsureReadyAsync();

    // Do editor para o Toolkit: se a janela principal existir, mostra-a; senão cria-a (passa pelo login normal).
    private void Toolkit_Click(object s, RoutedEventArgs e) => App.ShowToolkit();

    // ---- chrome ----
    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Maximize_Click(s, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed && WindowState == WindowState.Normal) DragMove();
    }
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
