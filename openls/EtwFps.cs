// Per-process present rate via a real-time ETW session (the PresentMon approach).
//
// Lossless.dll exports exactly 11 functions and none of them report a framerate;
// the 9-argument status callback carries resolutions only, and the profile's
// DrawFps flag just makes the core draw its own overlay onto the scaled output.
// So "real" and "generated" FPS have to be measured from outside the core:
//
//   real      = present rate of the captured game's process
//   generated = present rate of THIS process, because the core presents the
//               scaled/interpolated frames from inside openls
//
// Raw P/Invoke rather than the TraceEvent package, to keep the offline
// zero-package build. Requires an elevated run: a real-time ETW session needs
// admin or membership in Performance Log Users.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OpenLs;

public sealed class EtwFps : IDisposable
{
    // --- providers -------------------------------------------------------

    public static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    public static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl

    /// <summary>DXGI Present events. Confirmed on this machine with --fps-dump.</summary>
    private static readonly (Guid Provider, ulong Keyword, byte Level)[] Providers =
    [
        (DxgiProvider, 0x2UL, 5),   // keyword 0x2 = "Events" (0x1 is "Objects" and carries no Present)
    ];

    // Present start/stop for IDXGISwapChain::Present. Only the start is counted,
    // so a frame is counted once. Populated from the --fps-dump findings.
    private static readonly HashSet<int> PresentEventIds = [42];

    // --- ETW interop -----------------------------------------------------

    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
    private static readonly ulong InvalidHandle = 0xFFFFFFFFFFFFFFFFUL;

