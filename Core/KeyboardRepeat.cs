using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace AdamsToolkit.Core;

/// <summary>
/// "FilterKeys" — repetição do teclado em ms SÓ para W/A/S/D.
///
/// O FilterKeys do Windows é global (não dá para limitar a teclas), por isso isto é um
/// hook WH_KEYBOARD_LL próprio: o 1º KEYDOWN físico passa,
/// os auto-repeats do Windows dessa tecla são ENGOLIDOS e um timer nosso injeta KEYDOWNs
/// (SendInput, scancode, marker) ao atraso/intervalo escolhidos até ao KEYUP. Resto do
/// teclado intacto; nada é escrito no Windows — desligar = o PC fica exatamente como estava.
/// Só com o FiveM em foco (fora dele passthrough). Persiste em keyrepeat.json + RestoreOnBoot.
/// </summary>
public static class KeyboardRepeat
{
    public const int MinRepeatMs = 10, MaxRepeatMs = 500, MinDelayMs = 100, MaxDelayMs = 1000;

    public class State
    {
        public bool Enabled { get; set; }
        public int DelayMs { get; set; } = 500;  // padrão real do Windows
        public int RepeatMs { get; set; } = 33;
        /// <summary>v2.0.2: cada tecla com os seus valores (e ligada/desligada à parte).</summary>
        public bool PerKey { get; set; }
        public Dictionary<string, KeyCfg> Keys { get; set; } = new();
    }

    /// <summary>Valores de uma tecla no modo "por tecla". On=false → essa tecla fica com o comportamento normal do Windows.</summary>
    public class KeyCfg
    {
        public bool On { get; set; } = true;
        public int DelayMs { get; set; } = 500;
        public int RepeatMs { get; set; } = 33;
    }

