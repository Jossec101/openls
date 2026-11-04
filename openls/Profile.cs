// Profile model matching the decompiled UI/Profile.cs enums and field order,
// plus the DEFAULT profile exactly as installed in Settings.xml.
namespace OpenLs;

public sealed class Profile
{
    // ScalingType indices: Off=0 LS1=1 FSR=2 NIS=3 SGSR=4 BicubicCAS=5 Anime4K=6 XBR=7 SharpBilinear=8 Integer=9 Nearest=10
    // FrameGeneration indices: Off=0 LSFG3=1 LSFG2=2 LSFG1=3 LSFI=4 LSFI1=5
    // SyncMode indices: OFF=0 DEFAULT=1 VSYNC1=2 VSYNC2=3 VSYNC3=4 VSYNC4=5
    // CaptureApi indices: DXGI=0 WGC=1 GDI=2
    // ScalingFitMode: AspectRatio=0 Fullscreen=1
    // ScalingMode: Auto=0 Custom=1

    public string Title { get; set; } = "Default";
    public int ScalingMode { get; set; }
    public int ScalingFitMode { get; set; }
    public float ScaleFactor { get; set; }
    public bool ResizeBeforeScaling { get; set; }
    public int ScalingType { get; set; }
    public int FsrType { get; set; }        // ORIGINAL=0 OPTIMIZED=1
    public int Ls1Type { get; set; }        // BALANCED=0 PERFORMANCE=1
    public int Lsfg2Mode { get; set; }      // X2=0 X3=1 X4=2
    public int Lsfg3Mode { get; set; }      // FIXED=0 ADAPTIVE=1
    public float Lsfg3Multiplier { get; set; }
    public float Lsfg3Target { get; set; }
    public int LsfgFlowScale { get; set; }
    public int LsfgSize { get; set; }       // BALANCED=0 PERFORMANCE=1
    public int Anime4kType { get; set; }    // S=0 M=1 L=2 VL=3 UL=4
    public int Sharpness { get; set; }
    public int Ls1Sharpness { get; set; }
    public bool Vrs { get; set; }
    public int FrameGeneration { get; set; }
    public bool ClipCursor { get; set; }
    public bool AdjustCursorSpeed { get; set; }
    public bool HideCursor { get; set; }
    public bool ScaleCursor { get; set; }
    public int SyncMode { get; set; }
    public int MaxFrameLatency { get; set; }
    public bool Gsync { get; set; }
    public bool Hdr { get; set; }
    public bool DrawFps { get; set; }
    public int CaptureApi { get; set; }
    public int QueueTarget { get; set; }
    public int GpuId { get; set; }
    public int DisplayId { get; set; }
    public int CropLeft { get; set; }
    public int CropTop { get; set; }
    public int CropRight { get; set; }
    public int CropBottom { get; set; }
    public bool MultiDisplayMode { get; set; }

    /// <summary>Defaults identical to the installed app's first profile (Settings.xml),
    /// except CaptureApi (WGC) and Hdr (on).</summary>
    public static Profile Default() => new()
    {
        Title = "Default",
        ScalingMode = 0,            // Auto
        ScalingFitMode = 1,         // Fullscreen
        ScaleFactor = 1.5f,
        ResizeBeforeScaling = false,
        ScalingType = 0,            // Off (raw scale factor only)
        FsrType = 0,                // ORIGINAL
        Ls1Type = 0,                // BALANCED
        Lsfg2Mode = 0,              // X2
        Lsfg3Mode = 1,              // ADAPTIVE
        Lsfg3Multiplier = 2f,
        Lsfg3Target = 144f,
        LsfgFlowScale = 50,
        LsfgSize = 0,               // BALANCED
        Anime4kType = 0,            // S
        Sharpness = 4,
        Ls1Sharpness = 1,
        Vrs = false,
        FrameGeneration = 1,        // LSFG3
        ClipCursor = true,
        AdjustCursorSpeed = false,
        HideCursor = false,
        ScaleCursor = false,
        SyncMode = 1,               // DEFAULT
        MaxFrameLatency = 3,
        Gsync = true,
        Hdr = true,                 // HDR support on (the original ships it off; --no-hdr restores that)
        DrawFps = true,
        CaptureApi = 1,             // WGC (the original ships DXGI; see ParseCaptureApi)
        QueueTarget = 1,
        GpuId = 0,
        DisplayId = 0,
        CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0,
        MultiDisplayMode = false,
    };

    /// <summary>Capture API names as the original's combo box lists them
    /// (decompiled UI.Pages.ProfilePage.xaml: DXGI, WGC; GDI exists in the enum only).</summary>
    public static string CaptureApiName(int api) => api switch { 0 => "DXGI", 1 => "WGC", 2 => "GDI", _ => "?" };

    /// <summary>Parses a --capture value; returns null when it is not one of dxgi/wgc/gdi (or 0/1/2).</summary>
    public static int? ParseCaptureApi(string? s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "dxgi": case "0": return 0;
            case "wgc": case "1": return 1;
            case "gdi": case "2": return 2;
            default: return null;
        }
    }

    /// <summary>Mirrors MainWindow.ApplyProfileToCore from the decompiled app.</summary>
    public void Apply(int setupPhase = 0)
    {
        int subtype = ScalingType switch
        {
            2 => FsrType,      // FSR -> FSRType
            1 => Ls1Type,      // LS1 -> LS1Type
            6 => Anime4kType,  // Anime4K -> Anime4kType
            _ => 0
        };
        int sharpness = ScalingType == 1 ? Ls1Sharpness : Sharpness;
        int fgMode = FrameGeneration == 1 ? Lsfg3Mode : Lsfg2Mode;

        Native.ApplySettings(
            ScalingMode, ScalingFitMode, ScalingType, subtype,
            ScaleFactor, ResizeBeforeScaling,
            sharpness, Vrs,
            FrameGeneration, LsfgSize, fgMode,
            Lsfg3Multiplier, Lsfg3Target, LsfgFlowScale,
            ClipCursor, AdjustCursorSpeed, HideCursor, ScaleCursor,
            SyncMode, MaxFrameLatency, Gsync, Hdr,
            CaptureApi, QueueTarget, DrawFps,
            GpuId, DisplayId,
            CropLeft, CropTop, CropRight, CropBottom,
            MultiDisplayMode, setupPhase);
    }
}