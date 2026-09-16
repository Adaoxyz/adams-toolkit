using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

/// <summary>
/// Separador "Código Aberto": mostra onde está o código publicado e responde, dentro
/// da própria app, às perguntas de uma verificação de PC.
///
/// Existe porque jogadores já levaram ban por a app aparecer como "editor
/// desconhecido" num scanner. Quem está a verificar o PC abre este separador, clica
/// e lê o código — em vez de banir por dúvida. O estado da repetição de
/// teclado é lido ao vivo da configuração, e os ficheiros que o guardam são texto
/// simples, para o staff poder confirmar sem confiar no que a app mostra.
/// </summary>
public partial class OpenSourceView : UserControl
{
    public const string RepoUrl = "https://gitlab.com/adamstoolkit-group/adams-toolkit"; // 2026-09-16: GitHub (conta filtrada) -> GitLab

    private static readonly string[] Claims =
    {
        "Não lê nem escreve a memória de outros programas — não há uma única chamada a ReadProcessMemory ou WriteProcessMemory em todo o código.",
        "Não injeta nada dentro do FiveM nem do GTA V, e não altera ficheiros do jogo.",
        "Não instala drivers de kernel. A biblioteca de sensores que trazia um foi removida em agosto de 2026.",
        "Não apaga rasto do sistema: nada de ficheiros temporários, Prefetch, registos de eventos ou Reciclagem. A limpeza foi desligada de propósito.",
        "Não desliga o Windows Defender nem serviços de diagnóstico.",
        "Não envia dados do teu PC. Sai apenas o teu ID, nome e avatar do Discord, para validar o acesso.",
        "Tudo o que altera no Windows fica guardado com o valor original e tem botão para reverter; a maior parte volta ao normal quando reinicias o PC.",
    };

    public OpenSourceView()
    {
        InitializeComponent();

        RepoUrlText.Text = RepoUrl;
        ClaimList.ItemsSource = Claims;
        VersionText.Text = $"Versão instalada: v{SelfUpdater.CurrentVersion}";

        RefreshInputState();
    }

    /// <summary>Estado atual da repetição de teclado, com cor.</summary>
    private void RefreshInputState()
    {
        Paint(KeyRepeatState, KeyboardRepeat.Cfg.Enabled);

        void Paint(System.Windows.Controls.TextBlock tb, bool on)
        {
            tb.Text = on ? "LIGADO" : "DESLIGADO";
            tb.Foreground = (Brush)FindResource(on ? "BrWarn" : "BrSuccess");
        }
    }

    private void BtnRepo_Click(object sender, RoutedEventArgs e) => DownloadManager.OpenUrl(RepoUrl);

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RepoUrl);
            BtnCopy.Content = "Copiado!";
        }
        catch { BtnCopy.Content = "Não deu para copiar"; }
    }

    private void BtnOpenData_Click(object sender, RoutedEventArgs e)
        => DownloadManager.OpenFolder(ConfigService.DataDir);
}
