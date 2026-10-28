using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

/// <summary>
/// How the iPhone Duo is held, Apple's six poses. Closed and tent use the outer display (tent: folded, standing like an
/// A-frame, the outer display landscape); the other four the inner one: open flat or half open like a book
/// (landscape, fold down the middle), and open flat or half open as a tabletop (portrait, fold across).
/// </summary>
internal enum DuoPose { Folded, Open, HalfOpen, OpenPortrait, Tabletop, Tent }

/// <summary>
/// What a Duo emulator's Android reports beyond <c>wm size</c>: the size apps see now (rotation included), and, once it
/// has been made a foldable, its device state (0 closed, 1 half open, 2 open) and whether the postures overlay is on.
/// </summary>
internal sealed record DuoDevice(Size? Current, bool Foldable, int? FoldState, bool Postures);

/// <summary>
/// iPhone Duo on a MuMu emulator. The emulator runs at the bounding box of both displays (2670 × 2034 at 480 dpi) and
/// Android's <c>wm size</c> switches between them live: the closed phone's 1398 × 2034 outer display or the open
/// phone's 2670 × 1878 inner one, which can also be turned portrait (1878 × 2670, MuMu turns its window with it). Each
/// shares one side with the surface, so Android draws it at exactly 1:1, centred, with black bars around it.
///
/// Made a foldable (see <see cref="DuoSetup"/>), Android also has the device states CLOSED, HALF_OPENED and OPENED, a
/// postures overlay that maps them to Jetpack WindowManager's postures, and a fold line in <c>display_features</c>,
/// so apps get a FoldingFeature the way they do on a Galaxy Z Fold. Everything here is pure geometry and text.
/// </summary>
internal static class IPhoneDuo
{
    public const string Category = "Apple iPhone", Model = "iPhone Duo";
    public const int Density = 480;

    /// <summary>MuMu's render surface as seen in its window, the usual (landscape) way round.</summary>
    public static readonly Size Surface = new(2670, 2034);
    /// <summary>Closed: the outer display, portrait, hinge on the left.</summary>
    public static readonly Size OuterScreen = new(1398, 2034);
    /// <summary>Open, book: the inner display, landscape.</summary>
    public static readonly Size InnerScreen = new(2670, 1878);
    /// <summary>Open portrait, tabletop: the inner display turned upright.</summary>
    public static readonly Size InnerPortrait = new(1878, 2670);
    /// <summary>Tent: the outer display turned landscape, the hinge on top, the outer camera bottom right.</summary>
    public static readonly Size TentScreen = new(2034, 1398);

    /// <summary>The order of the pose buttons and of Ctrl+Alt+1 to 6 (tent last, so the first five keep their keys).</summary>
    public static readonly DuoPose[] Poses = [DuoPose.Folded, DuoPose.Open, DuoPose.HalfOpen, DuoPose.OpenPortrait, DuoPose.Tabletop, DuoPose.Tent];

    public static bool IsInner(DuoPose pose) => pose is not (DuoPose.Folded or DuoPose.Tent);
    /// <summary>Open portrait and tabletop: the inner display upright, the fold across it.</summary>
    public static bool IsUpright(DuoPose pose) => pose is DuoPose.OpenPortrait or DuoPose.Tabletop;
    /// <summary>Book and tabletop: the halves at an angle (device state HALF_OPENED).</summary>
    public static bool IsHalf(DuoPose pose) => pose is DuoPose.HalfOpen or DuoPose.Tabletop;

    public static Size ScreenSize(DuoPose pose) => pose == DuoPose.Tent ? TentScreen : !IsInner(pose) ? OuterScreen : IsUpright(pose) ? InnerPortrait : InnerScreen;

    /// <summary>
    /// The poses Android shows at rotation 0 on MuMu's turned panel, so MuMu turns its window upright for them: open
    /// portrait, tabletop, and tent. Tent is landscape, but at rotation 90 MuMu stretches a landscape display narrower
    /// than its window to the window's width (the picture comes out magnified and cropped); at 0 it is drawn 1:1.
    /// </summary>
    public static bool TurnsWindow(DuoPose pose) => IsUpright(pose) || pose == DuoPose.Tent;

