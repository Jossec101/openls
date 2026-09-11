// openls — CLI frontend for the Lossless Scaling native core (Lossless.dll).
// Detects a global hotkey, applies the default profile, and toggles scaling.
//
// Threading mirrors the original WPF app: every call into the core happens on
// one STA, message-pumped thread (CoreThread), the same one that called Init.
using System.Runtime.InteropServices;

namespace OpenLs;

public static class Program
{
    private static Native.StatusListener? _callback;
    private static GCHandle _callbackHandle;
    private static CoreThread? _core;
    private static int _lastStatus;                 // 0 off, 1 temp (focused away), 2 scaling
    private static IntPtr _targetHwnd;              // window the core is scaling
    private static readonly object _logLock = new();
    private static readonly ManualResetEventSlim _statusEvent = new(false);
    private static string _dllPath = "";
    private static string _hotkeySpec = "Ctrl+Alt+S";
    private static bool _exited;
    private static float _dwmRefresh;
    private static bool _coreReady;
    private static readonly Profile _profile = Profile.Default();
    private static EtwFps? _fps;
    private static AutoFrameGen? _autoFg;
    private static Timer? _fpsLineTimer;

    public static int Main(string[] args)
    {
        bool probe = false, toggleOnce = false, help = false, diagnose = false;
        bool installStartup = false, uninstallStartup = false, minimized = false;
        string? dllArg = null;
        int selfTestDelay = 0;
        int fpsLineMs = 0, fpsDumpSeconds = 0, fgDwell = 20;
        // auto-fg is on by default: below 60 fps the core runs LSFG3 fixed x2 (x3 at most,
        // when x3 still fits under the target); at or above it, adaptive. --no-auto-fg turns it off.
        bool autoFg = true;
        int? fgFixed = null;
        float fgTarget = 144f;
        double fgThreshold = 60, fgBand = 5, fpsWindow = 5;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help": help = true; break;
                case "--probe": probe = true; break;
                case "--toggle-once": toggleOnce = true; break;
                case "--diagnose": diagnose = true; break;
                case "--install-startup": installStartup = true; break;
                case "--uninstall-startup": uninstallStartup = true; break;
                case "--minimized": minimized = true; break;
                case "--capture":
                {
                    string? v = i + 1 < args.Length ? args[++i] : null;
                    int? api = Profile.ParseCaptureApi(v);
                    if (api == null) { Console.Error.WriteLine("--capture must be dxgi, wgc or gdi"); return 2; }
                    _profile.CaptureApi = api.Value;
                    break;
                }
                case "--self-test":
                    // optional value: delay ms before injecting the synthetic hotkey
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int ms) && ms >= 0) { i++; selfTestDelay = ms; }
                    else selfTestDelay = 2000;
                    break;
                case "--dll": dllArg = i + 1 < args.Length ? args[++i] : null; break;
                case "--fps-line": fpsLineMs = OptInt(args, ref i, 1000); break;
                case "--fps-dump": fpsDumpSeconds = OptInt(args, ref i, 10); break;
                case "--fps-window": fpsWindow = ReqDouble(args, ref i, "--fps-window"); break;
                case "--auto-fg": autoFg = true; break;
                case "--no-auto-fg": autoFg = false; break;
                case "--fg-target": fgTarget = (float)ReqDouble(args, ref i, "--fg-target"); break;
                case "--fg-threshold": fgThreshold = ReqDouble(args, ref i, "--fg-threshold"); break;
                case "--fg-band": fgBand = ReqDouble(args, ref i, "--fg-band"); break;
                case "--fg-dwell": fgDwell = (int)ReqDouble(args, ref i, "--fg-dwell"); break;
                case "--no-fg-auto-fixed": fgFixed = 2; break;
                case "--fg-fixed":
                {
                    int m = (int)ReqDouble(args, ref i, "--fg-fixed");
                    if (m is not (2 or 3)) { Console.Error.WriteLine("--fg-fixed must be 2 or 3"); return 2; }
                    fgFixed = m;
                    break;
                }
                case "--hotkey": _hotkeySpec = i + 1 < args.Length ? args[++i] : "Ctrl+Alt+S"; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }
        if (help)
        {
            PrintUsage();
            return 0;
        }

        if (uninstallStartup)
            return Startup.Uninstall(Log);
        if (installStartup)
            return Startup.Install(args, Log);

        if (minimized)
            Native.MinimizeConsole();

        if (fpsDumpSeconds > 0)
            return FpsDump(fpsDumpSeconds);

        if (autoFg && (double.IsNaN(fgThreshold) || fgThreshold <= 0))
        {
            Console.Error.WriteLine("--fg-threshold must be a positive source framerate (default 60).");
            return 2;
        }

        string? found = dllArg ?? FindDll();
        if (string.IsNullOrEmpty(found) || !File.Exists(found!))
        {
            Console.Error.WriteLine("Lossless.dll not found. Pass --dll <path> (the Lossless Scaling install directory contains it).");
            return 3;
        }
        _dllPath = found!;

        IntPtr h = Native.LoadCore(_dllPath);
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"LoadLibraryEx failed: Win32 error {Marshal.GetLastWin32Error()}");
            return 4;
        }
        string? missing = Native.VerifyExports(h);
        if (missing != null)
        {
            Console.Error.WriteLine($"ABI mismatch: export '{missing}' not present in {_dllPath}");
            return 5;
        }
        Log($"Loaded {_dllPath} — all {Native.AllExports.Length} exports verified.");

        // The core must be driven from one STA thread with a running message pump,
        // exactly as the original drives it from the WPF UI thread.
        _core = new CoreThread();
        _core.Start();

        if (diagnose)
        {
            Diag();
            _core.Dispose();
            return 0;
        }

        // Single-instance check: the original app owns the "LosslessScaling" mutex (decompiled App.cs).
        using (var mu = new Mutex(false, "LosslessScaling", out bool instanceFree))
        {
            if (!instanceFree)
                Log("NOTE: another LosslessScaling instance is already running (it holds the single-instance mutex 'LosslessScaling'). " +
                    "Close it before expecting the core to init here.");
        }

        _callback = new Native.StatusListener(OnStatus);
        _callbackHandle = GCHandle.Alloc(_callback);

        var init = InitCore();
        if (!init.Ok)
        {
            _coreReady = false;
            Log("CORE UNAVAILABLE: " + init.Describe());
        }
        else
        {
            _coreReady = true;
            Log("Core initialized (status listener live).");
            // These exports read state that Init populates, so they are only
            // meaningful afterwards — the original checks the Windows build in
            // its Loaded handler, well after Core.Init (MainWindow.cs:257,316).
            ReportCapabilities();
        }

        if (probe)
        {
            if (_coreReady) _core.Invoke(Native.UnInit);
            Log("probe complete.");
            _core.Dispose();
            return init.Ok ? 0 : 6;
        }

        if (_coreReady && (fpsLineMs > 0 || autoFg))
            StartFpsFeatures(fpsLineMs, autoFg, fpsWindow,
                new AutoFrameGen.Options(fgTarget, fgThreshold, fgBand, fgDwell, fgFixed));

        var (mods, vk) = ParseHotkey(_hotkeySpec);

        if (toggleOnce)
        {
            Toggle();
            if (!_statusEvent.Wait(TimeSpan.FromSeconds(6)))
                Log("no status event within 6s (core may be idle/failed — see messages above)");
            CleanExit(null, "toggle-once done");
            return 0;
        }

        IHotkeyEngine? engine;
        try
        {
            var r = new RegisterHotkeyEngine(_core, mods, vk, Toggle);
            r.Start();
            engine = r;
            Log("hotkey engine: " + engine.Name + " — active: " + _hotkeySpec);
        }
        catch (Exception ex)
        {
            var cause = Unwrap(ex);
            Log("hotkey engine: RegisterHotKey unavailable here (" + cause.GetType().Name + ": " +
                FirstLine(cause.Message) + ") — using fallback.");
            var p = new PollHotkeyEngine(mods, vk, () => _core!.Post(ToggleOnCoreThread));
            p.Start();
            engine = p;
            Log("hotkey engine: " + engine.Name + " — active: " + _hotkeySpec);
        }
        Log("press " + _hotkeySpec + " to toggle Lossless scaling on the focused window (default profile). Ctrl+C exits.");
        Log(DefaultProfileLine());
        if (_profile.CaptureApi == 2)
            Log("NOTE: capture GDI is in the core's enum but the original app never exposes it (its combo box lists DXGI and WGC only).");

        var gate = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = false;
            Log("Ctrl+C — stopping");
            gate.Set();
        };

        if (selfTestDelay > 0)
        {
            var delay = selfTestDelay;
            Task.Run(async () =>
            {
                await Task.Delay(delay);
                if (_exited) return;
                Log("--self-test: injecting synthetic " + _hotkeySpec + " (SendInput)");
                try
                {
                    Native.SendHotkey(BuildHotkeyVks(mods, vk));
                }
                catch (Exception ex2)
                {
                    Log("--self-test: SendInput failed: " + ex2.Message);
                }
                await Task.Delay(3000);
                Log("--self-test: done, exiting");
                gate.Set();
            });
        }

        gate.Wait();
        CleanExit(engine, "exit");
        return 0;
    }

    // ---------- core init ----------

    private sealed record InitResult(bool Ok, string? ExceptionName, string? NativeError, uint SehCode)
    {
        public string Describe()
        {
            if (ExceptionName == null && NativeError == null)
                return "Init returned false without throwing — the core refused to start but reported no error.";
            var parts = new List<string>();
            if (NativeError != null) parts.Add(NativeError);
            if (SehCode != 0) parts.Add("SEH code 0x" + SehCode.ToString("X8") + ExplainCode(SehCode));
            if (parts.Count == 0 && ExceptionName != null) parts.Add(ExceptionName + " with no recoverable error detail");
            return "Init aborted — " + string.Join(" | ", parts);
        }
    }

    /// <summary>Calls Init on the core thread and captures the real cause there,
    /// because WinRT/COM error info is thread-local.</summary>
    private static InitResult InitCore() => _core!.Invoke(() =>
    {
        try
        {
            bool ok = Native.Init(_callback!);
            return new InitResult(ok, null, ok ? null : Native.LastNativeError(), 0);
        }
        catch (Exception ex)
        {
            return new InitResult(false, ex.GetType().Name, Native.LastNativeError(),
                ex is SEHException ? 0xE06D7363u : 0u);
        }
    });

    private static void ReportCapabilities()
    {
        var caps = _core!.Invoke(() => (
            refresh: Native.GetDwmRefreshRate(),
            buildOk: Native.IsWindowsBuildAtLeast(19041),
            adapters: Native.SplitBstr(Native.GetAdapterNames(), ';'),
            displays: Native.SplitBstr(Native.GetDisplayNames(), ';')));

        _dwmRefresh = caps.refresh;
        Log($"Windows build >=19041: {caps.buildOk} | DWM refresh ~{_dwmRefresh:0.#} Hz");
        Log("GPU adapters (gpuId):");
        foreach (var (name, i) in caps.adapters.Select((n, i) => (n, i)))
            Log($"  [{i}] {name}");
        Log("Displays (displayId):");
        foreach (var (name, i) in caps.displays.Select((n, i) => (n, i)))
            Log($"  [{i}] {name}");
    }

    // ---------- FPS measurement and automatic frame generation ----------

    private static void StartFpsFeatures(int fpsLineMs, bool autoFg, double window, AutoFrameGen.Options opt)
    {
        _fps = new EtwFps(window);
        if (!_fps.Start())
        {
            Log("FPS: " + (_fps.StartError ?? "could not start the ETW session"));
            _fps.Dispose();
            _fps = null;
            if (autoFg)
                Log("auto-fg: disabled — it needs the source framerate, which is what the ETW session measures.");
            return;
        }
        Log($"FPS: ETW session live (window {window:0.#}s). real = the captured game's process, generated = this process.");

        if (fpsLineMs > 0)
            _fpsLineTimer = new Timer(_ => FpsLine(), null, fpsLineMs, fpsLineMs);

        if (autoFg)
        {
            _autoFg = new AutoFrameGen(_core!, _fps, opt, _profile, () => _lastStatus, () => _targetHwnd, Log);
            _autoFg.Start();
            Log(_autoFg.Describe());
        }
    }

    private static void FpsLine()
    {
        if (_exited || _fps == null || _lastStatus == 0) return;
        try
        {
            int pid = Native.WindowPid(_targetHwnd);
            double? real = pid != 0 ? _fps.Rate(pid) : null;
            double? gen = _fps.Rate(Environment.ProcessId);
            string mode = _autoFg?.CurrentMode() ?? (_profile.Lsfg3Mode == 1
                ? $"LSFG3 adaptive->{_profile.Lsfg3Target:0.#}"
                : $"LSFG3 fixed x{_profile.Lsfg3Multiplier:0.#}");
            string eff = real is > 0 && gen != null ? $"x{gen.Value / real.Value:0.00}" : "n/a";
            Log($"real {Fmt(real)} fps ({Native.ProcessName(pid)} {pid}) | generated {Fmt(gen)} fps | {mode} | eff {eff}");
        }
        catch { }
    }

    private static string Fmt(double? v) => v?.ToString("0.0") ?? "n/a";

    /// <summary>--fps-dump: report which provider/event ids actually arrive, so the
    /// Present filter in EtwFps is derived from this machine rather than guessed.</summary>
    private static int FpsDump(int seconds)
    {
        using var fps = new EtwFps(5, dump: true);
        if (!fps.Start())
        {
            Console.Error.WriteLine("fps-dump: " + (fps.StartError ?? "could not start the ETW session"));
            return 7;
        }
        Log($"--fps-dump: collecting for {seconds}s — put something on screen that is presenting frames.");
        Thread.Sleep(seconds * 1000);
        Log($"--fps-dump: traceHandle=0x{fps.TraceHandle:X} rawEvents={fps.RawEvents} processTraceRc={fps.ProcessTraceRc} err={fps.StartError ?? "none"}");
        Log("--fps-dump: provider / event id / count");
        foreach (var (key, count) in fps.DumpCounts().Take(40))
            Log($"   {key.Provider} id={key.EventId,-6} {count}");
        Log("--fps-dump: presents per process (this is what 'real'/'generated' read)");
        foreach (var (pid, rate, presents) in fps.PerProcess().Take(20))
            Log($"   {Native.ProcessName(pid),-24} pid {pid,-8} {Fmt(rate),6} fps  ({presents} presents)");
        return 0;
    }

    private static int OptInt(string[] args, ref int i, int fallback)
    {
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int v) && v >= 0) { i++; return v; }
        return fallback;
    }

    private static double ReqDouble(string[] args, ref int i, string flag)
    {
        if (i + 1 < args.Length && double.TryParse(args[i + 1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            i++;
            return v;
        }
        throw new ArgumentException(flag + " needs a number");
    }

    private static Exception Unwrap(Exception ex) =>
        ex is CoreInvokeException && ex.InnerException != null ? ex.InnerException : ex;

    private static string FirstLine(string? s) =>
        (s ?? "?").Split('\n')[0].TrimEnd();

    private static uint[] BuildHotkeyVks(uint mods, uint vk)
    {
        var list = new List<uint>(4);
        if ((mods & 0x2) != 0) list.Add(0x11); // CTRL
        if ((mods & 0x1) != 0) list.Add(0x12); // ALT
        if ((mods & 0x4) != 0) list.Add(0x10); // SHIFT
        if ((mods & 0x8) != 0) list.Add(0x5B); // LWIN
        list.Add(vk);
        return list.ToArray();
    }

    // ---------- --diagnose ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DIAGWNDCLASS
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;      // the 12th field: without it cbSize is 72, not the native 80,
                                    // and RegisterClassEx rejects a wrong cbSize with Win32 87
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref DIAGWNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(IntPtr classNameOrAtom, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index); // SM_CMONITORS = 8

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? name, uint num, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>Run every check, then touch Init last so all findings survive a core abort.</summary>
    private static void Diag()
    {
        Log("=== openls --diagnose (checks first, Init probe last) ===");

        // 1. original app instance?
        var procs = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.Contains("Lossless")).ToList();
        if (procs.Count == 0) Log("1) no LosslessScaling process running.");
        foreach (var p in procs)
        {
            string path;
            try { path = p.MainModule?.FileName ?? "?"; } catch { path = "(no access)"; }
            Log($"1) PROCESS RUNNING: {p.ProcessName} pid={p.Id} path={path}");
        }

        // 2. single-instance mutex
        bool mutexHeld = false;
        try
        {
            mutexHeld = Mutex.TryOpenExisting("LosslessScaling", out var m);
            m?.Dispose();
        }
        catch { }
        Log($"2) single-instance mutex 'LosslessScaling' held: {mutexHeld}");

        // 3. identity / elevation / session
        var ident = System.Security.Principal.WindowsIdentity.GetCurrent();
        bool elevated = new System.Security.Principal.WindowsPrincipal(ident)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Log($"3) user={ident.Name} elevated={elevated} desktop session={System.Diagnostics.Process.GetCurrentProcess().SessionId}");

        // 4. OS build (modern .NET queries RtlGetVersion, so .Build is the real number)
        var osv = Environment.OSVersion;
        Log($"4) OS={osv.Version} (build>=19041: {osv.Version.Build >= 19041})");

        // 5. displays / GPU adapters as Windows sees them (the core's own view needs Init first)
        int monitors = GetSystemMetrics(8);
        var adapters = new List<string>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if ((dd.StateFlags & 0x20) == 0) adapters.Add($"{dd.DeviceName} -> {dd.DeviceString}");
        }
        Log($"5) monitors={monitors} | display adapters={(adapters.Count == 0 ? "NONE" : adapters.Count.ToString())}");
        foreach (var a in adapters) Log($"   {a}");
        try { Log($"   DWM refresh per core: {_core!.Invoke(Native.GetDwmRefreshRate):0.#} Hz"); } catch { }

        // 6. class registration (the check openls itself used to get wrong)
        ushort atom = 0;
        int clsErr = 0;
        var defWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
        try
        {
            var namePtr = Marshal.StringToHGlobalUni("LsDiagTmpClass");
            var inst = GetModuleHandleW(null);
            var wc = new DIAGWNDCLASS
            {
                cbSize = (uint)Marshal.SizeOf<DIAGWNDCLASS>(),
                lpfnWndProc = defWndProc,   // a NULL wndproc is exactly what Win32 87 means here
                hInstance = inst,
                lpszClassName = namePtr,
            };
            atom = RegisterClassExW(ref wc);
            clsErr = Marshal.GetLastWin32Error();
            if (atom != 0) UnregisterClassW(new IntPtr(atom), inst);
            Marshal.FreeHGlobal(namePtr);
        }
        catch (Exception ex3) { clsErr = -1; Log("6) class probe threw: " + ex3.Message); }
        Log($"6) test RegisterClassExW: {(atom != 0 ? "OK (atom 0x" + atom.ToString("X") + ")" : "FAILED Win32 " + clsErr)}");

        // 7. core Init probe (last — if this aborts the process, everything above is already printed)
        Log("7) attempting core Init on the STA core thread (the answer here arrives last on purpose):");
        var cb = new Native.StatusListener((_, _, _, _, _, _, _, _, _) => { });
        var hdl = GCHandle.Alloc(cb);
        bool initOk = false;
        try
        {
            var res = _core!.Invoke(() =>
            {
                try
                {
                    bool ok = Native.Init(cb);
                    return (ok: ok, err: ok ? null : Native.LastNativeError(), ex: (string?)null);
                }
                catch (Exception ex)
                {
                    return (ok: false, err: Native.LastNativeError(), ex: ex.GetType().FullName);
                }
            });
            initOk = res.ok;
            if (res.ok)
            {
                Log("   Init OK — core is HEALTHY.");
                Log($"   core build>=19041: {_core.Invoke(() => Native.IsWindowsBuildAtLeast(19041))}");
                foreach (var a in _core.Invoke(() => Native.SplitBstr(Native.GetAdapterNames(), ';')))
                    Log($"   adapter: {a}");
                foreach (var d in _core.Invoke(() => Native.SplitBstr(Native.GetDisplayNames(), ';')))
                    Log($"   display: {d}");
                _core.Invoke(Native.UnInit);
                Log("   UnInit done.");
            }
            else
            {
                if (res.ex != null) Log("   Init aborted: " + res.ex);
                else Log("   Init returned false (cleanly, no exception).");
                if (res.err != null) Log("   native error: " + res.err);
                else Log("   the core left no IRestrictedErrorInfo/IErrorInfo behind.");
            }
        }
        catch (Exception ex)
        {
            var c = Unwrap(ex);
            Log("   Init probe failed: " + c.GetType().FullName + ": " + FirstLine(c.Message));
        }
        finally { hdl.Free(); }

        // 8. verdict
        Log("--- verdict / fixes ---");
        if (procs.Count > 0 || mutexHeld)
            Log("   * a LosslessScaling instance owns the single-instance core: close it and re-run.");
        if (monitors == 0 || adapters.Count == 0)
            Log("   * Windows reports no display adapters in this session; the core has nothing to capture or present to.");
        if (atom == 0)
            Log("   * RegisterClassExW failed for some reason other than a NULL wndproc — the Win32 code above is the citation.");
        if (!initOk)
            Log("   * Init did not succeed. The 'native error' line above is the core's own HRESULT; treat that as the cause rather than guessing at the environment.");
        else
            Log("   * core is healthy: run without --diagnose and press the hotkey.");
    }

    private static string ExplainCode(uint code) => code switch
    {
        0xE06D7363 => " (a native C++ exception — winrt::hresult_error; the HRESULT above is the real cause)",
        0xC0000005 => " (ACCESS_VIOLATION)",
        0xC0000409 => " (FAST_FAIL — an in-core assert aborted)",
        0xC0000096 => " (PRIVILEGED_INSTRUCTION)",
        0xE0434352 => " (a CLR exception crossed the FFI boundary)",
        _ => "",
    };

    // Mirrors MainWindow.ActivateCore from the decompiled app:
    //   off  -> apply profile, then Activate (core toggles on for the focused window)
    //   on   -> Activate again (core toggles off)
    private static void Toggle() => _core!.Invoke(ToggleOnCoreThread);

    private static void ToggleOnCoreThread()
    {
        if (_exited) return;
        if (!_coreReady)
        {
            Log("toggle requested, but core unavailable (init failed at startup) — nothing to toggle.");
            return;
        }
        try
        {
            if (_lastStatus == 0)
            {
                _profile.Apply();
                _targetHwnd = Native.GetForegroundWindowEx();
                Log($"applying DEFAULT profile, activating on focused window ({Native.WinTitle(_targetHwnd)})");
                bool ok = Native.Activate(IntPtr.Zero);
                _lastStatus = 2; // optimistic; core events remain authoritative
                Log(ok ? "Activate() ok — scaling should be engaging now" : "Activate() returned false");
            }
            else
            {
                Log("deactivating");
                Native.Activate(IntPtr.Zero);
                _lastStatus = 0;
                _targetHwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _coreReady = false;
            _lastStatus = 0;
            string? err = Native.LastNativeError();
            Log("CORE CRASHED during toggle (" + ex.GetType().Name + (err != null ? " — " + err : "") +
                "). Core is now offline; hotkey stays registered but inactive.");
        }
    }

    private static void OnStatus(int status, IntPtr hwnd, int iw, int ih, int ow, int oh, bool resized, float scale, int err)
    {
        try
        {
            Log($"[cb] status={status} hwnd={hwnd:X} {iw}x{ih}->{ow}x{oh} resized={resized} scale={scale:0.000} err={err}");
        }
        catch { }
        if (_exited) return;
        try
        {
            if (err != 0)
            {
                _lastStatus = 0;
                switch (err)
                {
                    case 1: Log($"CORE ERROR RESIZE_FAILED: input {iw}x{ih}, output {ow}x{oh}"); break;
                    case 2: Log($"CORE ERROR LARGE_OFFSET: input {iw}x{ih}"); break;
                    case 3: Log($"CORE ERROR CAPTURE_FAILED: input {iw}x{ih}"); break;
                    default: Log($"CORE ERROR {err}"); break;
                }
                Log("core fell back to off — press the hotkey again to re-engage");
            }
            else
            {
                _lastStatus = status;
                if (status != 0) _targetHwnd = hwnd;
                string win = Native.WinTitle(hwnd);
                switch (status)
                {
                    case 1:
                    {
                        IntPtr fgw = Native.GetForegroundWindow();
                        Log($"SCALING (paused/focused {Native.WinTitle(fgw)} [{Native.WinClass(fgw)}]): target \"{win}\" {iw}x{ih} -> {ow}x{oh} x{scale:0.00}{(resized ? " (resized)" : "")}");
                        break;
                    }
                    case 2:
                        Log($"SCALING \"{win}\": {iw}x{ih} -> {ow}x{oh} x{scale:0.00}{(resized ? " (resized)" : "")}");
                        break;
                    case 0:
                        Log("off");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _coreReady = false;
            Log("status callback errored (" + ex.GetType().Name + ") — core treated as offline");
        }
        _statusEvent.Set();
    }

    private static void CleanExit(IDisposable? hk, string why)
    {
        if (_exited) return;
        _exited = true;
        Log($"shutting down ({why})");
        try { _autoFg?.Dispose(); } catch { }
        try { _fpsLineTimer?.Dispose(); } catch { }
        try { _fps?.Dispose(); } catch { }
        try { hk?.Dispose(); } catch { }
        try
        {
            if (_coreReady) _core!.Invoke(Native.UnInit);
        }
        catch { }
        try { _core?.Dispose(); } catch { }
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
        _callback = null;
    }

    private static void Log(string msg)
    {
        lock (_logLock)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
    }

    private static string DefaultProfileLine()
    {
        var p = _profile;
        string type = p.ScalingType switch
        {
            0 => "off (raw scale)", 1 => "LS1", 2 => "FSR", 3 => "NIS", 4 => "SGSR",
            5 => "BicubicCAS", 6 => "Anime4K", 7 => "XBR", 8 => "SharpBilinear",
            9 => "integer", 10 => "nearest", _ => "?"
        };
        string fg = p.FrameGeneration switch
        {
            0 => "off",
            1 => "LSFG3 " + (p.Lsfg3Mode == 1 ? "adaptive" : "fixed") + " (target " + p.Lsfg3Target.ToString("0.#") + ", multiplier x" + p.Lsfg3Multiplier.ToString("0.#") + ")",
            2 => "LSFG2 x" + (p.Lsfg2Mode + 2),
            3 => "LSFG1", 4 => "LSFI", 5 => "LSFI1", _ => "?"
        };
        string captureName = Profile.CaptureApiName(p.CaptureApi);
        string syncName = p.SyncMode switch { 0 => "off", 1 => "default", 2 => "vsync", 3 => "vsync 1/2", 4 => "vsync 1/3", _ => "vsync 1/4" };
        return "default profile: scale x" + p.ScaleFactor.ToString("0.##") + " | type " + type + " | " + fg
            + " | sync " + syncName
            + " | gsync " + p.Gsync.ToString().ToLowerInvariant()
            + " | capture " + captureName
            + " | fps counter " + p.DrawFps.ToString().ToLowerInvariant()
            + " | gpu " + p.GpuId + " display " + p.DisplayId;
    }

    private static string? FindDll()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "Lossless.dll") };
        if (Environment.GetEnvironmentVariable("LS_INSTALL") is string ls && ls.Length > 0)
            candidates.Add(Path.Combine(ls, "Lossless.dll"));
        foreach (string? start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                candidates.Add(Path.Combine(dir.FullName, "Lossless.dll"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static (uint mods, uint vk) ParseHotkey(string spec)
    {
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        uint mods = MOD_NOREPEAT;
        uint vk = 0;
        foreach (var raw in spec.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": case "windows": case "super": mods |= MOD_WIN; continue;
            }
            if (part.Length == 1)
            {
                char c = char.ToUpperInvariant(part[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) vk = c;
                else if (c == ' ') vk = 0x20;
                else throw new ArgumentException("unknown key '" + part + "'");
                continue;
            }
            if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part.AsSpan(1), out int f) && f >= 1 && f <= 24)
            {
                vk = (uint)(0x70 + f - 1);
                continue;
            }
            throw new ArgumentException($"unknown key '{part}'");
        }
        if (vk == 0) throw new ArgumentException($"hotkey '{spec}' has no key");
        return (mods, vk);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "openls — CLI frontend for the Lossless Scaling native core (Lossless.dll).\n" +
            "\n" +
            "Usage: openls.exe [options]\n" +
            "  (no args)          run: register the global hotkey, toggle scaling on hotkey\n" +
            "  --dll <path>       Lossless.dll to load (default: auto-discovered next to the exe / via %LS_INSTALL%)\n" +
            "  --hotkey <combo>   global hotkey, e.g. \"Ctrl+Alt+S\" (default), \"F8\", \"Alt+P\"...\n" +
            "  --probe            init the core, print adapters/displays/refresh, uninit and exit\n" +
            "  --toggle-once      init core, fire one toggle (activation test), wait for a status\n" +
            "                     event, then exit\n" +
            "  --self-test [ms]   run normally, then after [ms] (default 2000) inject the hotkey\n" +
            "                     synthetically (SendInput) and exit after it fires — end-to-end\n" +
            "                     test that needs no physical keyboard\n" +
            "  --diagnose         prints who owns the core (processes/mutex), elevation, OS build,\n" +
            "                     monitor+adapter enumeration, class registration, then probes Init\n" +
            "                     and reports the core's own HRESULT — the triage path when\n" +
            "                     'CORE UNAVAILABLE' shows up\n" +
            "\n" +
            "Profile:\n" +
            "  --capture <api>    capture API: wgc (default), dxgi, or gdi. WGC output can be recorded\n" +
            "                     by any software and is colour-managed by Windows 11's auto colour\n" +
            "                     management; DXGI needs OBS game capture and the HDR option instead\n" +
            "\n" +
            "Run at sign-in (HKCU\\...\\CurrentVersion\\Run, no elevation needed):\n" +
            "  --install-startup  register this exe with every OTHER flag on this command line, plus\n" +
            "                     --minimized, to run when you sign in; then exit\n" +
            "  --uninstall-startup  remove that registration and exit\n" +
            "  --minimized        start with the console window minimised\n" +
            "\n" +
            "FPS readout (real = the captured game's process, generated = this process).\n" +
            "The core exposes no framerate, so these come from a real-time ETW session:\n" +
            "  --fps-line [ms]    log real/generated FPS every ms (default 1000) while scaling\n" +
            "  --fps-dump [s]     collect present events for s seconds (default 10), print the\n" +
            "                     providers/event ids seen and the per-process present rates\n" +
            "  --fps-window <s>   averaging window for the rates (default 5)\n" +
            "\n" +
            "Automatic LSFG3 adaptive/fixed selection, driven by the source framerate (ON by default):\n" +
            "  --auto-fg              enable it (the default)\n" +
            "  --no-auto-fg           disable it: the profile's LSFG3 adaptive mode is used as-is\n" +
            "  --fg-target <hz>       adaptive target (default 144)\n" +
            "  --fg-threshold <fps>   source FPS at or above which adaptive is used (default 60);\n" +
            "                         below it LSFG3 runs fixed x2, or x3 when x3 fits under the target\n" +
            "  --fg-fixed <2|3>       pin the fixed fallback (default x2, or x3 when x3 fits)\n" +
            "  --no-fg-auto-fixed     same as --fg-fixed 2\n" +
            "  --fg-band <fps>        hysteresis band (default 5)\n" +
            "  --fg-dwell <s>         minimum seconds between switches (default 20)\n" +
            "\n" +
            "  -h | --help        this text\n" +
            "\n" +
            "Semantics mirror the original LosslessScaling WPF app's hotkey path:\n" +
            "off  -> ApplySettings(default profile) + Activate()   (core engages on the focused window)\n" +
            "on   -> Activate() again                               (core toggles off)");
    }
}
