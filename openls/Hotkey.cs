// Global hotkey via a hidden message-only window + RegisterHotKey.
// The window is created ON THE CORE THREAD (see CoreThread.cs) so WM_HOTKEY
// lands in the same message pump that owns the native core; the toggle then
// runs on the thread that called Init, which is what the original app does.
using System.Runtime.InteropServices;

namespace OpenLs;

public sealed class HotkeyWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
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
        public IntPtr lpszClassName; // explicit pointer; marshalled from below
        public IntPtr hIconSm;      // the 12th field: without it cbSize is 72, not the native 80,
                                    // and RegisterClassEx rejects a wrong cbSize with Win32 87
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwndclass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(IntPtr classNameOrAtom, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, IntPtr className, IntPtr windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    public const int WM_HOTKEY = 0x0312;

    // RegisterClassEx rejects a NULL lpfnWndProc with ERROR_INVALID_PARAMETER (87).
    // A raw user32 function pointer needs no GC lifetime management, unlike a
    // managed delegate, so DefWindowProcW is resolved directly.
    private static readonly IntPtr DefWindowProcPtr =
        GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");

    private readonly IntPtr _hwnd;
    private readonly IntPtr _instance;
    private readonly ushort _atom;
    private readonly uint _mods;
    private readonly uint _vk;
    private readonly int _id;
    private bool _disposed;

    public IntPtr Hwnd => _hwnd;
    public bool Registered { get; private set; }

    public HotkeyWindow(uint mods, uint vk, int id = 1)
    {
        _mods = mods;
        _vk = vk;
        _id = id;
        _instance = GetModuleHandleW(null);

        if (DefWindowProcPtr == IntPtr.Zero)
            throw new InvalidOperationException("could not resolve user32!DefWindowProcW");

        var classNamePtr = Marshal.StringToHGlobalUni("openlsHiddenWnd" + id);
        try
        {
            WNDCLASSEX wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = DefWindowProcPtr,
                hInstance = _instance,
                lpszClassName = classNamePtr,
            };
            _atom = RegisterClassExW(ref wc);
            if (_atom == 0)
                throw new InvalidOperationException("RegisterClassExW failed: Win32 " + Marshal.GetLastWin32Error());

            _hwnd = CreateWindowExW(0, new IntPtr(_atom), IntPtr.Zero, 0, 0, 0, 0, 0,
                new IntPtr(-3), // HWND_MESSAGE -> message-only window
                IntPtr.Zero, _instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                UnregisterClassW(new IntPtr(_atom), _instance);
                _atom = 0;
                throw new InvalidOperationException($"CreateWindowExW failed: Win32 {err}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    public void Register()
    {
        if (!RegisterHotKey(_hwnd, _id, _mods, _vk))
            throw new InvalidOperationException(
                $"RegisterHotKey failed (Win32 error {Marshal.GetLastWin32Error()}) - another app may already own this hotkey.");
        Registered = true;
    }

    /// <summary>True when this WM_HOTKEY message is ours.</summary>
    public bool IsOurHotkey(uint message, IntPtr wParam) =>
        message == WM_HOTKEY && wParam.ToInt64() == _id;

    /// <summary>Must run on the thread that created the window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Registered)
        {
            UnregisterHotKey(_hwnd, _id);
            Registered = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_atom != 0) UnregisterClassW(new IntPtr(_atom), _instance);
    }
}

public interface IHotkeyEngine : IDisposable
{
    void Start();
    string Name { get; }
}

// Engine wrapping HotkeyWindow. The window lives on the core thread and its
// WM_HOTKEY is handled by that thread's pump (CoreThread.RunPump), so there is
// no pump of its own here. The constructor registers the window class and the
// hotkey; when the environment refuses either, it throws and the caller falls
// back to polling.
public sealed class RegisterHotkeyEngine : IHotkeyEngine
{
    private readonly CoreThread _core;
    private readonly HotkeyWindow _hk;

    public string Name => "RegisterHotKey";

    public RegisterHotkeyEngine(CoreThread core, uint mods, uint vk, Action onHotkey, int id = 1)
    {
        _core = core;
        _hk = core.Invoke(() =>
        {
            var w = new HotkeyWindow(mods, vk, id);
            w.Register();
            return w;
        });
        core.SetHotkeyHandler(_hk, onHotkey);
    }

    public void Start()
    {
        // The core thread's pump is already running; nothing to start.
    }

    public void Dispose()
    {
        try
        {
            _core.SetHotkeyHandler(null, null);
            _core.Invoke(() => _hk.Dispose());
        }
        catch { }
    }
}

// Fallback hotkey detection without any class registration: polls async key state.
// Edge-triggered: fires once per full press of the combo. Runs on its own thread,
// so the callback must marshal itself onto the core thread.
public sealed class PollHotkeyEngine : IHotkeyEngine
{
    public string Name => "polling (GetAsyncKeyState)";

    private const ushort KEYISDOWN = 0x8000;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly uint _mods;
    private readonly uint _vk;
    private readonly Action _onHotkey;
    private readonly int _pollMs;
    private Thread? _thread;
    private volatile bool _stop;

    public PollHotkeyEngine(uint mods, uint vk, Action onHotkey, int pollMs = 20)
    {
        _mods = mods & 0xF;
        _vk = vk;
        _onHotkey = onHotkey;
        _pollMs = pollMs;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "hotkey-poll" };
        _thread.Start();
    }

    private void Loop()
    {
        bool wasDown = false;
        while (!_stop)
        {
            bool down = ComboDown();
            if (down && !wasDown)
            {
                try { _onHotkey(); } catch { }
            }
            wasDown = down;
            Thread.Sleep(_pollMs);
        }
    }

    private bool ComboDown()
    {
        if ((_mods & 0x2) != 0 && (GetAsyncKeyState(VK_CONTROL) & KEYISDOWN) == 0) return false;
        if ((_mods & 0x1) != 0 && (GetAsyncKeyState(VK_MENU) & KEYISDOWN) == 0) return false;
        if ((_mods & 0x4) != 0 && (GetAsyncKeyState(VK_SHIFT) & KEYISDOWN) == 0) return false;
        if ((_mods & 0x8) != 0 && ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & KEYISDOWN) == 0) return false;
        return (GetAsyncKeyState((int)_vk) & KEYISDOWN) != 0;
    }

    public void Dispose() => _stop = true;
}
