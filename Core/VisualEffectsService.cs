using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdamsToolkit.Core;

/// <summary>
/// Réplica dos 3 presets do diálogo "Opções de Desempenho → Efeitos Visuais"
/// (SystemPropertiesPerformance.exe): 0 = o Windows decide, 1 = melhor aspeto,
/// 2 = melhor desempenho. Escreve o mesmo valor que o diálogo
/// (HKCU\...\Explorer\VisualEffects\VisualFXSetting) e aplica cada efeito via
/// SystemParametersInfo / registo, com WM_SETTINGCHANGE no fim. Só HKCU — sem admin.
/// Permanente (igual ao diálogo do Windows); o utilizador volta atrás escolhendo outro preset.
/// </summary>
public static class VisualEffectsService
{
    public const int ModeWindows = 0, ModeAppearance = 1, ModePerformance = 2, ModeCustom = 3;

    private const string FxKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";

    // ---- P/Invoke ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    private const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
    private const uint SPIF = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    private const uint SPI_SETDRAGFULLWINDOWS = 0x0025, SPI_SETANIMATION = 0x0049, SPI_SETFONTSMOOTHING = 0x004B,
        SPI_SETMENUANIMATION = 0x1003, SPI_SETCOMBOBOXANIMATION = 0x1005, SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007,
        SPI_SETGRADIENTCAPTIONS = 0x1009, SPI_SETMENUFADE = 0x1013, SPI_SETSELECTIONFADE = 0x1015,
        SPI_SETTOOLTIPANIMATION = 0x1017, SPI_SETTOOLTIPFADE = 0x1019, SPI_SETCURSORSHADOW = 0x101B,
        SPI_SETDROPSHADOW = 0x1025, SPI_SETUIEFFECTS = 0x103F, SPI_SETCLIENTAREAANIMATION = 0x1043;

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Modo atual (0/1/2/3) lido do registo; 3 = personalizado.</summary>
    public static int GetMode()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(FxKey);
            return k?.GetValue("VisualFXSetting") is int v && v is >= 0 and <= 3 ? v : ModeCustom;
        }
        catch { return ModeCustom; }
    }

    public static string ModeLabel(int mode) => mode switch
    {
        ModeWindows => "O Windows decide",
        ModeAppearance => "Melhor aspeto",
        ModePerformance => "Melhor desempenho",
        _ => "Personalizado",
    };

    /// <summary>Aplica um preset. Devolve lista de erros (vazia = tudo OK).</summary>
    public static List<string> Apply(int mode)
    {
        var errors = new List<string>();
        // efeito → (nome da subchave em VisualEffects p/ ler DefaultValue, ação on/off)
        var effects = new (string key, Action<bool> set)[]
        {
            ("AnimateMinMax",          on => SetAnimation(on)),
            ("ComboBoxAnimation",      on => Spi(SPI_SETCOMBOBOXANIMATION, on)),
            ("ControlAnimations",      on => Spi(SPI_SETCLIENTAREAANIMATION, on)),
            ("CursorShadow",           on => Spi(SPI_SETCURSORSHADOW, on)),
            ("DWMAeroPeekEnabled",     on => Reg(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", on ? 1 : 0)),
            ("DWMSaveThumbnailEnabled",on => Reg(@"Software\Microsoft\Windows\DWM", "AlwaysHibernateThumbnails", on ? 1 : 0)),
            ("DragFullWindows",        on => Spi(SPI_SETDRAGFULLWINDOWS, on, asParam: true)),
            ("DropShadow",             on => Spi(SPI_SETDROPSHADOW, on)),
            ("FontSmoothing",          on => Spi(SPI_SETFONTSMOOTHING, on, asParam: true)),
            ("ListBoxSmoothScrolling", on => Spi(SPI_SETLISTBOXSMOOTHSCROLLING, on)),
            ("ListviewAlphaSelect",    on => Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect", on ? 1 : 0)),
            ("ListviewShadow",         on => Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow", on ? 1 : 0)),
            ("MenuAnimation",          on => { Spi(SPI_SETMENUANIMATION, on); Spi(SPI_SETMENUFADE, on); }),
            ("SelectionFade",          on => Spi(SPI_SETSELECTIONFADE, on)),
            ("TaskbarAnimations",      on => Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", on ? 1 : 0)),
            ("ThumbnailsOrIcon",       on => Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "IconsOnly", on ? 0 : 1)),
            ("TooltipAnimation",       on => { Spi(SPI_SETTOOLTIPANIMATION, on); Spi(SPI_SETTOOLTIPFADE, on); }),
            ("Themes",                 on => Spi(SPI_SETGRADIENTCAPTIONS, on)),
        };

        // master switch (SPI_SETUIEFFECTS) — desempenho desliga tudo de uma vez
        try { Spi(SPI_SETUIEFFECTS, mode != ModePerformance); } catch (Exception ex) { errors.Add("UIEffects: " + ex.Message); }

        foreach (var (key, set) in effects)
        {
            bool on = mode switch
            {
                ModeAppearance => true,
                ModePerformance => false,
                _ => DefaultFor(key),
            };
            try { set(on); } catch (Exception ex) { errors.Add($"{key}: {ex.Message}"); }
        }

        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(FxKey);
            k?.SetValue("VisualFXSetting", mode, RegistryValueKind.DWord);
        }
        catch (Exception ex) { errors.Add("VisualFXSetting: " + ex.Message); }

        // avisar Explorer/DWM (barra de tarefas, listas, miniaturas)
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "WindowMetrics", SMTO_ABORTIFHUNG, 2000, out _);
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ShellState", SMTO_ABORTIFHUNG, 2000, out _);
        }
        catch { /* best-effort */ }

        return errors;
    }

    /// <summary>"O Windows decide" = DefaultValue de cada subchave (é isto que o diálogo usa).</summary>
    private static bool DefaultFor(string key)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(FxKey + "\\" + key)
                       ?? Registry.LocalMachine.OpenSubKey(FxKey + "\\" + key);
            return k?.GetValue("DefaultValue") is int v ? v != 0 : true;
        }
        catch { return true; }
    }

    private static void Spi(uint action, bool on, bool asParam = false)
    {
        // a maioria dos SPI_SET* booleanos recebe o valor em pvParam; DragFullWindows/FontSmoothing em uiParam
        bool ok = asParam
            ? SystemParametersInfo(action, on ? 1u : 0u, IntPtr.Zero, SPIF)
            : SystemParametersInfo(action, 0, new IntPtr(on ? 1 : 0), SPIF);
        if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void SetAnimation(bool on)
    {
        var ai = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = on ? 1 : 0 };
        if (!SystemParametersInfo(SPI_SETANIMATION, ai.cbSize, ref ai, SPIF))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Reg(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", on ? "1" : "0");
    }

    private static void Reg(string sub, string name, object value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(sub);
        k?.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }
}
