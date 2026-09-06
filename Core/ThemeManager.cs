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
        ["BrBg"]        = ("#0A0917", "#F3F1FB"),
        ["BrSurface"]   = ("#0F0D20", "#FFFFFF"),
        ["BrCard"]      = ("#14122B", "#FFFFFF"),
        ["BrCardHover"] = ("#1C1839", "#EDE9FA"),
        ["BrBorder"]    = ("#2A2450", "#DDD5F3"),
        ["BrAccent"]    = ("#A855F7", "#7C3AED"),
        ["BrAccent2"]   = ("#7C3AED", "#5B21B6"),
        ["BrText"]      = ("#EAE7F7", "#171334"),
        ["BrMuted"]     = ("#8B86AE", "#5F5A80"),
        ["BrSuccess"]   = ("#34D399", "#0F9D6E"),
        ["BrDanger"]    = ("#F87171", "#DC2626"),
        ["BrInfo"]      = ("#38BDF8", "#0284C7"),
        ["BrWarn"]      = ("#FBBF24", "#B45309"),
    };

    public static bool IsLight { get; private set; }

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
        try { File.WriteAllText(PrefPath, IsLight ? "light" : "dark"); } catch { }
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
    }

    private static Color Col((string dark, string light) c, bool light) =>
        (Color)ColorConverter.ConvertFromString(light ? c.light : c.dark);
}
