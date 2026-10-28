using System.Drawing.Drawing2D;

namespace MuMuDeviceFrames;

/// <summary>What iOS draws around an app on the iPhone Duo besides the home indicator: the status element (time and ring)
/// down the trailing rail (closed, with the camera; open, book), or across the top-trailing corner (open portrait,
/// tabletop). iPhone frames have their own status bar window.</summary>
internal enum ChromeKind { None, DuoRailClosed, DuoRailOpen, DuoCorner, DuoRailTent }

/// <summary>One state of that chrome: what, the home indicator or not, on which screen (native pixels), at what
/// density, light or dark, the time.</summary>
internal sealed record ChromeState(ChromeKind Kind, bool Home, Size Screen, int Density, bool DarkGlyphs, string Time, bool DarkHome = false);

/// <summary>
/// Draws the iOS chrome the tool owns, in iOS points (vector shapes, sharp at any size). The iPhone Duo's status is one
/// element, Apple's "circular, flexible system that nestles into the corner of the screen": the time (semibold) and a
/// ring, with the battery level as an arc over the top, Wi-Fi inside, and the cellular dots along its open bottom. No
/// iPhone status bar in any pose. Every position and size below is measured on Apple's own renders (HIG, "Designing
/// for iPhone Duo"; 466 / 890 / 626 pt display widths) and checked against them by overlay.
/// </summary>
internal static class IosChrome
{
    /// <summary>One placement of the status element: points from the screen's top-right corner (x leftwards).</summary>
    /// <param name="Scale">The ring's size against the closed rail's (Apple draws the open display's 7 % smaller).</param>
    /// <param name="TimeScale">The time's (on the open display only about 4 % smaller).</param>
    /// <param name="TimeGap">Upright only: the time ends this far before the ring (pt), growing leftwards, so a wider time
    /// ("12:59") never comes closer to the ring than Apple's "9:41" does; 0 centres the time on TimeX.</param>
    /// <param name="FromBottom">Y measured up from the bottom edge (tent: the camera is at the bottom).</param>
    private sealed record Element(float TimeX, float TimeY, float RingX, float RingY, float Scale, float TimeScale, float TimeGap = 0, bool FromBottom = false);

    // Closed (mail-compact.png at 1.0944 px/pt): camera 37.5 pt at 48 / 48; digits 11 pt tall centred at 48.9 / 91.8;
    // ring 39.3 pt across centred at 48.0 / 130.0.
    private static readonly Element Closed = new(48.9f, 91.8f, 48.0f, 130.0f, 1f, 1f);
    // Tent (Apple's card shows the outer display landscape with the camera bottom right and the controls up the trailing
    // edge; the status itself is cropped out there): the closed rail's element and camera, the same distances from the
    // camera, going up from the bottom, so the controls stay aligned with the hardware as the HIG has it.
    private static readonly Element Tent = new(48.9f, 91.8f, 48.0f, 130.0f, 1f, 1f, 0, FromBottom: true);
    private const float CameraDiameter = 37.5f, CameraX = 48.0f, CameraY = 48.0f;
    // Open and book (pane-controls.png at 1.2045 px/pt): digits centred at 45.7 / 47.7, ring 36.5 pt at 44.8 / 81.4.
    private static readonly Element Open = new(45.7f, 47.7f, 44.8f, 81.4f, 36.5f / 39.3f, 0.96f);
    // Open portrait and tabletop (the overview card, de-rotated 10.0 deg, 0.5176 px/pt): one row 44 pt down, the ring
    // 42.5 pt in, "9:41" ending 8.5 pt before the ring's outer edge (69.5 pt in against the ring's 61.0).
    private static readonly Element Corner = new(0, 44.0f, 42.5f, 44.0f, 36.5f / 39.3f, 0.96f, 8.5f);

    /// <summary>How far the portrait corner element reaches down (its lowest dot); the upright poses' top inset (the
    /// rail's 84 pt, the same panel edge) keeps app content below it.</summary>
    public static float CornerBottom => Corner.RingY + (RingDots + DotDiameter / 2) * Corner.Scale;

