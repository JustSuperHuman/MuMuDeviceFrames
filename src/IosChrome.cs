using System.Drawing.Drawing2D;

namespace MuMuDeviceFrames;

/// <summary>What iOS draws around an app on the iPhone Duo besides the home indicator: the rail (closed, open, book) or a
/// plain status bar across the top (open portrait, tabletop). iPhone frames have their own status bar window.</summary>
internal enum ChromeKind { None, DuoRailClosed, DuoRailOpen, DuoBar }

/// <summary>One state of that chrome: what, the home indicator or not, on which screen (native pixels), at what
/// density, light or dark, the time.</summary>
internal sealed record ChromeState(ChromeKind Kind, bool Home, Size Screen, int Density, bool DarkGlyphs, string Time);

/// <summary>
/// Draws the iOS chrome the tool owns, in iOS points (vector shapes, sharp at any size). The Duo rail copies the
/// geometry the JustGains app used to draw (so nothing moves now that the tool draws it): an 84 pt rail on the
/// trailing edge, the outer camera (34 pt, 25 pt from the top) when closed, the time (15 pt semibold) below it or
/// 26 pt from the top when open, and a 30 pt status ring (battery arc around Wi-Fi) 5 pt under the time. The rail has
/// no background: the app paints its own under it, as on iOS.
/// </summary>
internal static class IosChrome
{
    public const float RailWidth = 84;

    /// <summary>
    /// The home indicator goes with the bottom safe area that keeps apps clear of it; drawn without one it sits on top of
    /// tab bars. Android does not report that inset yet (a display cutout cannot carry it: Android letterboxes apps that
    /// are not edge to edge away from it), so no frame gets the indicator until it does.
    /// </summary>
    public static bool HomeIndicator(FrameInfo frame) => false;

    /// <summary>The Duo's chrome for a pose (none for other frames).</summary>
    public static ChromeKind KindFor(FrameInfo? frame, DuoPose? pose) => !IPhoneDuo.IsDuo(frame) ? ChromeKind.None : pose switch
    {
        DuoPose.Folded => ChromeKind.DuoRailClosed,
        DuoPose.Open or DuoPose.HalfOpen => ChromeKind.DuoRailOpen,
        DuoPose.OpenPortrait or DuoPose.Tabletop => ChromeKind.DuoBar,
        _ => ChromeKind.None,
    };

    /// <summary>
    /// The Duo's open display upright: an iPad-like 24 pt bar at three quarters of an iPhone's glyph size, its items
    /// inset 28 pt so the screen's 44 pt corner arc never cuts them (6 pt down, where the glyphs start, the arc is
    /// 22 pt in).
    /// </summary>
    public static StatusBarLayout DuoBarLayout(Size screen, int density)
    {
        float px = density / 160f;
        return new StatusBarLayout(screen, (int)Math.Round(24 * px), null, (int)Math.Round(28 * px), px, 0.75f);
    }

    /// <summary>Draws onto <paramref name="g"/>: <paramref name="origin"/> is the screen's top-left on the target,
    /// <paramref name="scale"/> target pixels per native pixel.</summary>
    public static void Draw(Graphics g, PointF origin, float scale, ChromeState state, FontFamily family)
    {
        if (state.Kind == ChromeKind.None && !state.Home) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float pt = state.Density / 160f * scale; // target pixels per iOS point
        var screen = new RectangleF(origin.X, origin.Y, state.Screen.Width * scale, state.Screen.Height * scale);
        var ink = state.DarkGlyphs ? Color.FromArgb(255, 26, 26, 26) : Color.White;
        if (state.Home)
        {
            // 134 x 5 pt, 8 pt above the bottom edge of the display as it is held, black on light apps, white on dark.
            float w = 134 * pt, h = 5 * pt;
            using var path = Pill(new RectangleF(screen.Left + screen.Width / 2 - w / 2, screen.Bottom - (8 + 5) * pt, w, h));
            using var brush = new SolidBrush(state.DarkGlyphs ? Color.FromArgb(255, 0, 0, 0) : Color.White);
            g.FillPath(brush, path);
        }
        switch (state.Kind)
        {
            case ChromeKind.DuoRailClosed:
            case ChromeKind.DuoRailOpen:
            {
                bool closed = state.Kind == ChromeKind.DuoRailClosed;
                float cx = screen.Right - RailWidth / 2 * pt, y = screen.Top;
                float timeTop = y + 26 * pt;
                if (closed)
                {
                    using var camera = new SolidBrush(Color.Black);
                    g.FillEllipse(camera, cx - 17 * pt, y + 25 * pt, 34 * pt, 34 * pt);
                    timeTop = y + (25 + 34 + 12) * pt;
                }
                using var font = new Font(family, 15 * pt, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(ink);
                var size = g.MeasureString(state.Time, font, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(state.Time, font, text, cx - size.Width / 2, timeTop, StringFormat.GenericTypographic);
                float lineHeight = 18 * pt; // React Native's line for 15 pt text, which the app's rail used
                DrawStatusRing(g, new PointF(cx - 15 * pt, timeTop + lineHeight + 5 * pt), pt, ink);
                break;
            }
            case ChromeKind.DuoBar:
            {
                // The inner display upright has no island: iPad-like, time at the leading edge, signal, Wi-Fi and battery
                // at the trailing edge, in a 24 pt bar (Android's status bar height, which apps already keep clear).
                IosStatusBar.Draw(g, origin, scale, DuoBarLayout(state.Screen, state.Density), state.DarkGlyphs, state.Time, family);
                break;
            }
        }
    }

    /// <summary>The 30 pt ring: a dotted track, the battery arc (78 %) from the top, and Wi-Fi inside.</summary>
    private static void DrawStatusRing(Graphics g, PointF topLeft, float pt, Color ink)
    {
        float cx = topLeft.X + 15 * pt, cy = topLeft.Y + 15 * pt, r = 13 * pt;
        var box = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        using (var dots = new SolidBrush(Color.FromArgb(ink.A * 28 / 100, ink)))
        {
            // dasharray 0.1 / 3.3 with round caps: a dot of the stroke's width every 3.4 pt round the circle.
            int count = (int)Math.Round(2 * Math.PI * 13 / 3.4);
            for (int i = 0; i < count; i++)
            {
                double a = -Math.PI / 2 + i * 2 * Math.PI / count;
                float x = cx + r * (float)Math.Cos(a), y = cy + r * (float)Math.Sin(a);
                g.FillEllipse(dots, x - pt, y - pt, 2 * pt, 2 * pt);
            }
        }
        using (var arc = new Pen(ink, 2.2f * pt) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, box, -90, 360 * 0.78f);
        using var wifi = new Pen(ink, 1.9f * pt) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // The app's SVG: arcs radius 8.2 and 5 about (15, 19), and a dot at (15, 18.6).
        float ox = topLeft.X, oy = topLeft.Y;
        g.DrawArc(wifi, ox + (15 - 8.2f) * pt, oy + (19.0f - 8.2f) * pt, 16.4f * pt, 16.4f * pt, 225, 90);
        g.DrawArc(wifi, ox + (15 - 5f) * pt, oy + (19.07f - 5f) * pt, 10f * pt, 10f * pt, 224, 92);
        using var dot = new SolidBrush(ink);
        g.FillEllipse(dot, ox + (15 - 1.5f) * pt, oy + (18.6f - 1.5f) * pt, 3 * pt, 3 * pt);
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
