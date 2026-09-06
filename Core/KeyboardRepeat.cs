using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AdamsToolkit.Core;

/// <summary>
/// "FilterKeys" — repetição do teclado em ms SÓ para W/A/S/D.
///
/// O FilterKeys do Windows é global (não dá para limitar a teclas), por isso isto é um
/// hook WH_KEYBOARD_LL próprio, igual ao <see cref="SnapTap"/>: o 1º KEYDOWN físico passa,
/// os auto-repeats do Windows dessa tecla são ENGOLIDOS e um timer nosso injeta KEYDOWNs
/// (SendInput, scancode, marker) ao atraso/intervalo escolhidos até ao KEYUP. Resto do
/// teclado intacto; nada é escrito no Windows — desligar = o PC fica exatamente como estava.
/// Só com o FiveM em foco (fora dele passthrough). Persiste em keyrepeat.json + RestoreOnBoot.
/// </summary>
public static class KeyboardRepeat
{
    public const int MinRepeatMs = 10, MaxRepeatMs = 500, MinDelayMs = 100, MaxDelayMs = 1000;

    public class State { public bool Enabled { get; set; } public int DelayMs { get; set; } = 500; public int RepeatMs { get; set; } = 33; } // padrão real do Windows

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "keyrepeat.json");

    public static State Cfg { get; private set; } = Load();
    public static bool IsActive => _hook != IntPtr.Zero;

    private static State Load()
    {
        try { if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(); } catch { }
        return new();
    }
    private static void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!); File.WriteAllText(StateFile, JsonSerializer.Serialize(Cfg)); } catch { }
    }

    // ---------------- Win32 ----------------
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10, KEYEVENTF_SCANCODE = 0x0008, INPUT_KEYBOARD = 1;
    private static readonly UIntPtr Marker = (UIntPtr)0x5A17B0;

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public long pad1; [FieldOffset(8)] public long pad2; [FieldOffset(16)] public long pad3; [FieldOffset(24)] public long pad4; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }

    // ---------------- estado ----------------
    private static readonly uint[] Keys = { 0x57, 0x41, 0x53, 0x44 }; // W A S D
    private static readonly Dictionary<uint, Timer> _timers = new();
    private static readonly object _lock = new();
    private static readonly LowLevelKeyboardProc _proc = Callback;
    private static IntPtr _hook = IntPtr.Zero;
    private static IntPtr _lastHwnd; private static bool _lastIsFiveM; private static long _lastTick;

    /// <summary>Liga com os valores dados (na thread da UI). Guarda e fica ligado até Repor.</summary>
    public static string? Apply(int delayMs, int repeatMs)
    {
        Cfg = new State { Enabled = true, DelayMs = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs), RepeatMs = Math.Clamp(repeatMs, MinRepeatMs, MaxRepeatMs) };
        Save();
        return Start();
    }

    /// <summary>Desliga: nada foi mudado no Windows, o teclado volta a ser 100% o do PC.</summary>
    public static string? RestoreDefault()
    {
        Stop();
        Cfg = new State(); // Enabled=false + sliders voltam ao padrão real do Windows (500 / 33)
        Save();
        return null;
    }

    public static (int delay, int repeat) Current() => (Cfg.DelayMs, Cfg.RepeatMs);
    public static string? RestoreOnBoot() => Cfg.Enabled ? Start() : null;

    private static string? Start()
    {
        if (IsActive) return null;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook == IntPtr.Zero ? "Não consegui instalar o hook do teclado (erro " + Marshal.GetLastWin32Error() + ")." : null;
    }

    private static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        lock (_lock) { foreach (var t in _timers.Values) t.Dispose(); _timers.Clear(); }
    }

    private static bool FiveMForeground()
    {
        var now = Environment.TickCount64;
        var h = GetForegroundWindow();
        if (h == _lastHwnd && now - _lastTick < 2000) return _lastIsFiveM;
        _lastHwnd = h; _lastTick = now;
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            var n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            _lastIsFiveM = n.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase) || n.StartsWith("GTA5", StringComparison.OrdinalIgnoreCase);
        }
        catch { _lastIsFiveM = false; }
        return _lastIsFiveM;
    }

    private static void SendDown(uint vk)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = KEYEVENTF_SCANCODE, dwExtraInfo = Marker } },
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;
        var vk = k.vkCode;
        if (Array.IndexOf(Keys, vk) < 0 || (k.flags & LLKHF_INJECTED) != 0 || k.dwExtraInfo == Marker)
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (!FiveMForeground())
        {
            lock (_lock) { if (_timers.Remove(vk, out var t0)) t0.Dispose(); }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            lock (_lock)
            {
                if (_timers.ContainsKey(vk)) return (IntPtr)1; // auto-repeat do Windows → engolir, o nosso timer trata
                var key = vk;
                Timer? self = null;
                self = new Timer(_ =>
                {
                    // BUG (v1.66-1.67): Timer.Dispose() no KEYUP não espera por um callback já a
                    // correr → um KEYDOWN injetado chegava DEPOIS do KEYUP físico e o jogo ficava
                    // com a tecla presa (boneco a andar sozinho até ao próximo toque). Agora o
                    // envio é feito sob o lock e só se ESTE timer ainda for o da tecla — o KEYUP
                    // remove-o sob o mesmo lock, por isso nunca há down depois do up.
                    lock (_lock)
                    {
                        if (!_timers.TryGetValue(key, out var cur) || !ReferenceEquals(cur, self)) return;
                        // não re-premir uma tecla que o Snap Tap tem virtualmente solta (A/D, W/S)
                        if (SnapTap.IsActive && SnapTap.IsVirtuallyUp(key)) return;
                        SendDown(key);
                    }
                }, null, Cfg.DelayMs, Cfg.RepeatMs);
                _timers[vk] = self;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam); // 1º down físico passa
        }
        if (msg is WM_KEYUP or WM_SYSKEYUP)
        {
            lock (_lock) { if (_timers.Remove(vk, out var t)) t.Dispose(); }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
