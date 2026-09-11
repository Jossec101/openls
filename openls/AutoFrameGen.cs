// Automatic LSFG3 adaptive-vs-fixed selection, driven by the game's real
// (source) framerate measured in EtwFps.
//
//   source >= threshold + band  ->  LSFG3 ADAPTIVE targeting --fg-target
//   source <  threshold - band  ->  LSFG3 FIXED at x2, or x3 when x3 still fits
//                                   under the target
//
// The band gives hysteresis so a source rate sitting on the threshold does not
// flap, and a dwell timer keeps switches rare regardless.
//
// A switch is applied by toggle-cycle (deactivate, ApplySettings, reactivate),
// which is the only path the original app exercises: it re-applies to a live
// core solely while scaling is paused (decompiled MainWindow.cs:531-537) and
// never while it is actively scaling.
namespace OpenLs;

public sealed class AutoFrameGen : IDisposable
{
    public sealed record Options(
        float Target = 144f,
        double Threshold = 72,
        double Band = 5,
        int DwellSeconds = 20,
        int? PinnedFixed = null);   // null = pick x2/x3 per switch

    private readonly CoreThread _core;
    private readonly EtwFps _fps;
    private readonly Options _opt;
    private readonly Profile _profile;
    private readonly Func<int> _status;
    private readonly Func<IntPtr> _targetHwnd;
    private readonly Action<string> _log;
    private Timer? _timer;
    private DateTime _lastSwitch = DateTime.MinValue;
    private bool _adaptive;
    private bool _disposed;

    /// <summary>A switch runs across ticks: deactivate+apply, then re-engage, then confirm.</summary>
    private enum Phase { Idle, Reactivate, Verify }

    private Phase _phase = Phase.Idle;
    private IntPtr _pendingHwnd;
    private string _pendingWhat = "";

    public AutoFrameGen(CoreThread core, EtwFps fps, Options opt, Profile profile,
        Func<int> status, Func<IntPtr> targetHwnd, Action<string> log)
    {
        _core = core;
        _fps = fps;
        _opt = opt;
        _profile = profile;
        _status = status;
        _targetHwnd = targetHwnd;
        _log = log;
        _adaptive = profile.Lsfg3Mode == 1;
    }

    public void Start() => _timer = new Timer(_ => Evaluate(), null, 1000, 1000);

    public string Describe() =>
        $"auto-fg: LSFG3 adaptive@{_opt.Target:0.#} while source >= {_opt.Threshold:0.#} fps, " +
        (_opt.PinnedFixed is int pin ? $"else fixed x{pin}" : "else fixed x2 (x3 when x3 fits under the target)") +
        $" | band +/-{_opt.Band:0.#} fps, dwell {_opt.DwellSeconds}s";

    private void Evaluate()
    {
        if (_disposed) return;
        try
        {
            // A switch in progress owns the next couple of ticks.
            if (_phase != Phase.Idle) { AdvanceSwitch(); return; }

            if (_status() == 0) return;                     // not scaling: nothing to tune

            IntPtr hwnd = _targetHwnd();
            int pid = Native.WindowPid(hwnd);
            if (pid == 0) return;

            double? src = _fps.Rate(pid);
            if (src == null) return;                        // window still filling

            // Hysteresis: only cross the threshold by more than the band.
            bool want = _adaptive
                ? src.Value >= _opt.Threshold - _opt.Band
                : src.Value >= _opt.Threshold + _opt.Band;
            if (want == _adaptive) return;

            double sinceSwitch = (DateTime.UtcNow - _lastSwitch).TotalSeconds;
            if (sinceSwitch < _opt.DwellSeconds) return;

            BeginSwitch(want, src.Value, hwnd);
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle;
            _log("auto-fg: evaluation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
    }

    // The switch is spread over ticks rather than done in one burst. Firing
    // Activate(off), ApplySettings and Activate(on) back-to-back leaves the
    // re-activation landing inside the core's asynchronous capture teardown,
    // where it is silently dropped: observed live as scaling going off and
    // staying off for 39s until an unrelated foreground change woke the core.
    private void BeginSwitch(bool adaptive, double src, IntPtr hwnd)
    {
        if (adaptive)
        {
            _profile.Lsfg3Mode = 1;
            _profile.Lsfg3Target = _opt.Target;
            _pendingWhat = $"adaptive@{_opt.Target:0.#}";
            _log($"auto-fg: source {src:0.0} fps >= {_opt.Threshold + _opt.Band:0.#} ({_opt.Threshold:0.#}+{_opt.Band:0.#}) — switching LSFG3 to {_pendingWhat}");
        }
        else
        {
            // x2 unless x3 still lands under the target.
            float mult = _opt.PinnedFixed ?? (src * 3 <= _opt.Target + _opt.Band ? 3 : 2);
            _profile.Lsfg3Mode = 0;
            _profile.Lsfg3Multiplier = mult;
            _pendingWhat = $"fixed x{mult:0.#}";
            _log($"auto-fg: source {src:0.0} fps < {_opt.Threshold - _opt.Band:0.#} ({_opt.Threshold:0.#}-{_opt.Band:0.#}) — switching LSFG3 to {_pendingWhat}");
        }

        bool offOk = _core.Invoke(() =>
        {
            bool r = Native.Activate(IntPtr.Zero);
            _profile.Apply();
            return r;
        });
        _log($"auto-fg: deactivated (Activate() {(offOk ? "ok" : "returned false")}), settings applied — re-engaging on the next tick");

        _pendingHwnd = hwnd;
        _adaptive = adaptive;
        _lastSwitch = DateTime.UtcNow;
        _phase = Phase.Reactivate;
    }

    private void AdvanceSwitch()
    {
        switch (_phase)
        {
            case Phase.Reactivate:
            {
                // Prefer Activate(0) — "toggle on the current foreground window" is
                // the path the hotkey uses and the only one proven to engage. The
                // stored hwnd is the fallback for when focus has moved on.
                IntPtr fg = _core.Invoke(Native.GetForegroundWindowEx);
                IntPtr use = fg == _pendingHwnd ? IntPtr.Zero : _pendingHwnd;
                bool onOk = _core.Invoke(() => Native.Activate(use));
                _log($"auto-fg: re-engaging {_pendingWhat} on " +
                     (use == IntPtr.Zero
                        ? "the foreground window"
                        : $"hwnd {_pendingHwnd:X} (foreground has moved to \"{Native.WinTitle(fg)}\")") +
                     $" — Activate() {(onOk ? "ok" : "returned false")}");
                _phase = Phase.Verify;
                break;
            }

            case Phase.Verify:
            {
                if (_status() == 0)
                    _log($"auto-fg: scaling did NOT resume after switching to {_pendingWhat}. " +
                         "The core stayed off; press the hotkey to re-engage.");
                else
                    _log($"auto-fg: {_pendingWhat} engaged (status {_status()}).");
                _phase = Phase.Idle;
                break;
            }
        }
    }

    public string CurrentMode() =>
        _profile.Lsfg3Mode == 1
            ? $"LSFG3 adaptive->{_profile.Lsfg3Target:0.#}"
            : $"LSFG3 fixed x{_profile.Lsfg3Multiplier:0.#}";

    public void Dispose()
    {
        _disposed = true;
        try { _timer?.Dispose(); } catch { }
    }
}
