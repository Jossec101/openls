// A single STA, message-pumped thread that owns the native core.
//
// The original app calls Core.Init from the WPF UI thread (MainWindow.cs:257):
// STA, OLE-initialized, with a running message pump, and every later
// ApplySettings/Activate goes through Dispatcher.Invoke back onto that same
// thread. openls used to call Init from a plain console Main (MTA, no pump)
// and then Activate from the hotkey thread. C++/WinRT inside Lossless.dll
// throws on the apartment mismatch, which surfaced in .NET as
// "SEHException: External component has thrown an exception".
//
// CoreThread reproduces the original's threading exactly.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OpenLs;

public sealed class CoreThread : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_INVOKE = 0x8000 + 1; // WM_APP + 1
    private const uint PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    private sealed class WorkItem
    {
        public Func<object?> Work = null!;
        public ManualResetEventSlim? Done;
        public object? Result;
        public Exception? Error;
    }

    private readonly Thread _thread;
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private HotkeyWindow? _hotkeyWindow;
    private Action? _onHotkey;
    private volatile bool _stopped;

    public CoreThread()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ls-core" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public bool IsCoreThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Starts the thread and returns once its pump is running.</summary>
    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("core thread did not start within 10s");
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        // Force the message queue into existence before anyone can PostThreadMessage to it.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        // WPF's UI thread is OLE-initialized; some WinRT paths inside the core need it.
        // S_FALSE (already initialized) is fine, so the result is deliberately ignored.
        OleInitialize(IntPtr.Zero);
        _ready.Set();
        try
        {
            RunPump();
        }
        finally
        {
            _stopped = true;
            DrainQueue();      // release anyone blocked in Invoke
            OleUninitialize();
        }
    }

    private void RunPump()
    {
        while (true)
        {
            int r = GetMessageW(out MSG m, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) return;   // WM_QUIT or error

            if (m.message == WM_APP_INVOKE)
            {
                DrainQueue();
                continue;
            }

            var hk = _hotkeyWindow;
            if (hk != null && hk.IsOurHotkey(m.message, m.wParam))
            {
                try { _onHotkey?.Invoke(); } catch { }
                continue;
            }

            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            if (_stopped && item.Done != null)
            {
                item.Error = new InvalidOperationException("core thread has stopped");
                item.Done.Set();
                continue;
            }
            try { item.Result = item.Work(); }
            catch (Exception ex) { item.Error = ex; }
            item.Done?.Set();
        }
    }

    /// <summary>Hooks WM_HOTKEY for the given window into the pump. Pass nulls to unhook.</summary>
    public void SetHotkeyHandler(HotkeyWindow? window, Action? onHotkey)
    {
        _hotkeyWindow = window;
        _onHotkey = onHotkey;
    }

    /// <summary>Queue work without waiting for it.</summary>
    public void Post(Action work)
    {
        if (_stopped) return;
        _queue.Enqueue(new WorkItem { Work = () => { work(); return null; } });
        PostThreadMessageW(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Run work on the core thread and wait for it, rethrowing whatever it threw.</summary>
    public void Invoke(Action work) => Invoke<object?>(() => { work(); return null; });

    /// <summary>Run work on the core thread and return its result.</summary>
    public T Invoke<T>(Func<T> work)
    {
        if (IsCoreThread) return work();   // re-entrant call: run inline, never deadlock
        if (_stopped) throw new InvalidOperationException("core thread has stopped");

        using var done = new ManualResetEventSlim(false);
        var item = new WorkItem { Work = () => work(), Done = done };
        _queue.Enqueue(item);
        if (!PostThreadMessageW(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("PostThreadMessage to core thread failed: Win32 " + Marshal.GetLastWin32Error());

        done.Wait();
        if (item.Error != null)
            throw new CoreInvokeException(item.Error);
        return (T)item.Result!;
    }

    public void Dispose()
    {
        if (_stopped) return;
        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
        _stopped = true;
    }
}

/// <summary>Wraps an exception thrown on the core thread, preserving its stack.</summary>
public sealed class CoreInvokeException : Exception
{
    public CoreInvokeException(Exception inner)
        : base(inner.Message, inner) { }
}
