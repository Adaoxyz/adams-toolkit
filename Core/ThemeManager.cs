using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AdamsToolkit.Core;

/// <summary>
/// Modo noturno (default) / diurno. Os XAML referenciam os brushes Br* por
/// DynamicResource, por isso trocar os recursos ao nível da Application
/// repinta toda a UI em runtime. Preferência persiste em theme.txt.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string dark, string light)> Palette = new()
    {
        ["BrBg"]        = ("#08070C", "#F4F2F9"),
        // fundos das caixas semi-transparentes: as bolinhas do fundo veem-se por trás
        // de cada opção (pedido do dono 10-24). Avisos/confirmações usam BrBg (opaco).
        ["BrSurface"]   = ("#800B0911", "#B3FFFFFF"),
        ["BrCard"]      = ("#99100D17", "#BFFFFFFF"),
        ["BrCardHover"] = ("#B3171222", "#D9EFEAFB"),
        ["BrBorder"]    = ("#1F1930", "#E0D9F0"),
        ["BrAccent"]    = ("#A66BFF", "#6D28D9"),
        ["BrAccent2"]   = ("#7C3AED", "#5B21B6"),
        ["BrText"]      = ("#F1ECFA", "#16121F"),
        ["BrMuted"]     = ("#A89FBA", "#5E5670"),
        ["BrSuccess"]   = ("#3DDC97", "#0F8F5F"),
        ["BrDanger"]    = ("#FF5C7A", "#D6204A"),
        ["BrInfo"]      = ("#6EA8FF", "#2563EB"),
        ["BrWarn"]      = ("#FFB547", "#B45309"),
        ["BrActive"]       = ("#B31C1430", "#D9EDE5FD"),
        ["BrActiveBorder"] = ("#33264D", "#D6C7F7"),
        ["BrHeroBorder"]   = ("#2A2040", "#D6C7F7"),
    };

    public static bool IsLight { get; private set; }

    /// <summary>Tema trocado (UI thread) — para quem desenha em código (ex.: bolinhas).</summary>
    public static event Action? Changed;

    private static string PrefPath => Path.Combine(ConfigService.DataDir, "theme.txt");

    public static void Load()
    {
        var light = false;
        try { light = File.ReadAllText(PrefPath).Trim() == "light"; } catch { }
        Apply(light);
    }

    public static void Toggle()
    {
        Apply(!IsLight);
        try { File.WriteAllText(PrefPath, IsLight ? "light" : "dark"); }
        catch (Exception ex) { Log.Swallow("gravar tema", ex); }
    }

    private static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        foreach (var (key, colors) in Palette)
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(light ? colors.light : colors.dark));
            brush.Freeze();
            res[key] = brush;
        }

        // O gradiente de acento é StaticResource no App.xaml (nasce do tema escuro);
        // no modo diurno tem de ser reconstruído, senão fica com o violeta noturno.
        var g = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
        };
        g.GradientStops.Add(new GradientStop(Col(Palette["BrAccent"], light), 0));
        g.GradientStops.Add(new GradientStop(Col(Palette["BrAccent2"], light), 1));
        g.Freeze();
        res["BrAccentGradient"] = g;

        // logo: no claro só o "A" (o fumo e o quadrado escuro sujavam o fundo branco)
        var logo = new System.Windows.Media.Imaging.BitmapImage(new Uri(light ? "pack://application:,,,/Assets/logo-a.png" : "pack://application:,,,/Assets/logo.png"));
        logo.Freeze();
        res["ImgLogo"] = logo;
        var logoBg = new SolidColorBrush(light ? Colors.Transparent : (Color)ColorConverter.ConvertFromString("#150F22"));
        logoBg.Freeze();
        res["BrLogoBg"] = logoBg;
        Changed?.Invoke();
    }

    private static Color Col((string dark, string light) c, bool light) =>
        (Color)ColorConverter.ConvertFromString(light ? c.light : c.dark);
}
