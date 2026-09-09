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
        InitializeComponent();
        VersionText.Text = $"v{SelfUpdater.CurrentVersion}";
        Loaded += async (_, _) => await EnsureReadyAsync();
        Closing += (_, _) => { _setupCts?.Cancel(); _editor?.Shutdown(); };
        // v1.97: janela opaca (sem AllowsTransparency) — ver nota no XAML. Cantos redondos pedidos ao
        // DWM (Win11; no Win10 ficam direitos, sem Clip = sem superfície intermédia a cada frame).
        SourceInitialized += (_, _) => { ApplyDwmCorners(); HookMinMax(); };
        StateChanged += (_, _) =>
        {
            var max = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(max ? 0 : 14);
            RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
            TitleBar.CornerRadius = new CornerRadius(max ? 0 : 13, max ? 0 : 13, 0, 0);
            MaximizeBtn.Content = max ? "🗗" : "🗖";
        };
    }

    // ---- DWM / Win32 (só chrome; nada disto toca no vídeo) ----
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    private void ApplyDwmCorners()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int pref = 2; // DWMWCP_ROUND — DWMWA_WINDOW_CORNER_PREFERENCE (33), Win11 22000+; noutros Windows devolve erro e ignora-se
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    // WindowStyle=None + maximizar: sem isto a janela "sai" 6-8 px para fora do monitor.
    // Mantém-se o comportamento antigo (ecrã inteiro, por cima da barra de tarefas).
    private void HookMinMax()
    {
        var src = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        src?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
            try
            {
                var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (mon == IntPtr.Zero) return IntPtr.Zero;
                var mi = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var r = mi.Monitor;
                mmi.MaxPosition = new POINT { X = 0, Y = 0 }; // relativo ao monitor
                mmi.MaxSize = new POINT { X = r.Right - r.Left, Y = r.Bottom - r.Top };
                mmi.MaxTrackSize = mmi.MaxSize;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            catch { }
            return IntPtr.Zero;
        });
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