    // EVENT_TRACE_PROPERTIES: WNODE_HEADER (48 bytes) followed by the session fields.
    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public uint WnodeBufferSize;
        public uint WnodeProviderId;
        public ulong WnodeHistoricalContext;
        public long WnodeTimeStamp;
        public Guid WnodeGuid;
        public uint WnodeClientContext;
        public uint WnodeFlags;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    // Only the four fields we set are named; the rest is opaque padding.
    // Offsets are the x64 layout of EVENT_TRACE_LOGFILEW:
    //   CurrentEvent (EVENT_TRACE, 88 bytes) at 32, LogfileHeader (280) at 120,
    //   BufferCallback at 400, then the callback union at 424 and Context at 440.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
        [FieldOffset(440)] public IntPtr Context;
    }

    // Offsets inside EVENT_RECORD -> EVENT_HEADER (which is 80 bytes):
    private const int OffsetProcessId = 12;   // EVENT_HEADER.ProcessId
    private const int OffsetProviderId = 24;  // EVENT_HEADER.ProviderId (GUID)
    private const int OffsetEventId = 40;     // EVENT_HEADER.EventDescriptor.Id (USHORT)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong sessionHandle, in Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong traceHandle);

    // --- state -----------------------------------------------------------

    private const string SessionName = "openlsFps";
    private const int SampleMs = 250;

    private readonly bool _dump;
    private readonly int _windowSamples;
    private readonly ConcurrentDictionary<int, long> _counts = new();
    private readonly ConcurrentDictionary<int, Ring> _rings = new();
    private readonly ConcurrentDictionary<(Guid, int), long> _dumpCounts = new();
    private EventRecordCallback? _callback;   // held so the GC cannot collect it
    private ulong _session;
    private ulong _trace = InvalidHandle;
    private Thread? _processThread;
    private Timer? _sampler;
    private bool _stopped;

    public bool Running { get; private set; }
    public string? StartError { get; private set; }
    public long RawEvents;
    public int ProcessTraceRc = -1;
    public ulong TraceHandle;

    /// <param name="windowSeconds">averaging window for Rate()</param>
    /// <param name="dump">tally provider/event ids instead of filtering to Present</param>
    public EtwFps(double windowSeconds = 5.0, bool dump = false)
    {
        _dump = dump;
        _windowSamples = Math.Max(2, (int)Math.Round(windowSeconds * 1000 / SampleMs));
    }

    private sealed class Ring
    {
        private readonly (DateTime T, long C)[] _buf;
        private int _n;
        public Ring(int size) => _buf = new (DateTime, long)[size];

        public void Add(DateTime t, long c)
        {
            lock (_buf)
            {
                _buf[_n % _buf.Length] = (t, c);
                _n++;
            }
        }

        /// <summary>Events per second across the samples held, or null until there are two.</summary>
        public double? Rate()
        {
            lock (_buf)
            {
                int have = Math.Min(_n, _buf.Length);
                if (have < 2) return null;
                int start = _n >= _buf.Length ? _n % _buf.Length : 0;
                var oldest = _buf[start];
                var newest = _buf[(_n - 1) % _buf.Length];
                double secs = (newest.T - oldest.T).TotalSeconds;
                if (secs <= 0) return null;
                return (newest.C - oldest.C) / secs;
            }
        }
    }

    /// <summary>Starts the session. Returns false and sets StartError when it cannot
    /// (most often: not elevated). Scaling and hotkeys are unaffected either way.</summary>
    public bool Start()
    {
        // A session outlives the process that made it, so clear a leaked one first.
        StopByName();

        int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int nameBytes = (SessionName.Length + 1) * 2;
        int total = propsSize + nameBytes + 2;
        IntPtr props = Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);
            var p = new EVENT_TRACE_PROPERTIES
            {
                WnodeBufferSize = (uint)total,
                WnodeFlags = WNODE_FLAG_TRACED_GUID,
                WnodeClientContext = 1,             // QPC timestamps
                BufferSize = 64,                    // KB
                MinimumBuffers = 8,
                MaximumBuffers = 64,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,
                LoggerNameOffset = (uint)propsSize,
            };
            Marshal.StructureToPtr(p, props, false);
            // StartTrace reads the session name from LoggerNameOffset.
            var nameBytesArr = System.Text.Encoding.Unicode.GetBytes(SessionName + "\0");
            Marshal.Copy(nameBytesArr, 0, props + propsSize, nameBytesArr.Length);

            int rc = StartTraceW(out _session, SessionName, props);
            if (rc != ERROR_SUCCESS)
            {
                StartError = rc switch
                {
                    ERROR_ACCESS_DENIED =>
                        "FPS measurement needs an elevated run (a real-time ETW session requires admin or Performance Log Users). " +
                        "Scaling and hotkeys work normally without it.",
                    ERROR_ALREADY_EXISTS =>
                        $"an ETW session named '{SessionName}' already exists and could not be reclaimed",
                    _ => $"StartTrace failed: Win32 {rc}",
                };
                return false;
            }

            foreach (var (provider, keyword, level) in Providers)
            {
                int erc = EnableTraceEx2(_session, provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                    level, keyword, 0, 0, IntPtr.Zero);
                if (erc != ERROR_SUCCESS)
                {
                    StartError = $"EnableTraceEx2({provider}) failed: Win32 {erc}";
                    StopByName();
                    return false;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        _callback = OnEvent;
        var logfile = new EVENT_TRACE_LOGFILEW
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        _trace = OpenTraceW(ref logfile);
        TraceHandle = _trace;
        if (_trace == InvalidHandle)
        {
            StartError = $"OpenTrace failed: Win32 {Marshal.GetLastWin32Error()}";
            StopByName();
            return false;
        }

        _processThread = new Thread(() =>
        {
            // ProcessTrace blocks until the session is stopped.
            try { ProcessTraceRc = ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero); } catch (Exception ex) { StartError = "ProcessTrace threw: " + ex.Message; }
        })
        { IsBackground = true, Name = "etw-fps" };
        _processThread.Start();

        _sampler = new Timer(_ => Sample(), null, SampleMs, SampleMs);
        Running = true;
        return true;
    }

    private unsafe void OnEvent(IntPtr record)
    {
        System.Threading.Interlocked.Increment(ref RawEvents);
        if (record == IntPtr.Zero) return;
        byte* p = (byte*)record;
        int pid = *(int*)(p + OffsetProcessId);
        int id = *(ushort*)(p + OffsetEventId);

        if (_dump)
        {
            var provider = *(Guid*)(p + OffsetProviderId);
            _dumpCounts.AddOrUpdate((provider, id), 1, (_, v) => v + 1);
        }

        if (!PresentEventIds.Contains(id)) return;
        _counts.AddOrUpdate(pid, 1, (_, v) => v + 1);
    }

    private void Sample()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _counts)
            _rings.GetOrAdd(kv.Key, _ => new Ring(_windowSamples)).Add(now, kv.Value);
    }

    /// <summary>Presents per second for a process, or null while the window is still filling.</summary>
    public double? Rate(int pid) =>
        _rings.TryGetValue(pid, out var ring) ? ring.Rate() : null;

    /// <summary>Every process seen presenting, with its current rate.</summary>
    public IEnumerable<(int Pid, double? Rate, long Presents)> PerProcess() =>
        _counts.Select(kv => (kv.Key, Rate(kv.Key), kv.Value)).OrderByDescending(x => x.Item3);

    /// <summary>--fps-dump: what actually arrived, most frequent first.</summary>
    public IEnumerable<((Guid Provider, int EventId) Key, long Count)> DumpCounts() =>
        _dumpCounts.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value);

    private void StopByName()
    {
        int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int total = propsSize + (SessionName.Length + 1) * 2 + 2;
        IntPtr props = Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);
            var p = new EVENT_TRACE_PROPERTIES
            {
                WnodeBufferSize = (uint)total,
                WnodeFlags = WNODE_FLAG_TRACED_GUID,
                LoggerNameOffset = (uint)propsSize,
            };
            Marshal.StructureToPtr(p, props, false);
            int rc = ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
            // "not found" is the normal case; anything else is worth knowing about.
            if (rc != ERROR_SUCCESS && rc != ERROR_WMI_INSTANCE_NOT_FOUND && rc != ERROR_ACCESS_DENIED)
                StartError ??= $"ControlTrace(STOP) returned Win32 {rc}";
        }
        finally { Marshal.FreeHGlobal(props); }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        Running = false;
        try { _sampler?.Dispose(); } catch { }
        // Stopping the session makes ProcessTrace return so the thread can exit.
        try { StopByName(); } catch { }
        try { if (_trace != InvalidHandle) CloseTrace(_trace); } catch { }
        try { _processThread?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _callback = null;
    }
}