    /// <summary>The trailing safe area of the rail, as Apple's renders show it (Mail's content ends 84 pt in).</summary>
    public const int RailPt = 84;

    /// <summary>The upright poses' top safe area: the corner element ends 63.4 pt down, content starts 4.6 pt below.</summary>
    public const int UprightTopPt = 68;

    /// <summary>iOS's home indicator safe area.</summary>
    public const int HomeIndicatorPt = 34;

    /// <summary>Where the home indicator sits (native pixels), for its light or dark: what is under it decides.</summary>
    public static Rectangle HomeArea(ChromeState state)
    {
        float px = state.Density / 160f;
        int w = (int)(150 * px), h = (int)(24 * px);
        return new Rectangle(state.Screen.Width / 2 - w / 2, state.Screen.Height - h, w, h);
    }

    /// <summary>
    /// Where the time and ring sit, in the screen's native pixels: the content behind them decides light or dark
    /// glyphs, as on iOS. Null when there is nothing to draw.
    /// </summary>
    private static Element? ElementFor(ChromeKind kind) => kind switch
    {
        ChromeKind.DuoRailClosed => Closed,
        ChromeKind.DuoRailTent => Tent,
        ChromeKind.DuoRailOpen => Open,
        ChromeKind.DuoCorner => Corner,
        _ => null,
    };

    public static Rectangle? GlyphArea(ChromeState state)
    {
        var element = ElementFor(state.Kind);
        if (element == null) return null;
        float px = state.Density / 160f, ring = (RingDots + DotDiameter) * element.Scale;
        // Rail: the column from the time's top to the ring's dots; corner: the row from the time's leading edge.
        float left = element.TimeGap > 0 ? element.RingX + ring + element.TimeGap + 40 : Math.Max(element.TimeX, element.RingX) + ring;
        float right = element.RingX - ring, top = Math.Min(element.TimeY - 12, element.RingY - ring), bottom = element.RingY + ring;
        if (element.FromBottom) (top, bottom) = (state.Screen.Height / px - bottom, state.Screen.Height / px - top);
        return Rectangle.FromLTRB((int)(state.Screen.Width - left * px), (int)(top * px), (int)(state.Screen.Width - right * px), (int)(bottom * px));
    }