    public static readonly string[] KeyNames = { "W", "A", "S", "D" };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "keyrepeat.json");

    public static State Cfg { get; private set; } = Load();
    public static bool IsActive => _hook != IntPtr.Zero;

    private static State Load()
    {
        try { if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(); }
        catch (Exception ex) { Log.Swallow("ler definições da repetição de teclado", ex); }
        return new();
    }
    private static void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!); File.WriteAllText(StateFile, JsonSerializer.Serialize(Cfg)); }
        catch (Exception ex) { Log.Error("gravar definições da repetição de teclado falhou", ex); }
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
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }

    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012;
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
    private static IntPtr _lastHwnd; private static volatile bool _lastIsFiveM; private static long _lastTick;

    /// <summary>Liga com os valores dados (na thread da UI). Guarda e fica ligado até Repor.</summary>
    public static string? Apply(int delayMs, int repeatMs)
    {
        Cfg = new State { Enabled = true, DelayMs = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs), RepeatMs = Math.Clamp(repeatMs, MinRepeatMs, MaxRepeatMs) };
        Save();
        ClearTimers();
        return Start();
    }

    // valores novos: repetições em curso (com os valores antigos) param já
    private static void ClearTimers()
    {
        lock (_lock) { foreach (var t in _timers.Values) t.Dispose(); _timers.Clear(); }
    }

    /// <summary>Desliga: nada foi mudado no Windows, o teclado volta a ser 100% o do PC.</summary>
    public static string? RestoreDefault()
    {
        Stop();
        Cfg = new State(); // Enabled=false + sliders voltam ao padrão real do Windows (500 / 33)
        Save();
        return null;
    }

    /// <summary>Interruptor Off: desliga mas guarda os valores para o próximo On.</summary>
    public static void TurnOff()
    {
        Stop();
        Cfg.Enabled = false;
        Save();
    }

    /// <summary>Liga no modo por tecla. Teclas em falta herdam os valores gerais.</summary>
    public static string? ApplyPerKey(IDictionary<string, KeyCfg> keys, int delayMs, int repeatMs)
    {
        var map = new Dictionary<string, KeyCfg>();
        foreach (var name in KeyNames)
        {
            var k = keys.TryGetValue(name, out var v) ? v : new KeyCfg { DelayMs = delayMs, RepeatMs = repeatMs };
            map[name] = new KeyCfg
            {
                On = k.On,
                DelayMs = Math.Clamp(k.DelayMs, MinDelayMs, MaxDelayMs),
                RepeatMs = Math.Clamp(k.RepeatMs, MinRepeatMs, MaxRepeatMs),
            };
        }
        Cfg = new State
        {
            Enabled = true, PerKey = true, Keys = map,
            DelayMs = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs),
            RepeatMs = Math.Clamp(repeatMs, MinRepeatMs, MaxRepeatMs),
        };
        Save();
        ClearTimers();
        return Start();
    }

    /// <summary>Valores que valem para esta tecla agora (modo geral ou por tecla).</summary>
    public static KeyCfg For(string name)
    {
        var c = Cfg;
        if (c.PerKey && c.Keys.TryGetValue(name, out var k)) return k;
        return new KeyCfg { On = true, DelayMs = c.DelayMs, RepeatMs = c.RepeatMs };
    }

    private static string NameOf(uint vk) => vk switch { 0x57 => "W", 0x41 => "A", 0x53 => "S", _ => "D" };

    public static (int delay, int repeat) Current() => (Cfg.DelayMs, Cfg.RepeatMs);
    public static string? RestoreOnBoot() => Cfg.Enabled ? Start() : null;

    /// <summary>
    /// O hook vive numa THREAD SÓ DELE, com bomba de mensagens própria.
    /// Antes era instalado na thread da UI: o Windows entrega os eventos de teclado à thread
    /// que instalou o hook, por isso qualquer trabalho da interface (desenhar, sensores) punha
    /// as teclas à espera e, passado o LowLevelHooksTimeout (300 ms por omissão), o Windows
    /// DESCARTA o evento — um KEYUP descartado = tecla presa no jogo. Numa thread dedicada e
    /// vazia isso não acontece.
    /// </summary>
    private static Thread? _hookThread;
    private static uint _hookThreadId;
    private static string? _startError;

    private static string? Start()
    {
        if (IsActive) return null;
        _startError = null;
        using var ready = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                _startError = "Não consegui instalar o hook do teclado (erro " + Marshal.GetLastWin32Error() + ").";
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            // bomba de mensagens: sem isto o Windows nunca chama o hook
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "AdamsToolkit.KeyRepeat", Priority = ThreadPriority.AboveNormal };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(3000);
        return _startError;
    }

    private static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_hookThreadId != 0) { PostThreadMessage(_hookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero); _hookThreadId = 0; }
        _hookThread = null;
        lock (_lock) { foreach (var t in _timers.Values) t.Dispose(); _timers.Clear(); }
    }

    /// <summary>
    /// Só lê um valor já calculado: saber o nome do processo da janela em primeiro plano
    /// (Process.GetProcessById) demora milissegundos e ESTAVA a ser feito dentro do hook, no
    /// caminho de cada tecla — era o engasgo de vez em quando. Agora a leitura é feita numa
    /// task à parte e o hook só consulta o resultado.
    /// </summary>
    private static bool FiveMForeground()
    {
        var now = Environment.TickCount64;
        var h = GetForegroundWindow();
        if (h != _lastHwnd || now - _lastTick > 2000)
        {
            _lastHwnd = h; _lastTick = now;
            if (!_resolving)
            {
                _resolving = true;
                Task.Run(() =>
                {
                    try
                    {
                        GetWindowThreadProcessId(h, out var pid);
                        var n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                        _lastIsFiveM = n.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase)
                                    || n.StartsWith("GTA5", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { _lastIsFiveM = false; }
                    finally { _resolving = false; }
                });
            }
        }
        return _lastIsFiveM;
    }

    private static volatile bool _resolving;

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

        // modo por tecla: tecla desligada → comportamento normal do Windows (nem engole nem injeta)
        var kc = For(NameOf(vk));
        if (!kc.On) return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            lock (_lock)
            {
                if (_timers.ContainsKey(vk)) return (IntPtr)1; // auto-repeat do Windows → engolir, o nosso timer trata

                // Como o Windows: só a ÚLTIMA tecla carregada repete. Antes, com W+A (ou A→D)
                // premidas, as duas repetiam ao mesmo tempo e o jogo recebia W,A,W,A… misturados —
                // com intervalos diferentes por tecla ficava irregular e o boneco "mudava de
                // direção" sozinho. As outras continuam premidas (o jogo sabe), só não repetem.
                foreach (var other in _timers.Values) other.Dispose();
                _timers.Clear();

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
                        // Alt-tab a meio de um W premido: o jogo deixa de estar à frente e o KEYUP
                        // vai para a outra janela, não para cá. Sem isto o timer continuava a injetar
                        // W no Discord/browser até se tocar outra vez na tecla.
                        if (!_lastIsFiveM) { _timers.Remove(key); cur.Dispose(); return; }
                        SendDown(key);
                    }
                }, null, kc.DelayMs, kc.RepeatMs);
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
