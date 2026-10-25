using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class OverlayView : UserControl
{
    private record MetricRow(OverlayMetric M, string Label, bool On, Brush Swatch);

    private static readonly string[] Palette = { "#FFFFFF", "#3DDC97", "#6EA8FF", "#A66BFF", "#FFB547", "#FF5C7A", "#38BDF8" };
    private bool _syncing;

    public OverlayView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SyncControls();
            OverlayService.Attach(PreviewText);
            if (AppState.IsVisible) OverlayService.AddPreview();
            AppState.Changed += OnVisibility;
        };
        Unloaded += (_, _) =>
        {
            AppState.Changed -= OnVisibility;
            OverlayService.Detach(PreviewText);
            if (_previewing) OverlayService.RemovePreview();
            _previewing = false;
        };
    }

    // pré-visualização só amostra com a janela à vista (o overlay em si segue ligado)
    private bool _previewing;
    private void OnVisibility(bool visible)
    {
        if (visible && !_previewing) { OverlayService.AddPreview(); _previewing = true; }
        else if (!visible && _previewing) { OverlayService.RemovePreview(); _previewing = false; }
    }

    private void SyncControls()
    {
        _previewing = AppState.IsVisible;
        _syncing = true;
        var s = OverlayService.Settings;
        EnableToggle.IsChecked = s.Enabled;
        (s.Position switch { "TR" => PosTR, "BL" => PosBL, "BR" => PosBR, _ => PosTL }).IsChecked = true;
        (s.FontSize switch { <= 11 => Size11, >= 16 => Size16, _ => Size13 }).IsChecked = true;
        PlacePreview();
        _syncing = false;
        RefreshMetrics();
    }

    private void RefreshMetrics() =>
        MetricList.ItemsSource = OverlayService.Settings.Metrics
            .Select(m => new MetricRow(m, OverlayService.Label(m.Key), m.On, ToBrush(m.Color))).ToList();

    private static Brush ToBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.White; }
    }

    private void PlacePreview()
    {
        var p = OverlayService.Settings.Position;
        PreviewChip.HorizontalAlignment = p is "TR" or "BR" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PreviewChip.VerticalAlignment = p is "BL" or "BR" ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    private void Enable_Click(object sender, RoutedEventArgs e) => OverlayService.SetEnabled(EnableToggle.IsChecked == true);

    private void Pos_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag }) return;
        OverlayService.Settings.Position = tag;
        PlacePreview();
        OverlayService.SaveSettings();
    }

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag }) return;
        OverlayService.Settings.FontSize = double.Parse(tag);
        OverlayService.SaveSettings();
    }

    private void Metric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MetricRow row } cb) return;
        row.M.On = cb.IsChecked == true;
        OverlayService.SaveSettings();
        RefreshMetrics();
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MetricRow row }) return;
        var i = Array.IndexOf(Palette, row.M.Color.ToUpperInvariant());
        row.M.Color = Palette[(i + 1) % Palette.Length];
        OverlayService.SaveSettings();
        RefreshMetrics();
    }
}