    /// <summary>True when the content in a 32-bit ARGB bitmap's <paramref name="area"/> is light (dark glyphs on it).</summary>
    public static bool IsLight(Bitmap picture, Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, picture.Width, picture.Height));
        if (area.Width <= 0 || area.Height <= 0) return false;
        var data = picture.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var lums = new List<int>();
            for (int y = 0; y < area.Height; y += 2)
                for (int x = 0; x < area.Width; x += 2)
                {
                    int c = System.Runtime.InteropServices.Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                    lums.Add((((c >> 16) & 255) * 299 + ((c >> 8) & 255) * 587 + (c & 255) * 114) / 1000);
                }
            lums.Sort();
            return lums.Count > 0 && lums[lums.Count / 2] > 140; // the median: the app's background, not its text
        }
        finally
        {
            picture.UnlockBits(data);
        }
    }

    /// <summary>The Duo's chrome for a pose (none for other frames).</summary>
    public static ChromeKind KindFor(FrameInfo? frame, DuoPose? pose) => !IPhoneDuo.IsDuo(frame) ? ChromeKind.None : pose switch
    {
        DuoPose.Folded => ChromeKind.DuoRailClosed,
        DuoPose.Tent => ChromeKind.DuoRailTent,
        DuoPose.Open or DuoPose.HalfOpen => ChromeKind.DuoRailOpen,
        DuoPose.OpenPortrait or DuoPose.Tabletop => ChromeKind.DuoCorner,
        _ => ChromeKind.None,
    };

    /// <summary>
    /// The home indicator goes with the bottom safe area that keeps apps clear of it (drawn without one it sits on top
    /// of tab bars): on the iPhone Duo once its safe-area overlay (duo-v6) is in, on Face ID iPhones once theirs has the
    /// home-indicator safe area.
    /// </summary>
    public static bool HomeIndicator(FrameInfo frame, string? duoOverlay, string? iphoneOverlay) => IPhoneDuo.IsDuo(frame)
        ? duoOverlay?.StartsWith("duo-v6", StringComparison.Ordinal) == true
        : AndroidCutout.HasHomeIndicator(frame) && iphoneOverlay?.EndsWith(AndroidCutout.HomeMark, StringComparison.Ordinal) == true;

    /// <summary>Draws onto <paramref name="g"/>: <paramref name="origin"/> is the screen's top-left on the target,
    /// <paramref name="scale"/> target pixels per native pixel.</summary>
    public static void Draw(Graphics g, PointF origin, float scale, ChromeState state, FontFamily family)
    {
        if (state.Kind == ChromeKind.None && !state.Home) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float pt = state.Density / 160f * scale; // target pixels per iOS point
        var screen = new RectangleF(origin.X, origin.Y, state.Screen.Width * scale, state.Screen.Height * scale);
        var ink = state.DarkGlyphs ? Color.Black : Color.White;
        if (state.Home)
        {
            // 134 x 5 pt, 8 pt above the bottom edge of the display as it is held, black on light apps, white on dark.
            float w = 134 * pt, h = 5 * pt;
            using var path = Pill(new RectangleF(screen.Left + screen.Width / 2 - w / 2, screen.Bottom - (8 + 5) * pt, w, h));
            using var brush = new SolidBrush(state.DarkHome ? Color.Black : Color.White);
            g.FillPath(brush, path);
        }
        var element = ElementFor(state.Kind);
        if (element == null) return;
        PointF At(float fromRight, float y) => new(screen.Right - fromRight * pt, element.FromBottom ? screen.Bottom - y * pt : screen.Top + y * pt);
        if (state.Kind is ChromeKind.DuoRailClosed or ChromeKind.DuoRailTent)
        {
            // The outer camera: a black hole in the screen, whatever the app shows.
            using var camera = new SolidBrush(Color.Black);
            var c = At(CameraX, CameraY);
            float d = CameraDiameter * pt;
            g.FillEllipse(camera, c.X - d / 2, c.Y - d / 2, d, d);
        }
        if (element.TimeGap > 0)
        {
            float trailing = element.RingX + (RingRadius + RingStroke / 2) * element.Scale + element.TimeGap;
            DrawTime(g, At(trailing, element.TimeY), DigitHeight * element.TimeScale * pt, state.Time, family, ink, trailingEdge: true);
        }
        else DrawTime(g, At(element.TimeX, element.TimeY), DigitHeight * element.TimeScale * pt, state.Time, family, ink);
        DrawRing(g, At(element.RingX, element.RingY), element.Scale * pt, ink);
    }

    // The time's digits are 11 pt tall on the closed rail and "9:41" is 30.2 pt wide (15 pt semibold SF). Drawn from
    // whatever font is installed, bold unless SF itself is there, sized by that height, widened or narrowed to SF's
    // proportions (measured on "9:41", so other times keep the face's own spacing), and centred where Apple centres it.
    private const float DigitHeight = 11.0f, NineFortyOneWidth = 30.2f;

    /// <param name="trailingEdge">The time ends at <paramref name="centre"/>.X instead of being centred on it.</param>
    private static void DrawTime(Graphics g, PointF centre, float digitHeight, string time, FontFamily family, Color ink, bool trailingEdge = false)
    {
        bool sf = family.Name.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase);
        var style = sf && family.IsStyleAvailable(FontStyle.Bold) || !sf ? FontStyle.Bold : FontStyle.Regular;
        if (!sf && !family.IsStyleAvailable(FontStyle.Bold)) style = FontStyle.Regular;
        using var reference = new GraphicsPath();
        reference.AddString("9:41", family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var r = reference.GetBounds();
        float stretch = NineFortyOneWidth / DigitHeight / (r.Width / r.Height);
        using var path = new GraphicsPath();
        path.AddString(time, family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        float k = digitHeight / b.Height;
        using var m = new Matrix();
        m.Translate(centre.X, centre.Y);
        m.Scale(k * stretch, k);
        m.Translate(-(trailingEdge ? b.Right : b.Left + b.Width / 2), -(b.Top + b.Height / 2));
        path.Transform(m);
        using var brush = new SolidBrush(ink);
        g.FillPath(brush, path);
    }

    // The ring, closed-rail size (pt), measured on mail-compact.png: the arc's centre line 18.3 pt out, 3 pt wide, from
    // 148.5 deg round over the top to 31.5 deg (y down); four 3.9 pt dots 18.9 pt out at 60, 80, 100 and 120 deg; Wi-Fi
    // inside as two 2 pt arcs (12.2 and 7.6 pt out, 76 deg wide) about a point 6.6 pt below the
    // ring's centre; the Wi-Fi's base is the fan's tip, a 3.2 pt wedge pointing down at the arcs' centre.
    private const float RingRadius = 18.3f, RingStroke = 3.0f, ArcStart = 148.5f, ArcSweep = 243f;
    private const float RingDots = 18.9f, DotDiameter = 3.9f;
    private static readonly float[] DotAngles = [60, 80, 100, 120];
    private const float WifiDrop = 6.6f, WifiOuter = 12.2f, WifiInner = 7.6f, WifiStroke = 2.0f, WifiTip = 3.2f, WifiSpan = 76f;

    private static void DrawRing(Graphics g, PointF c, float u, Color ink)
    {
        using (var arc = new Pen(ink, RingStroke * u) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            g.DrawArc(arc, c.X - RingRadius * u, c.Y - RingRadius * u, 2 * RingRadius * u, 2 * RingRadius * u, ArcStart, ArcSweep);
        using var fill = new SolidBrush(ink);
        foreach (float a in DotAngles)
        {
            double rad = a * Math.PI / 180;
            float x = c.X + RingDots * u * (float)Math.Cos(rad), y = c.Y + RingDots * u * (float)Math.Sin(rad);
            g.FillEllipse(fill, x - DotDiameter * u / 2, y - DotDiameter * u / 2, DotDiameter * u, DotDiameter * u);
        }
        var o = new PointF(c.X, c.Y + WifiDrop * u);
        using (var wifi = new Pen(ink, WifiStroke * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(wifi, o.X - WifiOuter * u, o.Y - WifiOuter * u, 2 * WifiOuter * u, 2 * WifiOuter * u, 270 - WifiSpan / 2, WifiSpan);
            g.DrawArc(wifi, o.X - WifiInner * u, o.Y - WifiInner * u, 2 * WifiInner * u, 2 * WifiInner * u, 270 - WifiSpan / 2, WifiSpan);
        }
        using var tip = new GraphicsPath();
        tip.AddPie(o.X - WifiTip * u, o.Y - WifiTip * u, 2 * WifiTip * u, 2 * WifiTip * u, 270 - WifiSpan / 2, WifiSpan);
        g.FillPath(fill, tip);
        using (var soften = new Pen(ink, 0.5f * u) { LineJoin = LineJoin.Round })
            g.DrawPath(soften, tip); // the wedge's corners are rounded in SF's glyph
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        float d = r.Height;
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    /// <summary>The chrome for an emulator picture of the given size, as a premultiplied BGRA bitmap the size of the picture.</summary>
    public static byte[] Render(Size picture, ChromeState state, FontFamily family)
    {
        var pixels = new byte[picture.Width * picture.Height * 4];
        float scale = (float)picture.Width / state.Screen.Width;
        IosStatusBar.DrawInto(pixels, picture.Width, picture.Height, g => Draw(g, PointF.Empty, scale, state, family));
        return pixels;
    }
}