    /// <summary>The render surface as seen for a pose: MuMu's window is upright for <see cref="TurnsWindow"/> poses.</summary>
    public static Size SurfaceFor(DuoPose pose) => TurnsWindow(pose) ? new Size(Surface.Height, Surface.Width) : Surface;

    /// <summary>Frame variant names, also what menus show.</summary>
    public static string Variant(DuoPose pose) => pose switch
    {
        DuoPose.Folded => "Closed",
        DuoPose.HalfOpen => "Book",
        DuoPose.OpenPortrait => "Open portrait",
        DuoPose.Tabletop => "Tabletop",
        DuoPose.Tent => "Tent",
        _ => "Open",
    };

    /// <summary>Command-line names (<c>--duo-pose book</c>).</summary>
    public static string CliName(DuoPose pose) => pose switch
    {
        DuoPose.Folded => "closed",
        DuoPose.HalfOpen => "book",
        DuoPose.OpenPortrait => "portrait",
        DuoPose.Tabletop => "tabletop",
        DuoPose.Tent => "tent",
        _ => "open",
    };

    public static DuoPose? ParseCliName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "closed" or "fold" or "folded" => DuoPose.Folded,
        "open" or "unfold" or "flat" => DuoPose.Open,
        "book" or "half" => DuoPose.HalfOpen,
        "portrait" or "open-portrait" => DuoPose.OpenPortrait,
        "tabletop" or "table" or "laptop" => DuoPose.Tabletop,
        "tent" => DuoPose.Tent,
        _ => null,
    };

    /// <summary>One line on the display and the fold, for tooltips and menus.</summary>
    public static string Describe(DuoPose pose) => pose switch
    {
        DuoPose.Folded => "Closed: outer display, 466 × 678 pt",
        DuoPose.Open => "Open: 890 × 626 pt, flat",
        DuoPose.HalfOpen => "Book: 890 × 626 pt, half open, fold down the middle",
        DuoPose.OpenPortrait => "Open portrait: 626 × 890 pt, flat",
        DuoPose.Tent => "Tent: outer display landscape, 678 × 466 pt, standing folded",
        _ => "Tabletop: 626 × 890 pt, half open, fold across the middle",
    };

    public static string FrameKey(DuoPose pose) => $"{Category}/{Model}/{Variant(pose)}";

    /// <summary>Android device state for a pose, as defined in <see cref="DeviceStateConfig"/>.</summary>
    public static int StateFor(DuoPose pose) => !IsInner(pose) ? 0 : IsHalf(pose) ? 1 : 2;

    /// <summary>Fold / unfold toggle: closed opens flat, anything open closes.</summary>
    public static DuoPose Toggle(DuoPose? pose) => pose is DuoPose.Folded or DuoPose.Tent ? DuoPose.Open : DuoPose.Folded;

    /// <summary>Half-open toggle, keeping the way round: book and open swap, so do tabletop and open portrait.</summary>
    public static DuoPose ToggleHalf(DuoPose? pose) => pose switch
    {
        DuoPose.HalfOpen => DuoPose.Open,
        DuoPose.OpenPortrait => DuoPose.Tabletop,
        DuoPose.Tabletop => DuoPose.OpenPortrait,
        _ => DuoPose.HalfOpen,
    };

    public static bool IsDuo(FrameInfo? frame) => frame != null &&
        string.Equals(frame.Category, Category, StringComparison.OrdinalIgnoreCase) && string.Equals(frame.Model, Model, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pose a Duo frame variant shows.</summary>
    public static DuoPose? PoseOf(FrameInfo? frame) => !IsDuo(frame) ? null
        : Poses.Cast<DuoPose?>().FirstOrDefault(p => string.Equals(frame!.Variant, Variant(p!.Value), StringComparison.OrdinalIgnoreCase)) ?? DuoPose.Folded;

    /// <summary>The display a screenshot of this size shows: outer (Folded), inner landscape (Open) or upright (OpenPortrait).</summary>
    public static DuoPose? PoseForScreen(Size size) =>
        size == OuterScreen ? DuoPose.Folded : size == TentScreen ? DuoPose.Tent : size == InnerScreen ? DuoPose.Open : size == InnerPortrait ? DuoPose.OpenPortrait : null;

    /// <summary>
    /// The pose to frame a screenshot in: the screenshot's size says which display and which way round, the pose shown
    /// says whether the halves were flat or at an angle.
    /// </summary>
    public static DuoPose? ForShot(DuoPose? shown, Size shot)
    {
        bool half = shown is { } p && IsHalf(p);
        return PoseForScreen(shot) switch
        {
            DuoPose.Folded => DuoPose.Folded,
            DuoPose.Tent => DuoPose.Tent,
            DuoPose.Open => half ? DuoPose.HalfOpen : DuoPose.Open,
            DuoPose.OpenPortrait => half ? DuoPose.Tabletop : DuoPose.OpenPortrait,
            _ => null,
        };
    }

    /// <summary>True when Android's physical display is the Duo bounding box (MuMu reports it in either orientation).</summary>
    public static bool IsSurface(Size physical) => SameSize(physical, Surface);

    /// <summary>
    /// The pose Android is in: the display from the <c>wm size</c> override, which way round from the size apps see
    /// now, and flat or half open from the device state. Null when the emulator is in no Duo pose.
    /// </summary>
    public static DuoPose? Detect(Size physical, Size? overrideSize, int? foldState = null, Size? current = null)
    {
        if (!IsSurface(physical) || overrideSize is not { } size || !SameSize(size, OuterScreen) && !SameSize(size, InnerScreen)) return null;
        // The outer display: closed shows it upright, tent turns it landscape.
        if (SameSize(size, OuterScreen)) return current is { } shown && shown.Width > shown.Height ? DuoPose.Tent : DuoPose.Folded;
        bool upright = current is { } now && now.Width < now.Height;
        bool half = foldState == StateFor(DuoPose.HalfOpen);
        return upright ? half ? DuoPose.Tabletop : DuoPose.OpenPortrait : half ? DuoPose.HalfOpen : DuoPose.Open;
    }

    public static DuoPose? Detect(AndroidDisplay? display) =>
        display == null ? null : Detect(display.Native, display.Override, display.Duo?.FoldState, display.Duo?.Current);

    /// <summary>
    /// True when everything a pose sets still holds: apps see the pose's display the right way round (MuMu turns the
    /// display back after a restart, and sometimes when an app it remembers comes to the front), and a foldable is in
    /// the pose's device state (a restart clears the state override). False means the pose commands should run again.
    /// </summary>
    public static bool InStep(DuoPose pose, AndroidDisplay? display) =>
        display?.Duo is { } duo && duo.Current == ScreenSize(pose) && (!duo.Foldable || duo.FoldState == StateFor(pose));

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

    // ---- Android foldable ---------------------------------------------------------------------------------------

    /// <summary>The device states, for /data/system/devicestate/device_state_configuration.xml. No conditions: the
    /// base state is CLOSED and the pose commands override it. No display properties, so no display switching.</summary>
    public const string DeviceStateConfig =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <device-state-config>
            <device-state>
                <identifier>0</identifier>
                <name>CLOSED</name>
            </device-state>
            <device-state>
                <identifier>1</identifier>
                <name>HALF_OPENED</name>
            </device-state>
            <device-state>
                <identifier>2</identifier>
                <name>OPENED</name>
            </device-state>
        </device-state-config>

        """;

    public const string PosturesOverlay = "com.justsuperhuman.deviceframes.foldable";

    /// <summary>
    /// <c>config_device_state_postures</c>: device state to Jetpack posture (1 no folding features, 2 half opened,
    /// 3 flat). Closed reports no fold, like a closed Galaxy Z Fold whose outer display has none.
    /// </summary>
    public const string PosturesValues =
        "    <string-array name=\"config_device_state_postures\" translatable=\"false\">\n" +
        "        <item>0:1</item>\n        <item>1:2</item>\n        <item>2:3</item>\n" +
        "    </string-array>\n";

    /// <summary>
    /// The fold for <c>Settings.Global display_features</c>: a line down the middle of the inner display, in the
    /// display's natural orientation (on MuMu's turned panel that is a horizontal line across the portrait panel).
    /// </summary>
    public static string FoldFeature(Size physical)
    {
        int fold = InnerScreen.Width / 2, across = InnerScreen.Height;
        return physical.Width < physical.Height ? $"fold-[0,{fold},{across},{fold}]" : $"fold-[{fold},0,{fold},{across}]";
    }

    /// <summary>
    /// The shell commands that put an emulator in a pose. <c>wm size</c> takes the display's natural orientation, and
    /// MuMu reports a landscape resolution as a portrait panel turned 90°, so sizes are swapped for it. Rotation is
    /// pinned (and app orientation requests ignored for rotation) so a portrait-only app cannot turn the display round.
    /// On a foldable the order matters: the fold is only ever reported while the window is the inner display's size
    /// (Android's extensions throw in the app for a fold that does not span the window), so closing drops the device
    /// state before shrinking and opening grows the display before setting the state.
    /// </summary>
    public static string[] Commands(DuoPose pose, Size physical, bool foldable = false)
    {
        bool turned = physical.Width < physical.Height;
        // wm size is the display in the panel's own orientation, the same for both ways round of the inner display;
        // the rotation turns it: on MuMu's turned panel 1 shows it landscape, 0 upright (see RotationFor).
        var landscape = IsInner(pose) ? InnerScreen : OuterScreen; // tent is the outer display too, at the other rotation
        var natural = turned ? new Size(landscape.Height, landscape.Width) : landscape;
        string[] display =
        [
            "wm fixed-to-user-rotation enabled",
            $"wm user-rotation lock {RotationFor(pose, turned)}",
            $"wm size {natural.Width}x{natural.Height}",
            $"wm density {Density}",
        ];
        if (!foldable) return display;
        return IsInner(pose)
            ? [.. display, $"settings put global display_features {FoldFeature(physical)}", $"cmd device_state state {StateFor(pose)}"]
            : [$"cmd device_state state {StateFor(pose)}", "settings delete global display_features", .. display];
    }

    /// <summary>
    /// The display rotation for a pose: on MuMu's turned panel 90 for the rail poses and 0 upright; on a landscape panel
    /// 0 and 90. <see cref="DuoSafeAreas"/> turns each pose group's insets back onto the panel's edges with it.
    /// </summary>
    public static int RotationFor(DuoPose pose, bool turned) => turned ? (TurnsWindow(pose) ? 0 : 1) : (TurnsWindow(pose) ? 1 : 0);

    /// <summary>Which safe-area set a pose uses (see <see cref="DuoSafeAreas"/>): 0 the rail poses, 1 upright, 2 tent.</summary>
    public static int SafeAreaGroup(DuoPose pose) => pose == DuoPose.Tent ? 2 : IsUpright(pose) ? 1 : 0;

    /// <summary>
    /// Which panel edge (natural orientation: 0 left, 1 top, 2 right, 3 bottom) shows on a given side of the display
    /// (same numbering) at a rotation, as measured on MuMu: at 90 the panel's top shows on the left, at 270 on the right.
    /// </summary>
    public static int PanelEdge(int side, int rotation) => rotation switch
    {
        1 => (side + 1) % 4,  // 90: left <- top, top <- right, right <- bottom, bottom <- left
        2 => (side + 2) % 4,
        3 => (side + 3) % 4,  // 270: right <- top, bottom <- right, left <- bottom, top <- left
        _ => side,
    };

    /// <summary>Display-side insets (left, top, right, bottom) turned into panel-edge waterfall sizes for a rotation.</summary>
    public static (int Left, int Top, int Right, int Bottom) ToPanel((int Left, int Top, int Right, int Bottom) shown, int rotation)
    {
        var panel = new int[4];
        int[] sides = [shown.Left, shown.Top, shown.Right, shown.Bottom];
        for (int side = 0; side < 4; side++) panel[PanelEdge(side, rotation)] = sides[side];
        return (panel[0], panel[1], panel[2], panel[3]);
    }

    /// <summary>
    /// Android re-reads the display cutout (the safe areas) when the display turns on, for the configuration it is in
    /// then; turning it off and on again after a switch between safe-area sets (<see cref="SafeAreaGroup"/>) gives the
    /// new pose its own edges (within a group the set is the same). 0.4 s off is the least that works (0.2 s is cancelled before
    /// the display goes off); the fold animation covers it. No app is relaunched (they pause and resume, as for any
    /// screen off). A display mode change and the overlay-changed broadcast do not make Android re-read it.
    /// </summary>
    public const string RefreshSafeAreas = "input keyevent 223; sleep 0.5; input keyevent 224";

    /// <summary>
    /// One shell line for everything <see cref="DuoDevice"/> holds; <see cref="ParseDevice"/> reads its output.
    /// </summary>
    public const string DeviceQuery =
        "dumpsys window displays | grep -m1 ' cur='; " +
        "echo states:$(cmd device_state print-states-simple 2>/dev/null); " +
        "echo state:$(cmd device_state print-state 2>/dev/null); " +
        "echo postures:$(cmd overlay list android | grep -c 'x] " + PosturesOverlay + "')";

    public static DuoDevice ParseDevice(string output)
    {
        var current = Regex.Match(output, @"\bcur=(\d+)x(\d+)");
        var states = Regex.Match(output, @"^states:([\d,\s]*)$", RegexOptions.Multiline);
        var ids = states.Success ? states.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
        bool foldable = new[] { "0", "1", "2" }.All(ids.Contains);
        var state = Regex.Match(output, @"^state:\s*(\d+)\s*$", RegexOptions.Multiline);
        var postures = Regex.Match(output, @"^postures:\s*(\d+)", RegexOptions.Multiline);
        return new DuoDevice(
            current.Success ? new Size(int.Parse(current.Groups[1].Value), int.Parse(current.Groups[2].Value)) : null,
            foldable,
            foldable && state.Success ? int.Parse(state.Groups[1].Value) : null,
            postures.Success && postures.Groups[1].Value != "0");
    }

    // ---- placement ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Where Android puts a pose on the render surface (surface pixels, as seen). It centres a smaller logical display
    /// with integer halving, and since each pose shares one side with the surface the scale is exactly 1.
    /// </summary>
    public static Rectangle PictureOnSurface(DuoPose pose)
    {
        Size size = ScreenSize(pose), surface = SurfaceFor(pose);
        return new Rectangle((surface.Width - size.Width) / 2, (surface.Height - size.Height) / 2, size.Width, size.Height);
    }

    /// <summary>
    /// The same rectangle in the emulator window's render pixels: MuMu scales the whole surface into its window, so the
    /// picture is a centred sub-rectangle there too. Null when the window is not showing the surface the Duo way round.
    /// </summary>
    public static Rectangle? PictureInRender(DuoPose pose, Size render)
    {
        var surface = SurfaceFor(pose);
        if (render.Width <= 0 || render.Height <= 0 || render.Width < render.Height != surface.Width < surface.Height) return null;
        var p = PictureOnSurface(pose);
        double sx = (double)render.Width / surface.Width, sy = (double)render.Height / surface.Height;
        int x0 = (int)Math.Round(p.Left * sx), x1 = (int)Math.Round(p.Right * sx);
        int y0 = (int)Math.Round(p.Top * sy), y1 = (int)Math.Round(p.Bottom * sy);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>True when an emulator window's picture has the Duo surface's aspect ratio (within 0.5 %).</summary>
    public static bool LooksLikeSurface(Size render) => render.Width > 0 && render.Height > 0 &&
        Math.Abs((double)Math.Max(render.Width, render.Height) / Math.Min(render.Width, render.Height) / ((double)Surface.Width / Surface.Height) - 1) < 0.005;

    private static bool SameSize(Size a, Size b) => a == b || (a.Width == b.Height && a.Height == b.Width);
}
