using System.Windows;
using AdamsToolkit.Core;

// Namespace RAIZ de propósito: o código em AdamsToolkit.* encontra este tipo antes de
// olhar para os usings (System.Windows.MessageBox), sem ambiguidade.
namespace AdamsToolkit;

/// <summary>
/// Sombra de System.Windows.MessageBox: o código da app (namespace AdamsToolkit.*)
/// resolve "MessageBox" para esta classe antes dos usings, por isso todos os avisos
/// passam a ser traduzidos sem tocar em cada chamada.
/// </summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text) =>
        System.Windows.MessageBox.Show(Lang.T(text));
    public static MessageBoxResult Show(string text, string caption) =>
        System.Windows.MessageBox.Show(Lang.T(text), Lang.T(caption));
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) =>
        System.Windows.MessageBox.Show(Lang.T(text), Lang.T(caption), button);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(Lang.T(text), Lang.T(caption), button, icon);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult def) =>
        System.Windows.MessageBox.Show(Lang.T(text), Lang.T(caption), button, icon, def);
    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(owner, Lang.T(text), Lang.T(caption), button, icon);
}
