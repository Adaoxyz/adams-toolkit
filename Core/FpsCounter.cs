using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdamsToolkit.Core;

/// <summary>
/// FPS do jogo em primeiro plano, lido do ETW do próprio Windows — o mesmo método do
/// PresentMon (Intel), FrameView (NVIDIA) e da Xbox Game Bar.
///
/// Porque é seguro para anti-cheat e PC check: NÃO injeta nada no jogo, não instala
/// driver e não apaga nada. Só escuta o evento "Present" do DXGI (cada frame que um
/// programa entrega ao ecrã) e conta quantos por segundo vêm do processo da janela da
/// frente. A sessão ETW ("Adams Toolkit FPS") existe só enquanto a métrica FPS do
/// overlay está ligada.
///
/// Peso: 1 thread parada à espera de eventos + 1 incremento por frame. Sem dependências.
/// Precisa de admin (o Windows só deixa admins abrir sessões ETW).
/// </summary>
public static class FpsCounter
{
    private const string SessionName = "Adams Toolkit FPS";
    // Microsoft-Windows-DXGI · evento 42 = Present_Start
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const ushort PresentStart = 42;

    private static readonly ConcurrentDictionary<int, long> Presents = new();
    private static ulong _session, _trace;
    private static Thread? _thread;
    private static EventRecordCallback? _callback; // referência viva: o Windows chama-a a cada evento
    private static IntPtr _propsBuf, _loggerName;

    /// <summary>null = a correr; senão o motivo (sem admin, Windows antigo…).</summary>
    public static string? Error { get; private set; }
    public static bool Running => _thread != null;

    // ---------- leitura ----------

    private static int _lastPid;
    private static long _lastCount;
    private static long _lastTicks;
    private static double _fps = -1;

    /// <summary>FPS do processo da janela da frente desde a última chamada (-1 = sem dados).</summary>
    public static double Sample()
    {
        if (!Running) return -1;
        var pid = ForegroundPid();
        var now = Stopwatch.GetTimestamp();
        var count = pid > 0 && Presents.TryGetValue(pid, out var c) ? c : 0;
        if (pid != _lastPid || pid <= 0 || pid == Environment.ProcessId) _fps = -1;
        else
        {
            var secs = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (secs > 0.2) _fps = (count - _lastCount) / secs;
        }
        _lastPid = pid; _lastCount = count; _lastTicks = now;
        return _fps;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    private static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return pid;
    }

    // ---------- arranque / paragem ----------

