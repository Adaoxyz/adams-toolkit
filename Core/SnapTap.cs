using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AdamsToolkit.Core;

/// <summary>
/// "Snap Tap" / SOCD last-input-wins para o strafe no FiveM (A/D e, opcional, W/S).
///
/// Problema: ao trocar A→D há sempre uns milissegundos com as DUAS teclas premidas
/// e o jogo anula o movimento (o boneco trava). Aqui, quando carregas D com A ainda
/// premida, o A é largado no mesmo instante; quando largas D e o A continua
/// fisicamente premido, o A volta a entrar. Mudança de direção instantânea.
/// É o mesmo que o Razer Snap Tap / Wooting Rappy Snappy fazem em hardware.
///
/// Como: hook de teclado low-level (WH_KEYBOARD_LL). Só mexe nas 4 letras e, por
/// defeito, só com o FiveM em primeiro plano — o resto passa intocado. Os eventos
/// físicos dessas teclas são engolidos e re-injetados por SendInput (com scancode,
/// que é o que o jogo lê via Raw Input) numa ÚNICA chamada, para a ordem
/// "solta a outra → carrega esta" ser determinística. Latência acrescentada: <1 ms.
/// Não toca em ficheiros do jogo nem injeta nada no processo do FiveM.
///
/// Limite conhecido (UIPI): se o FiveM correr como administrador e a app não, o
/// hook não vê o teclado do jogo → simplesmente não faz nada (nunca estraga input).
///
/// Nota de regras: isto é tecnicamente um macro de input. É feature oficial para o
/// servidor do dono; noutros servidores/jogos pode ser proibido — a UI avisa e a
/// ativação é sempre manual (persiste a escolha, não se liga sozinha da 1ª vez).
/// </summary>
public static class SnapTap
{
    public class Settings
    {
        public bool Enabled { get; set; }
        public bool AD { get; set; } = true;
        public bool WS { get; set; }
        public bool OnlyFiveM { get; set; } = true;
    }

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "snaptap.json");

    public static Settings Cfg { get; private set; } = Load();

    /// <summary>Hook instalado (a funcionar).</summary>
    public static bool IsActive => _hook != IntPtr.Zero;

    private static Settings Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(StateFile)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(Cfg));
        }
        catch { }
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MAPVK_VK_TO_VSC = 0;
    private static readonly UIntPtr Marker = (UIntPtr)0x5A17A9; // assinatura dos nossos eventos injetados

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    // a union tem o tamanho do MOUSEINPUT (32 bytes) — INPUT tem de dar 40 bytes em x64, senão SendInput rejeita (cbSize)
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public long pad1; [FieldOffset(8)] public long pad2; [FieldOffset(16)] public long pad3; [FieldOffset(24)] public long pad4; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    // ---------------- estado ----------------

    private sealed class Pair
    {
        public Pair(uint a, uint b) { A = a; B = b; }
        public readonly uint A, B;
        public bool PhysA, PhysB, VirtA, VirtB;
        public void Reset() => PhysA = PhysB = VirtA = VirtB = false;
        public bool Has(uint vk) => vk == A || vk == B;
    }

    private static readonly Pair PairAD = new(0x41, 0x44); // A / D
    private static readonly Pair PairWS = new(0x57, 0x53); // W / S

    /// <summary>True se a tecla está fisicamente premida mas o Snap Tap a tem "solta" para o jogo.</summary>
    public static bool IsVirtuallyUp(uint vk)
    {
        foreach (var p in new[] { PairAD, PairWS })
        {
            if (vk == p.A) return p.PhysA && !p.VirtA;
            if (vk == p.B) return p.PhysB && !p.VirtB;
        }
        return false;
    }

    private static readonly LowLevelKeyboardProc _proc = Callback; // referência viva: o GC não pode apanhar o delegate
    private static IntPtr _hook = IntPtr.Zero;

    private static IntPtr _lastHwnd = IntPtr.Zero;
    private static bool _lastIsFiveM;
    private static long _lastCheckTick;

    /// <summary>Instala o hook. TEM de ser chamado na thread da UI (precisa de message loop).</summary>
    public static string? Start()
    {
        if (IsActive) return null;
        PairAD.Reset(); PairWS.Reset();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            return $"não consegui instalar o hook de teclado (erro {Marshal.GetLastWin32Error()})";
        return null;
    }

    public static void Stop()
    {
        if (!IsActive) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        // ao desligar, qualquer tecla que o jogo ainda tenha como premida por nossa causa
        // (re-injetada) leva um UP explícito — UP a mais é inofensivo, DOWN preso não.
        foreach (var p in new[] { PairAD, PairWS })
        {
            if (p.VirtA) Send(Key(p.A, false));
            if (p.VirtB) Send(Key(p.B, false));
        }
        PairAD.Reset(); PairWS.Reset();
    }

    /// <summary>Arranque da app: repõe o estado que o jogador deixou (liga só se estava ligado).</summary>
    public static string? RestoreOnBoot() => Cfg.Enabled ? Start() : null;

    // ---------------- lógica ----------------

    private static bool FiveMForeground()
    {
        var h = GetForegroundWindow();
        var now = Environment.TickCount64;
        if (h == _lastHwnd && now - _lastCheckTick < 2000) return _lastIsFiveM;
        _lastHwnd = h; _lastCheckTick = now;
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) { _lastIsFiveM = false; return false; }
            using var p = Process.GetProcessById((int)pid);
            var n = p.ProcessName;
            _lastIsFiveM = n.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("GTA5", StringComparison.OrdinalIgnoreCase);
        }
        catch { _lastIsFiveM = false; }
        return _lastIsFiveM;
    }

    private static INPUT Key(uint vk, bool down)
    {
        var sc = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk, wScan = sc,
                    dwFlags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP),
                    dwExtraInfo = Marker,
                }
            }
        };
    }

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // eventos injetados (nossos ou de outro software) passam sempre — evita loops
            if ((k.flags & LLKHF_INJECTED) != 0 || k.dwExtraInfo == Marker)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            var vk = k.vkCode;
            Pair? pair = Cfg.AD && PairAD.Has(vk) ? PairAD : Cfg.WS && PairWS.Has(vk) ? PairWS : null;
            if (pair == null) return CallNextHookEx(_hook, nCode, wParam, lParam);

            if (Cfg.OnlyFiveM && !FiveMForeground())
            {
                // fora do jogo não mexemos; limpar estado para não "lembrar" teclas de antes do alt-tab
                pair.Reset();
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            var msg = (int)wParam;
            var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            var up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);

            var isA = vk == pair.A;
            ref var physThis = ref (isA ? ref pair.PhysA : ref pair.PhysB);
            ref var virtThis = ref (isA ? ref pair.VirtA : ref pair.VirtB);
            ref var physOther = ref (isA ? ref pair.PhysB : ref pair.PhysA);
            ref var virtOther = ref (isA ? ref pair.VirtB : ref pair.VirtA);
            var other = isA ? pair.B : pair.A;

            if (down)
            {
                if (physThis)
                {
                    // auto-repeat: passa se a tecla está "virtualmente" premida (chat/escrita normal);
                    // se foi suprimida pela outra do par, ENGOLE — senão o repeat voltava a marcar
                    // a tecla como premida no jogo e as duas ficavam ativas outra vez.
                    return virtThis ? CallNextHookEx(_hook, nCode, wParam, lParam) : (IntPtr)1;
                }
                physThis = true; virtThis = true;
                if (physOther && virtOther)
                {
                    virtOther = false;
                    Send(Key(other, false), Key(vk, true)); // solta a outra E carrega esta, nesta ordem, atómico
                    return (IntPtr)1;                        // engole o evento físico (já o re-injetámos)
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            else
            {
                physThis = false; virtThis = false;
                if (physOther && !virtOther)
                {
                    virtOther = true;
                    Send(Key(vk, false), Key(other, true));  // solta esta E volta a carregar a outra (ainda premida)
                    return (IntPtr)1;
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }
        }
        catch
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam); // nunca deixar o teclado preso por causa de um bug nosso
        }
    }
}
