// Isolates RegisterClassExW / CreateWindowExW behaviour, which openls once
// failed with Win32 87 (ERROR_INVALID_PARAMETER) on every attempt.
//
// The answer, confirmed by variants A-D below: the WNDCLASSEX declaration was
// missing the struct's 12th field, HICON hIconSm. Without it Marshal.SizeOf
// reports 72 where the native x64 WNDCLASSEXW is 80 bytes, and RegisterClassEx
// rejects a wrong cbSize with ERROR_INVALID_PARAMETER. That, and only that,
// produced the 87.
//
// The NULL lpfnWndProc the old code also had is a separate latent bug, not a
// cause of 87: variant B shows the class registers fine, and the failure moves
// to CreateWindowEx, which dispatches WM_NCCREATE through the null pointer and
// takes the process down. Neither problem was an environment restriction.
using System.Runtime.InteropServices;

// Correct: all twelve fields.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEX
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
    public IntPtr hIconSm;
}

class P
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UnregisterClassW(IntPtr classNameOrAtom, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(int ex, IntPtr cls, IntPtr name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? m);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    static void Try(string label, string className, uint cbSize, IntPtr wndProc, IntPtr inst)
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(className);
        var wc = new WNDCLASSEX
        {
            cbSize = cbSize,
            lpfnWndProc = wndProc,
            hInstance = inst,
            lpszClassName = namePtr,
        };
        ushort atom = RegisterClassExW(ref wc);
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"{label}: atom=0x{atom:X4} err={err}{(err == 87 ? " (ERROR_INVALID_PARAMETER)" : "")}");
        if (atom != 0 && wndProc == IntPtr.Zero)
        {
            Console.WriteLine("  create skipped: with a NULL wndproc the class registers, but CreateWindowEx");
            Console.WriteLine("  dispatches WM_NCCREATE through a null pointer and kills the process.");
            UnregisterClassW(new IntPtr(atom), inst);
        }
        else if (atom != 0)
        {
            IntPtr h = CreateWindowExW(0, new IntPtr(atom), IntPtr.Zero, 0, 0, 0, 0, 0,
                new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, inst, IntPtr.Zero);
            Console.WriteLine($"  create(HWND_MESSAGE): hwnd=0x{h:X}{(h == IntPtr.Zero ? " err=" + Marshal.GetLastWin32Error() : "")}");
            if (h != IntPtr.Zero) DestroyWindow(h);
            UnregisterClassW(new IntPtr(atom), inst);
        }
        Marshal.FreeHGlobal(namePtr);
    }

    static void Main()
    {
        IntPtr inst = GetModuleHandleW(null);
        IntPtr defWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
        uint correct = (uint)Marshal.SizeOf<WNDCLASSEX>();

        Console.WriteLine($"hInstance=0x{inst:X}  DefWindowProcW=0x{defWndProc:X}");
        Console.WriteLine($"sizeof(WNDCLASSEX) with hIconSm = {correct} (native x64 WNDCLASSEXW is 80)");
        Console.WriteLine();

        Try("A: correct cbSize + DefWindowProcW  (expected: works)", "ProbeClsA", correct, defWndProc, inst);
        Try("B: correct cbSize, NULL wndproc     (registers! see note)", "ProbeClsB", correct, IntPtr.Zero, inst);
        Try("C: cbSize 72 (hIconSm omitted)      (expected: 87)", "ProbeClsC", 72, defWndProc, inst);
        Try("D: cbSize 72 AND NULL wndproc       (expected: 87 — what openls used to do)", "ProbeClsD", 72, IntPtr.Zero, inst);
    }
}
