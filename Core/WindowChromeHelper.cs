using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AdamsToolkit.Core;

/// <summary>
/// Janelas OPACAS com aspeto moderno. AllowsTransparency=True (janela "layered") obriga o
/// WPF a copiar a janela inteira da GPU para a CPU a cada frame que mexe — com bolinhas,
/// gráficos e transições isso era CPU gasto o tempo todo com a app à frente (e é pior
/// em PCs fracos). Opaca: cantos redondos pedidos ao DWM (Win11) e maximizar sem sair
/// do monitor. Usado pelo Toolkit e pelo Editor de Clips.
/// </summary>
public static class WindowChromeHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    /// <summary>Chamar no SourceInitialized.</summary>
    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        try
        {
            int pref = 2; // DWMWCP_ROUND (atributo 33, Win11 22000+); noutros Windows dá erro e ignora-se
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { }

        // WindowStyle=None + maximizar: sem isto a janela "sai" 6-8 px para fora do monitor.
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
            try
            {
                var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (mon == IntPtr.Zero) return IntPtr.Zero;
                var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var r = mi.Monitor;
                mmi.MaxPosition = new POINT { X = 0, Y = 0 };
                mmi.MaxSize = new POINT { X = r.Right - r.Left, Y = r.Bottom - r.Top };
                mmi.MaxTrackSize = mmi.MaxSize;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            catch { }
            return IntPtr.Zero;
        });
    }
}
