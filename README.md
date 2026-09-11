# openls

A zero-dependency command-line frontend for [Lossless Scaling](https://store.steampowered.com/app/993090/Lossless_Scaling/).
It talks straight to the native engine (`Lossless.dll`) that ships with the
app, so you get frame generation and scaling from a global hotkey without the
WPF window, and a few things the app does not do:

* **Automatic LSFG3 mode.** Measures the game's real framerate and switches
  between adaptive and fixed frame generation: below 60 fps the core runs
  fixed x2 (or x3 when x3 still fits under the target refresh), at or above it
  adaptive to your target. On by default, fully tunable.
* **Live FPS readout.** Real (game) and generated (output) framerate and the
  effective multiplier, measured from Windows' own DXGI present events.
* **Capture API from the command line.** WGC by default, DXGI or GDI on request.
* **Run at sign-in.** One flag registers it under the per-user Run key, another
  removes it.

Everything reports on stdout. No GUI, no telemetry, no network, no NuGet
packages, no configuration files.

> **You need a legitimately purchased, installed copy of Lossless Scaling.**
> openls does not include, redistribute or modify `Lossless.dll`; it loads the
> one from your install. It is an independent project and is not affiliated
> with or endorsed by the developer of Lossless Scaling.

## Requirements

* Windows 10 build 19041 or newer (the same floor the app enforces), x64.
* Lossless Scaling installed (Steam). `Lossless.dll` is auto-discovered by
  walking up the directory tree from the executable, or via `--dll` / the
  `LS_INSTALL` environment variable.
* [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. The build is
  offline; there are no package references.
* Close the original Lossless Scaling app first. The core is single-instance.

## Build and run

```powershell
git clone https://github.com/jossec101/openls
dotnet build openls\openls -c Release

# run from anywhere; put the exe under the Lossless Scaling folder or pass --dll
openls\openls\bin\Release\net10.0-windows\openls.exe --dll "C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling\Lossless.dll"
```

Press **Ctrl+Alt+S** (or your `--hotkey`) with a game focused to engage; press
it again to stop; **Ctrl+C** exits. A typical session:

```
Loaded ...\Lossless.dll - all 11 exports verified.
Core initialized (status listener live).
Windows build >=19041: True | DWM refresh ~144 Hz
GPU adapters (gpuId):
  [0] NVIDIA GeForce RTX 3090
Displays (displayId):
  [0] 3840x2160 (NVIDIA GeForce RTX 3090)
FPS: ETW session live (window 5s). real = the captured game's process, generated = this process.
auto-fg: LSFG3 adaptive@144 while source >= 60 fps, else fixed x2 (x3 when x3 fits under the target) | band +/-5 fps, dwell 20s
hotkey engine: RegisterHotKey - active: Ctrl+Alt+S
default profile: scale x1.5 | type off (raw scale) | LSFG3 adaptive (target 144, multiplier x2) | sync default | gsync true | capture WGC | fps counter true | gpu 0 display 0
applying DEFAULT profile, activating on focused window (Game)
[cb] status=2 hwnd=20492 3840x2076->3840x2076 resized=False scale=1.000 err=0
SCALING "Game": 3840x2076 -> 3840x2076 x1.00
real 48.2 fps (Game 1234) | generated 96.1 fps | LSFG3 fixed x2 | eff x1.99
```

### Run at sign-in

```powershell
# publish a single folder, then register it. Every other flag on the line is kept.
dotnet publish openls\openls -c Release -o C:\Tools\openls
C:\Tools\openls\openls.exe --install-startup --hotkey F8 --fg-target 165

# later
C:\Tools\openls\openls.exe --uninstall-startup
```

The entry lands in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no
elevation), so it also appears under **Task Manager > Startup apps**.
`--minimized` is added automatically so the console sits in the taskbar.

## Options

| Flag | Meaning |
|---|---|
| *(none)* | Register the hotkey, toggle scaling on press, Ctrl+C exits |
| `--dll <path>` | Explicit `Lossless.dll` |
| `--hotkey <combo>` | e.g. `Ctrl+Alt+S` (default), `Alt+P`, `F8` |
| `--capture <dxgi\|wgc\|gdi>` | Capture API handed to the core (default `wgc`). GDI exists in the core's enum but the app never offers it |
| `--install-startup` | Register this exe with every other flag on the line, plus `--minimized`, to run at sign-in; exit |
| `--uninstall-startup` | Remove that registration; exit |
| `--minimized` | Start with the console window minimised |
| `--probe` | Init the core, print adapters/displays/refresh, uninit, exit |
| `--toggle-once` | Init, fire one toggle, wait for a status event, exit |
| `--self-test [ms]` | Normal run; after `ms` (default 2000) inject the hotkey via `SendInput`, then exit: an end-to-end test without a keyboard |
| `--diagnose` | Full triage: who owns the core, elevation, OS build, monitors/adapters, class registration, then an Init probe that reports the core's own HRESULT |
| `--fps-line [ms]` | Log real/generated FPS every `ms` (default 1000) while scaling |
| `--fps-dump [s]` | Collect ETW present events for `s` seconds (default 10) and print which providers/event ids arrived and the per-process present rates |
| `--fps-window <s>` | Averaging window for the rates (default 5) |
| `--auto-fg` / `--no-auto-fg` | Automatic LSFG3 adaptive/fixed selection (default on) |
| `--fg-target <hz>` | Adaptive target, i.e. the profile's `Lsfg3Target` (default 144) |
| `--fg-threshold <fps>` | Source FPS at or above which adaptive is used (default 60) |
| `--fg-fixed <2\|3>` | Pin the fixed-fallback multiplier (default: chosen automatically) |
| `--no-fg-auto-fixed` | Same as `--fg-fixed 2` |
| `--fg-band <fps>` | Hysteresis band (default 5) |
| `--fg-dwell <s>` | Minimum seconds between switches (default 20) |

The profile applied is the app's stock default with one change, the capture
API: x1.5 scale, fullscreen fit, scaling type off, LSFG3 adaptive to 144 (x2
fixed fallback), sync default, G-Sync on, **WGC** capture, FPS counter on, GPU
and display 0. `--capture dxgi` gives you the app's own default. The full
mapping is in `openls/Profile.cs`.

## How it works

### Threading

The app calls the core from its WPF UI thread: STA, OLE-initialised, with a
message pump, and marshals every later `ApplySettings` / `Activate` back onto
it. Calling `Init` from a plain console `Main` (MTA, no pump) makes the core
throw a native `winrt::hresult_error`, which surfaces in .NET as an opaque
`SEHException`. `CoreThread.cs` reproduces the original's threading exactly:
one STA thread with a message loop owns the core, and everything else posts
to it. When `Init` does fail, the message carries the core's own HRESULT and
description, recovered with `GetRestrictedErrorInfo` on that thread.

### Hotkey

Primary engine: `RegisterHotKey` on a hidden message-only window owned by the
core thread, so `WM_HOTKEY` is handled by the pump that owns the core.
Fallback: `GetAsyncKeyState` polling (20 ms, edge-triggered) if class
registration or `RegisterHotKey` is refused. The app itself uses a
`WH_KEYBOARD_LL` hook.

### Capture API

`ApplySettings` takes `captureApi` as an int: `0` DXGI, `1` WGC, `2` GDI
(the app's `Profile.CaptureApiEnum`; its combo box lists only the first two).
The app's own tooltip is the best summary of the trade-off: WGC output can be
recorded by any software and is colour-managed by Windows 11's "Automatically
manage color for apps", while DXGI needs OBS game capture and the HDR option
instead; WGC may also perform better when the core runs on a secondary GPU.

### FPS measurement

`Lossless.dll` exports exactly 11 functions and none of them report a
framerate; the status callback carries resolutions and a scale factor only.
So both numbers are measured from outside with a real-time ETW session on
`Microsoft-Windows-DXGI` counting `PresentStart` events (`EtwFps.cs`):

* **real**: presents by the captured game's process.
* **generated**: presents by *this* process, since the core presents the
  scaled and interpolated frames from inside openls.
* **eff**: `generated / real`, the multiplier actually being achieved.

Notes:

* Keyword `0x2` ("Events"), not `0x1`; event id 42 is `PresentStart`, 43 is
  `PresentStop`, and only 42 is counted so a frame counts once. Verify on your
  own machine with `--fps-dump`.
* An ETW session normally needs administrator rights or membership in
  Performance Log Users. Where it cannot start, openls logs one line and runs
  on with the FPS features (and therefore auto-fg) off; scaling and hotkeys
  are unaffected.
* The session is stopped on exit and a leaked one is reclaimed at startup.
* `real` reads the *window's* owning process. Apps that render in a child GPU
  process (browsers, Electron) show `n/a`. Vulkan and OpenGL titles that bypass
  DXGI are the other blind spot; `Microsoft-Windows-DxgKrnl` would cover those
  and is not wired up yet.

### Automatic LSFG3 mode

Driven by the real (source) framerate:

* `source >= threshold + band`: LSFG3 **adaptive**, targeting `--fg-target`.
* `source < threshold - band`: LSFG3 **fixed**; x2 by default, or **x3** when
  `source * 3` still lands under `target + band`. With the defaults (60 fps
  threshold, 144 target) that means roughly: below ~50 fps picks x3, between
  ~50 and 60 picks x2, above 60 adaptive. `--fg-fixed` pins the multiplier.

The band prevents flapping on the threshold and `--fg-dwell` keeps switches
rare. While the source rate is unavailable, the current mode is held.

A switch is applied as a toggle-cycle spread over three one-second ticks:
`Activate` off and `ApplySettings`, then `Activate` back on, then confirm via
the status callback. Firing the three calls back-to-back lands the
re-activation inside the core's asynchronous capture teardown, where it is
silently dropped. Re-applying settings to a live core is also the only path the
app exercises (it does so while scaling is paused), so a live `ApplySettings`
mid-scale is deliberately avoided.

## Troubleshooting

* **`CORE UNAVAILABLE`**: run `--diagnose`. It prints who owns the core
  (process list and the `LosslessScaling` single-instance mutex), elevation,
  OS build, monitors and adapters, a class-registration probe, and finally an
  Init probe that reports the core's own HRESULT. Treat that HRESULT as the
  cause; the environment is rarely the problem.
* **`FPS: ...could not start the ETW session`**: run elevated, or add your user
  to the Performance Log Users group. Everything else keeps working.
* **Scaling goes off and stays off after an auto-fg switch**: the log says so
  explicitly; press the hotkey to re-engage. Raise `--fg-dwell` if it is
  frequent.
* **`real n/a`**: the focused window's process is not the one presenting (see
  the FPS notes above).

## The core's ABI

Everything below was recovered from the app's managed assembly
(`LosslessScaling.dll`, `UI/Core.cs` and friends) and from the export table of
`Lossless.dll`. The decompiled sources are not part of this repository.

Exactly 11 exports, all `Cdecl`:

| Export | Signature | Use |
|---|---|---|
| `Init` | `bool Init(StatusListener cb)` | bring the core up; installs the status callback |
| `UnInit` | `void UnInit()` | teardown |
| `Activate` | `bool Activate(HWND)` | toggle: off to on focuses target, on to off. `HWND 0` = current foreground |
| `ApplySettings` | 33 args (below) | load a profile into the core |
| `GetForegroundWindowEx` | `HWND` | core's notion of the foreground window |
| `GetAdapterNames` | `BSTR` (`;`-separated) | gpuId list, valid **after `Init`** |
| `GetDisplayNames` | `BSTR` (`;`-separated) | displayId list, valid **after `Init`** |
| `SetDriverSettings` | `bool` | (re)apply driver settings |
| `IsWindowsBuildAtLeast` | `bool IsWindowsBuildAtLeast(uint)` | the app's 19041 gate, valid **after `Init`** |
| `GetDwmRefreshRate` | `float` | DWM refresh; works before `Init` |
| `SetWindowsSettings` | `bool SetWindowsSettings(int gpuPreference)` | GPU preference (the app restarts itself after) |

Status callback (Cdecl, 9 args):
`void(int status, HWND, int iw, int ih, int ow, int oh, bool resized, float scale, int errorCode)`
with `status` 0 = off, 1 = paused (focus elsewhere), 2 = active, and
`errorCode` 1 = RESIZE_FAILED, 2 = LARGE_OFFSET, 3 = CAPTURE_FAILED.

`ApplySettings` argument order (byte-for-byte the app's `UI/Core.cs` contract;
the order is part of the ABI):

```
int scalingMode, int scalingFitMode, int scalingType, int scalingSubtype,
float scaleFactor, bool resizeBeforeScale, int sharpness, bool vrs,
int frameGenerationType, int frameGenerationSize, int frameGenerationMode,
float frameGenerationMultiplier, float frameGenerationTarget, int frameGenerationFlowScale,
bool clipCursor, bool cursorSensitivity, bool hideCursor, bool scaleCursor,
int syncMode, int maxFrameLatency, bool gsyncSupport, bool hdrSupport,
int captureApi, int queueTarget, bool drawFps, int gpuId, int displayId,
int captureOffsetLeft, int captureOffsetTop, int captureOffsetRight, int captureOffsetBottom,
bool multiDisplayMode, int setupPhase
```

Enums keep the app's values: `FrameGeneration {Off=0, LSFG3=1, LSFG2=2,
LSFG1=3, LSFI=4, LSFI1=5}`, `LSFG3Mode {FIXED=0, ADAPTIVE=1}`,
`SyncMode {Off=0, Default=1, Vsync1..4}`, `CaptureApi {DXGI=0, WGC=1, GDI=2}`,
`ScalingType {Off=0, LS1, FSR, NIS, SGSR, BicubicCAS, Anime4K, XBR,
SharpBilinear, Integer, Nearest}`.

Toggle semantics mirror the app's `MainWindow.ActivateCore`: off to on is
`ApplySettings(profile)` then `Activate(0)`; on to off is `Activate(0)` again.
The status stream is authoritative; local state is only optimistic between
events.

### Things that bit us

Recorded because each one looked like an environment problem and was not:

* `RegisterClassExW` failing with Win32 error 87 was a `WNDCLASSEX` declaration
  missing its 12th field (`hIconSm`), so `cbSize` was 72 instead of 80. The
  `probe/` project isolates this.
* `Init` throwing `SEHException` was the threading issue described above, not
  a missing GPU.
* `IsWindowsBuildAtLeast`, `GetAdapterNames` and `GetDisplayNames` read state
  that `Init` populates. Called before it they return "build too old" and an
  empty adapter list on a perfectly good machine.

## Layout

```
openls/openls.csproj   net10.0-windows Exe, x64, no packages
openls/app.manifest    asInvoker + PerMonitorV2, matching the app's manifest
openls/Native.cs       ABI: 11 DllImports, StatusListener, BSTR helpers, export check,
                       LoadLibraryEx, GetRestrictedErrorInfo, SendInput, console helpers
openls/CoreThread.cs   the STA, message-pumped thread that owns the core
openls/Hotkey.cs       message-only window + RegisterHotKey; register/poll engines
openls/EtwFps.cs       real-time ETW session, per-process present rates
openls/AutoFrameGen.cs adaptive/fixed LSFG3 control loop
openls/Profile.cs      profile model, Default(), 33-arg Apply(), capture-API names
openls/Startup.cs      --install-startup / --uninstall-startup via the HKCU Run key
openls/Program.cs      CLI: args, probe, diagnose, init, engines, status stream, fps
probe/                 standalone RegisterClassExW probe
```

## Roadmap

* Profiles: persistence, several of them, `Settings.xml` compatibility.
* A tray icon and a hidden (rather than minimised) console when run at sign-in.
* `Microsoft-Windows-DxgKrnl` present events, for Vulkan/OpenGL titles.
* `SetDriverSettings` / `SetWindowsSettings` / GPU-preference management.
* Restart the core if it crash-exits.

## License

MIT, see [LICENSE](LICENSE). Lossless Scaling and `Lossless.dll` are the
property of their respective owner and are not covered by this license.
