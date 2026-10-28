using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

/// <summary>Which of the iPhone Duo's two displays is in use.</summary>
internal enum DuoPose { Folded, Open }

/// <summary>
/// iPhone Duo on a MuMu emulator. The emulator runs at the bounding box of both displays (2670 × 2034 at 480 dpi) and
/// Android's <c>wm size</c> switches between them live: the closed phone's 1398 × 2034 outer display or the open
/// phone's 2670 × 1878 inner one. Each shares one side with the bounding box, so Android draws it at exactly 1:1,
/// centred on MuMu's render surface with black bars around it. Everything here is pure geometry and text.
/// </summary>
internal static class IPhoneDuo
{
    public const string Category = "Apple iPhone", Model = "iPhone Duo";
    public const int Density = 480;

    /// <summary>MuMu's render surface as seen in its window: both displays fit inside it (landscape).</summary>
    public static readonly Size Surface = new(2670, 2034);
    /// <summary>Closed: the outer display, portrait, hinge on the left.</summary>
    public static readonly Size OuterScreen = new(1398, 2034);
    /// <summary>Open: the inner display, landscape.</summary>
    public static readonly Size InnerScreen = new(2670, 1878);

    public static Size ScreenSize(DuoPose pose) => pose == DuoPose.Folded ? OuterScreen : InnerScreen;
    public static string Variant(DuoPose pose) => pose == DuoPose.Folded ? "Closed" : "Open";
    public static string FrameKey(DuoPose pose) => $"{Category}/{Model}/{Variant(pose)}";
    public static DuoPose Other(DuoPose pose) => pose == DuoPose.Folded ? DuoPose.Open : DuoPose.Folded;

    public static bool IsDuo(FrameInfo? frame) => frame != null &&
        string.Equals(frame.Category, Category, StringComparison.OrdinalIgnoreCase) && string.Equals(frame.Model, Model, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pose a Duo frame variant shows ("Closed" or "Open").</summary>
    public static DuoPose? PoseOf(FrameInfo? frame) => !IsDuo(frame) ? null
        : string.Equals(frame!.Variant, Variant(DuoPose.Open), StringComparison.OrdinalIgnoreCase) ? DuoPose.Open : DuoPose.Folded;

    /// <summary>The pose whose display has this size (a screenshot, say), in either orientation.</summary>
    public static DuoPose? PoseForScreen(Size size) =>
        SameSize(size, OuterScreen) ? DuoPose.Folded : SameSize(size, InnerScreen) ? DuoPose.Open : null;

    /// <summary>True when Android's physical display is the Duo bounding box (MuMu reports it in either orientation).</summary>
    public static bool IsSurface(Size physical) => SameSize(physical, Surface);

    /// <summary>The pose Android is in, from its physical display and <c>wm size</c> override; null when it is in neither.</summary>
    public static DuoPose? Detect(Size physical, Size? overrideSize) =>
        IsSurface(physical) && overrideSize is { } size ? PoseForScreen(size) : null;

    public static DuoPose? Detect(AndroidDisplay? display) => display == null ? null : Detect(display.Native, display.Override);

    /// <summary>Pose from the output of <c>adb shell wm size</c> ("Physical size: …" and, once set, "Override size: …").</summary>
    public static DuoPose? ParsePose(string wmSizeOutput)
    {
        var physical = ParseSize(wmSizeOutput, "Physical size");
        return physical == null ? null : Detect(physical.Value, ParseSize(wmSizeOutput, "Override size"));
    }

    public static Size? ParseSize(string text, string label)
    {
        var match = Regex.Match(text, Regex.Escape(label) + @":\s*(\d+)x(\d+)");
        return match.Success ? new Size(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }

    /// <summary>
    /// Where Android puts a pose on the render surface (surface pixels, as seen). It centres a smaller logical display
    /// with integer halving, and since each pose shares one side with the surface the scale is exactly 1.
    /// </summary>
    public static Rectangle PictureOnSurface(DuoPose pose)
    {
        var size = ScreenSize(pose);
        return new Rectangle((Surface.Width - size.Width) / 2, (Surface.Height - size.Height) / 2, size.Width, size.Height);
    }

    /// <summary>
    /// The same rectangle in the emulator window's render pixels: MuMu scales the whole surface into its window, so the
    /// picture is a centred sub-rectangle there too. Null when the window is not showing the surface the Duo way round.
    /// </summary>
    public static Rectangle? PictureInRender(DuoPose pose, Size render)
    {
        if (render.Width <= 0 || render.Height <= 0 || render.Width < render.Height) return null;
        var p = PictureOnSurface(pose);
        double sx = (double)render.Width / Surface.Width, sy = (double)render.Height / Surface.Height;
        int x0 = (int)Math.Round(p.Left * sx), x1 = (int)Math.Round(p.Right * sx);
        int y0 = (int)Math.Round(p.Top * sy), y1 = (int)Math.Round(p.Bottom * sy);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>
    /// The shell commands that put an emulator in a pose. <c>wm size</c> takes the display's natural orientation, and
    /// MuMu reports a landscape resolution as a portrait panel turned 90°, so sizes are swapped for it. Rotation is
    /// pinned (and app orientation requests ignored for rotation) so a portrait-only app cannot turn the display round.
    /// </summary>
    public static string[] Commands(DuoPose pose, Size physical)
    {
        bool turned = physical.Width < physical.Height;
        var size = ScreenSize(pose);
        var natural = turned ? new Size(size.Height, size.Width) : size;
        return
        [
            "wm fixed-to-user-rotation enabled",
            $"wm user-rotation lock {(turned ? 1 : 0)}",
            $"wm size {natural.Width}x{natural.Height}",
            $"wm density {Density}",
        ];
    }

    /// <summary>True when an emulator window's picture has the Duo surface's aspect ratio (within 0.5 %).</summary>
    public static bool LooksLikeSurface(Size render) => render.Width > 0 && render.Height > 0 &&
        Math.Abs((double)Math.Max(render.Width, render.Height) / Math.Min(render.Width, render.Height) / ((double)Surface.Width / Surface.Height) - 1) < 0.005;

    private static bool SameSize(Size a, Size b) => a == b || (a.Width == b.Height && a.Height == b.Width);
}
