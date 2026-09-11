// Exact ABI of Lossless.dll as consumed by the original LosslessScaling WPF app
// (decompiled from LosslessScaling.dll -> UI/Core.cs). The native core exposes the
// same contract to any client that links it.
using System.Runtime.InteropServices;

namespace OpenLs;

public static class Native
{
    /// <summary>Native status callback.
    /// status: 0 = disabled, 1 = scaling active but temporarily paused (another window focused),
    /// 2 = scaling active. errorCode: 0 none, 1 RESIZE_FAILED, 2 LARGE_OFFSET, 3 CAPTURE_FAILED.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StatusListener(int status, IntPtr hwnd, int inputWidth, int inputHeight,
        int outputWidth, int outputHeight, [MarshalAs(UnmanagedType.I1)] bool resized,
        float scaleFactor, int errorCode);

    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint flags);

    /// <summary>Loads the core with its own directory on the dependency search path.
    /// Without LOAD_WITH_ALTERED_SEARCH_PATH the core's imports are resolved relative
    /// to openls.exe (e.g. frontend\publish\) rather than the install directory.</summary>
    public static IntPtr LoadCore(string path) =>
        LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>--minimized: shrink our own console to the taskbar (SW_MINIMIZE = 6).
    /// A no-op when there is no console, e.g. when launched from the Run key with no window.</summary>
    public static void MinimizeConsole()
    {
        IntPtr h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, 6);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Owning process id of a window, or 0. The original resolves the
    /// target the same way (decompiled MainWindow.cs:385).</summary>
    public static int WindowPid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public static string ProcessName(int pid)
    {
        if (pid == 0) return "?";
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return "pid " + pid; }
    }

    // ---- Lossless.dll exports (11) ----

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetForegroundWindowEx();

    /// <summary>Returns a BSTR: ';'-separated GPU adapter names.</summary>
    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetAdapterNames();

    /// <summary>Returns a BSTR: ';'-separated display names.</summary>
    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetDisplayNames();

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetDriverSettings();

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsWindowsBuildAtLeast(uint build);

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern float GetDwmRefreshRate();

    /// <summary>Sets the Windows graphics GPU preference (0..2). The original app restarts itself after changing it.</summary>
    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetWindowsSettings(int gpuPreference);

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool Init(StatusListener callback);

    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UnInit();

    /// <summary>Toggles scaling. Pass the target window to start on it, or 0 = current foreground window.</summary>
    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool Activate(IntPtr hwnd);

    /// <summary>Full profile. Argument order is part of the ABI — keep identical to the decompiled contract.</summary>
    [DllImport("Lossless.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ApplySettings(
        int scalingMode, int scalingFitMode, int scalingType, int scalingSubtype,
        float scaleFactor, [MarshalAs(UnmanagedType.I1)] bool resizeBeforeScale,
        int sharpness, [MarshalAs(UnmanagedType.I1)] bool vrs,
        int frameGenerationType, int frameGenerationSize, int frameGenerationMode,
        float frameGenerationMultiplier, float frameGenerationTarget, int frameGenerationFlowScale,
        [MarshalAs(UnmanagedType.I1)] bool clipCursor, [MarshalAs(UnmanagedType.I1)] bool cursorSensitivity,
        [MarshalAs(UnmanagedType.I1)] bool hideCursor, [MarshalAs(UnmanagedType.I1)] bool scaleCursor,
        int syncMode, int maxFrameLatency, [MarshalAs(UnmanagedType.I1)] bool gsyncSupport,
        [MarshalAs(UnmanagedType.I1)] bool hdrSupport, int captureApi, int queueTarget,
        [MarshalAs(UnmanagedType.I1)] bool drawFps, int gpuId, int displayId,
        int captureOffsetLeft, int captureOffsetTop, int captureOffsetRight, int captureOffsetBottom,
        [MarshalAs(UnmanagedType.I1)] bool multiDisplayMode, int setupPhase);

    public static string[] SplitBstr(IntPtr bstr, char sep)
    {
        if (bstr == IntPtr.Zero) return [];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; ; i += 2)
        {
            char c = (char)Marshal.ReadInt16(bstr, i);
            if (c == '\0') break;
            sb.Append(c);
        }
        return sb.ToString().Split(sep, System.StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Length > 0).ToArray();
    }

    public static string WinTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(1024);
        if (GetWindowTextW(hwnd, sb, sb.Capacity) > 0) return sb.ToString();
        return "<>";
    }

    public static string WinClass(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Presence of every export the frontend needs. Guards against a modified/partial build.</summary>
    public static string? VerifyExports(IntPtr module)
    {
        foreach (var name in AllExports)
        {
            if (GetProcAddress(module, name) == IntPtr.Zero)
                return name;
        }
        return null;
    }

    public static readonly string[] AllExports =
    [
        "Init", "UnInit", "Activate", "ApplySettings",
        "GetForegroundWindowEx", "GetAdapterNames", "GetDisplayNames",
        "SetDriverSettings", "IsWindowsBuildAtLeast", "GetDwmRefreshRate",
        "SetWindowsSettings",
    ];

    // --- Reading back what the core actually threw ---
    //
    // Lossless.dll imports RoOriginateLanguageException, so a winrt::hresult_error
    // escaping Init leaves a live IRestrictedErrorInfo on the thread carrying the
    // real HRESULT and message. .NET only reports "SEHException: External component
    // has thrown an exception", which says nothing; this recovers the cause.
    //
    // Raw vtable calls are used rather than [ComImport] interfaces so this keeps
    // working regardless of the built-in COM interop feature switch.

    [DllImport("combase.dll")]
    private static extern int GetRestrictedErrorInfo(out IntPtr ppRestrictedErrorInfo);

    [DllImport("oleaut32.dll")]
    private static extern int GetErrorInfo(uint dwReserved, out IntPtr ppIErrorInfo);

    private static unsafe IntPtr VtblSlot(IntPtr comObject, int slot)
    {
        IntPtr vtbl = *(IntPtr*)comObject;
        return ((IntPtr*)vtbl)[slot];
    }

    private static unsafe void ComRelease(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        var release = (delegate* unmanaged<IntPtr, uint>)VtblSlot(comObject, 2);
        release(comObject);
    }

    /// <summary>The HRESULT and description the last native call left behind,
    /// or null when nothing was recorded.</summary>
    public static unsafe string? LastNativeError()
    {
        // IRestrictedErrorInfo: IUnknown(0..2), GetErrorDetails(3), GetReference(4)
        if (GetRestrictedErrorInfo(out IntPtr rei) == 0 && rei != IntPtr.Zero)
        {
            try
            {
                var getDetails = (delegate* unmanaged<IntPtr, IntPtr*, int*, IntPtr*, IntPtr*, int>)VtblSlot(rei, 3);
                IntPtr desc = IntPtr.Zero, restricted = IntPtr.Zero, sid = IntPtr.Zero;
                int hr = 0;
                if (getDetails(rei, &desc, &hr, &restricted, &sid) == 0)
                {
                    string text = BstrToStringFree(restricted);
                    if (text.Length == 0) text = BstrToStringFree(desc);
                    else Marshal.FreeBSTR(desc);
                    Marshal.FreeBSTR(sid);
                    return Describe(hr, text);
                }
            }
            finally { ComRelease(rei); }
        }

        // IErrorInfo: IUnknown(0..2), GetGUID(3), GetSource(4), GetDescription(5)
        if (GetErrorInfo(0, out IntPtr ei) == 0 && ei != IntPtr.Zero)
        {
            try
            {
                var getDescription = (delegate* unmanaged<IntPtr, IntPtr*, int>)VtblSlot(ei, 5);
                IntPtr desc = IntPtr.Zero;
                if (getDescription(ei, &desc) == 0)
                {
                    string text = BstrToStringFree(desc);
                    if (text.Length > 0) return text;
                }
            }
            finally { ComRelease(ei); }
        }
        return null;
    }

    private static string BstrToStringFree(IntPtr bstr)
    {
        if (bstr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringBSTR(bstr) ?? ""; }
        finally { Marshal.FreeBSTR(bstr); }
    }

    private static string Describe(int hr, string text)
    {
        string code = "HRESULT 0x" + unchecked((uint)hr).ToString("X8");
        string known = unchecked((uint)hr) switch
        {
            0x80010106 => " (RPC_E_CHANGED_MODE - the thread's COM apartment does not match what the core needs)",
            0x80070005 => " (E_ACCESSDENIED)",
            0x8007000E => " (E_OUTOFMEMORY)",
            0x80004001 => " (E_NOTIMPL)",
            0x80004005 => " (E_FAIL)",
            0x887A0004 => " (DXGI_ERROR_UNSUPPORTED)",
            0x887A0005 => " (DXGI_ERROR_DEVICE_REMOVED)",
            0x887A0006 => " (DXGI_ERROR_DEVICE_HUNG)",
            0x887A002D => " (DXGI_ERROR_SDK_COMPONENT_MISSING)",
            _ => "",
        };
        return text.Length > 0 ? code + known + ": " + text.Trim() : code + known;
    }

    // --- SendInput: inject a synthetic hotkey press (used by --self-test) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public uint wVk;
        public uint wScanCode;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type; // 1 = INPUT_KEYBOARD
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT Key(uint vk, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };

    /// <summary>Synetically press &amp; release each VK in <paramref name="vks"/> (down in order, up in reverse).</summary>
    public static void SendHotkey(params uint[] vks)
    {
        var list = new List<INPUT>(vks.Length * 2);
        foreach (var v in vks) list.Add(Key(v, 0));
        for (int i = vks.Length - 1; i >= 0; i--) list.Add(Key(vks[i], 0x0002)); // KEYEVENTF_KEYUP
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }
}