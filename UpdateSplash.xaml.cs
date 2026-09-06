using System.Windows;

namespace AdamsToolkit;

/// <summary>Cartão pequeno enquanto se verifica/descarrega a atualização, ANTES do ecrã de escolha (v1.76).</summary>
public partial class UpdateSplash : Window
{
    public UpdateSplash() { InitializeComponent(); }
    public void Set(string msg) => Dispatcher.Invoke(() => Msg.Text = msg);
}
