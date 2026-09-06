using System.Windows;
using System.Windows.Input;
using AdamsToolkit.Core;

namespace AdamsToolkit;

/// <summary>Ecrã de arranque (v1.71): Toolkit ou Editor de Clips. Fecha sem escolher = sai.</summary>
public partial class ChooserWindow : Window
{
    /// <summary>StartMode.Toolkit / StartMode.Editor, ou null se fechou.</summary>
    public string? Choice { get; private set; }

    public ChooserWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { Activate(); BtnToolkit.Focus(); };
    }

    private void Pick(string mode)
    {
        Choice = mode;
        Close();
    }

    private void Toolkit_Click(object s, RoutedEventArgs e) => Pick(StartMode.Toolkit);
    private void Editor_Click(object s, RoutedEventArgs e) => Pick(StartMode.Editor);
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            // só Enter (no cartão focado) — sem letras: um "E" perdido no teclado abria o editor
            case Key.Enter when BtnEditor.IsKeyboardFocused: Pick(StartMode.Editor); break;
            case Key.Enter when BtnToolkit.IsKeyboardFocused: Pick(StartMode.Toolkit); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }
}
