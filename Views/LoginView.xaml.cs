using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

public partial class LoginView : UserControl
{
    public event Action<DiscordUser>? LoginSucceeded;

    private string? _code;
    private string? _authUrl;
    private DispatcherTimer? _pollTimer;
    private bool _polling;

    public LoginView()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopPolling();
    }

    /// <summary>Tenta reutilizar sessão guardada (revalida presença no guild).</summary>
    public async Task TryAutoLoginAsync()
    {
        var token = DiscordAuth.LoadSession();
        if (token == null)
        {
            await StartPairingAsync(); // primeira vez neste PC — único momento com código
            return;
        }

        StatusText.Text = "A verificar acesso…";
        var r = await DiscordAuth.ValidateAsync(token);
        if (r.User != null)
        {
            DiscordAuth.SaveOfflineSnapshot(r.User);
            LoginSucceeded?.Invoke(r.User);
            return;
        }
        if (r.NetworkError)
        {
            // servidor de verificação em baixo — modo offline com o último login
            // validado (graça de 7 dias); revogação a sério continua a bloquear
            var offline = DiscordAuth.LoadOfflineUser();
            if (offline != null)
            {
                LoginSucceeded?.Invoke(offline);
                return;
            }
        }
        else DiscordAuth.ClearOfflineSnapshot(); // revogado: mata também o modo offline
        // NÃO limpa a sessão nem gera código novo: token inválido aqui = saiu do guild
        // revogada, não sessão perdida. (Re-emparelhar é permitido no backend —
        // PC formatado sem session.json cai no StartPairingAsync acima.)
        // Se o jogador reentrar no Discord/recuperar a tag, "Tentar novamente" volta a funcionar.
        ShowBlocked(r.NetworkError
            ? "Sem ligação ao servidor de verificação.\nVerifica a tua internet e tenta novamente."
            : "Acesso revogado — precisas de estar no Discord Shame & Adão.\nReentra no servidor e tenta novamente.");
    }

    /// <summary>Chamado pelo MainWindow quando a verificação periódica falha.</summary>
    public void ShowRevoked() =>
        ShowBlocked("Acesso revogado — saíste do Discord Shame & Adão.\nReentra no servidor e tenta novamente.");

    private void ShowBlocked(string message)
    {
        StopPolling();
        _code = null;
        _authUrl = null;
        CodeText.Text = "🔒";
        AuthBtn.IsEnabled = false;
        CopyBtn.IsEnabled = false;
        NewCodeBtn.Visibility = Visibility.Collapsed;
        RetryBtn.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private async Task StartPairingAsync()
    {
        StopPolling();
        NewCodeBtn.Visibility = Visibility.Collapsed;
        AuthBtn.IsEnabled = false;
        CopyBtn.IsEnabled = false;
        CodeText.Text = "· · · · · ·";
        StatusText.Text = "A ligar ao servidor…";

        var start = await DiscordAuth.StartPairingAsync();
        _code = start?.Code;
        _authUrl = start?.AuthUrl;
        if (_code == null)
        {
            CodeText.Text = "——————";
            StatusText.Text = "Servidor de verificação inacessível.\nVerifica a tua internet e tenta de novo.";
            NewCodeBtn.Visibility = Visibility.Visible;
            return;
        }

        CodeText.Text = string.Join(" ", _code.ToCharArray());
        AuthBtn.IsEnabled = _authUrl != null;
        CopyBtn.IsEnabled = true;
        StatusText.Text = "Clica em Entrar com Discord — 1 clique no browser e entras.";

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _pollTimer.Tick += async (_, _) => await PollOnceAsync();
        _pollTimer.Start();
    }

    private void Auth_Click(object sender, RoutedEventArgs e)
    {
        if (_authUrl == null) return;
        DownloadManager.OpenUrl(_authUrl);
        StatusText.Text = "À espera da autorização no browser…";
    }

    private async Task PollOnceAsync()
    {
        if (_polling || _code == null) return;
        _polling = true;
        try
        {
            var r = await DiscordAuth.PollAsync(_code);
            switch (r.Status)
            {
                case "ok" when r.Token != null && r.User != null:
                    StopPolling();
                    DiscordAuth.SaveSession(r.Token);
                    DiscordAuth.SaveOfflineSnapshot(r.User);
                    StatusText.Text = $"Bem-vindo, {r.User.DisplayName}!";
                    LoginSucceeded?.Invoke(r.User);
                    break;
                case "expired":
                    StopPolling();
                    StatusText.Text = "Pedido expirado (10 min). Tenta de novo.";
                    AuthBtn.IsEnabled = false;
                    CopyBtn.IsEnabled = false;
                    NewCodeBtn.Visibility = Visibility.Visible;
                    break;
                // pending/error: continua a tentar em silêncio
            }
        }
        finally { _polling = false; }
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_code == null) return;
        try
        {
            Clipboard.SetText(_code);
            StatusText.Text = "Código copiado! Alternativa: /toolkit no Discord Shame & Adão.";
        }
        catch { }
    }

    private async void NewCode_Click(object sender, RoutedEventArgs e) => await StartPairingAsync();

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryBtn.Visibility = Visibility.Collapsed;
        await TryAutoLoginAsync();
    }

    private void JoinDiscord_Click(object sender, RoutedEventArgs e) =>
        DownloadManager.OpenUrl(ConfigService.Current.DiscordInvite);
}