    public static void Start()
    {
        if (Running) return;
        Error = null;
        try
        {
            if (!UninstallerEngine.IsAdmin()) { Error = "admin"; return; }

            // EVENT_TRACE_PROPERTIES (x64, 120 bytes) + nome da sessão logo a seguir
            const int nameOffset = 120, total = 120 + 256 * 2;
            _propsBuf = Marshal.AllocHGlobal(total);
            void InitProps()
            {
                for (var i = 0; i < total; i++) Marshal.WriteByte(_propsBuf, i, 0);
                Marshal.WriteInt32(_propsBuf, 0, total);        // Wnode.BufferSize
                Marshal.WriteInt32(_propsBuf, 40, 1);           // Wnode.ClientContext = QPC
                Marshal.WriteInt32(_propsBuf, 44, 0x00020000);  // Wnode.Flags = WNODE_FLAG_TRACED_GUID
                Marshal.WriteInt32(_propsBuf, 48, 64);          // BufferSize (KB)
                Marshal.WriteInt32(_propsBuf, 52, 4);           // MinimumBuffers
                Marshal.WriteInt32(_propsBuf, 56, 8);           // MaximumBuffers
                Marshal.WriteInt32(_propsBuf, 64, 0x100);       // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
                Marshal.WriteInt32(_propsBuf, 68, 1);           // FlushTimer = 1 s
                Marshal.WriteInt32(_propsBuf, 116, nameOffset); // LoggerNameOffset
            }

            InitProps();
            var err = StartTraceW(out _session, SessionName, _propsBuf);
            if (err == 183) // ERROR_ALREADY_EXISTS: sobrou de uma app que rebentou — fecha e recria
            {
                InitProps();
                ControlTraceW(0, SessionName, _propsBuf, 1 /* STOP */);
                InitProps();
                err = StartTraceW(out _session, SessionName, _propsBuf);
            }
            if (err != 0) { Error = err == 5 ? "admin" : $"ETW {err}"; Cleanup(); return; }

            // Só o evento 42 sai do DXGI (filtro no próprio Windows, Win8.1+); sem filtro
            // continua a funcionar, só chegam mais eventos que ignoramos.
            var guid = DxgiProvider;
            err = EnableWithEventFilter(ref guid);
            if (err != 0)
                err = EnableTraceEx2(_session, ref guid, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (err != 0) { Error = $"DXGI {err}"; Stop(); return; }

            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            _callback = OnEvent;
            var log = new EventTraceLogfile
            {
                LoggerName = _loggerName,
                ProcessTraceMode = 0x100 | 0x10000000, // REAL_TIME | EVENT_RECORD
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            };
            _trace = OpenTraceW(ref log);
            if (_trace == ulong.MaxValue) { Error = $"OpenTrace {Marshal.GetLastWin32Error()}"; Stop(); return; }

            _thread = new Thread(() =>
            {
                try { ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception ex) { Log.Swallow("FPS: ProcessTrace", ex); }
            })
            { IsBackground = true, Name = "AdamsToolkit.Fps", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Log.Info("sessão ETW de FPS ligada", "overlay");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("FPS: arrancar ETW falhou", ex);
            Stop();
        }
    }

    public static void Stop()
    {
        try
        {
            if (_trace != 0 && _trace != ulong.MaxValue) CloseTrace(_trace);
            if (_session != 0 && _propsBuf != IntPtr.Zero) ControlTraceW(_session, null, _propsBuf, 1 /* STOP */);
        }
        catch (Exception ex) { Log.Swallow("FPS: parar ETW", ex); }
        _thread?.Join(2000);
        _thread = null;
        _trace = _session = 0;
        Presents.Clear();
        _fps = -1;
        Cleanup();
    }

    private static void Cleanup()
    {
        if (_propsBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_propsBuf); _propsBuf = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
    }

    // EVENT_RECORD: EventHeader.ProcessId @12, ProviderId @24, EventDescriptor.Id @40
    private static void OnEvent(IntPtr record)
    {
        if ((ushort)Marshal.ReadInt16(record, 40) != PresentStart) return;
        var pid = Marshal.ReadInt32(record, 12);
        Presents.AddOrUpdate(pid, 1, (_, v) => v + 1);
    }

    private static uint EnableWithEventFilter(ref Guid guid)
    {
        // EVENT_FILTER_EVENT_ID { BOOLEAN FilterIn; UCHAR Reserved; USHORT Count; USHORT Events[1]; }
        var ids = Marshal.AllocHGlobal(8);
        // EVENT_FILTER_DESCRIPTOR { ULONGLONG Ptr; ULONG Size; ULONG Type; } — 16 bytes
        var desc = Marshal.AllocHGlobal(16);
        // ENABLE_TRACE_PARAMETERS v2 (x64): Version@0 EnableProperty@4 ControlFlags@8 SourceId@12
        // EnableFilterDesc@32 FilterDescCount@40 — 48 bytes
        var par = Marshal.AllocHGlobal(48);
        try
        {
            for (var i = 0; i < 48; i++) Marshal.WriteByte(par, i, 0);
            Marshal.WriteByte(ids, 0, 1);
            Marshal.WriteByte(ids, 1, 0);
            Marshal.WriteInt16(ids, 2, 1);
            Marshal.WriteInt16(ids, 4, (short)PresentStart);
            Marshal.WriteInt64(desc, 0, ids.ToInt64());
            Marshal.WriteInt32(desc, 8, 6);
            Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200)); // EVENT_FILTER_TYPE_EVENT_ID
            Marshal.WriteInt32(par, 0, 2);
            Marshal.WriteInt64(par, 32, desc.ToInt64());
            Marshal.WriteInt32(par, 40, 1);
            return EnableTraceEx2(_session, ref guid, 1, 4, ulong.MaxValue, 0, 0, par);
        }
        finally
        {
            Marshal.FreeHGlobal(par);
            Marshal.FreeHGlobal(desc);
            Marshal.FreeHGlobal(ids);
        }
    }

    // ---------- interop (advapi32) ----------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    // EVENT_TRACE_LOGFILEW em x64 = 448 bytes. Só preenchemos o que interessa.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EventTraceLogfile
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
}
