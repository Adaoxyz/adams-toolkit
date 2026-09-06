namespace AdamsToolkit.Core;

/// <summary>
/// Estado global "a app está a ser vista?" — regra da pena: nada de sensores,
/// timers ou desenho quando o jogador não está a olhar para a janela (na bandeja
/// ou com o jogo à frente). As views consultam <see cref="IsVisible"/> e ligam-se
/// ao <see cref="Changed"/> para se suspenderem/retomarem sozinhas.
/// </summary>
public static class AppState
{
    private static bool _shown = true;   // janela aberta (não está na bandeja)
    private static bool _active = true;  // janela em primeiro plano

    /// <summary>Vale a pena gastar CPU? (janela aberta E em primeiro plano)</summary>
    public static bool IsVisible => _shown && _active;

    /// <summary>Disparado quando o estado muda (thread de UI).</summary>
    public static event Action<bool>? Changed;

    public static void SetShown(bool v) => Set(ref _shown, v);
    public static void SetActive(bool v) => Set(ref _active, v);

    private static void Set(ref bool field, bool v)
    {
        if (field == v) return;
        var before = IsVisible;
        field = v;
        if (IsVisible != before) Changed?.Invoke(IsVisible);
    }
}
