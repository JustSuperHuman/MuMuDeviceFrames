using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MuMuDeviceFrames;

/// <summary>Where the status bar sits on the emulator's display, in the display's native pixels.</summary>
internal sealed record StatusBarLayout(Size Screen, int StatusBarHeight, Rectangle? Cutout, int Inset)
{
    /// <summary>Time and icons centre on the island / notch when there is one, otherwise on the bar.</summary>
    public float CenterY => Cutout is { } c ? (c.Top + c.Bottom) / 2f : StatusBarHeight / 2f;

    public static StatusBarLayout From(AndroidCutout.Spec spec, AndroidDisplay display)
    {
        var w = display.Native.Width;
        // iOS puts the time about 48 pt from the edge on Dynamic Island phones; keep clear of big corners on others.
        int inset = (int)Math.Round(Math.Max(0.12 * w, spec.CornerRadius * 0.7));
        return new StatusBarLayout(display.Native, spec.StatusBarHeight, spec.Cutout, inset);
    }
}

/// <summary>
/// Draws an iOS-style status bar: bold time on the left, cellular bars, Wi-Fi and battery on the right. Everything is
/// sized in iOS points, so it comes out right on any screen size, and drawn as vector shapes so it stays sharp at
/// native resolution.
/// </summary>
internal static class IosStatusBar
{
    private static readonly string[] FontCandidates = ["SF Pro Display", "SF Pro Text", "SF Pro", "Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI"];
    private static readonly object FontGate = new();
    private static FontFamily? _family;

    public static FontFamily Family(string? configured)
    {
        lock (FontGate)
        {
            if (_family != null) return _family;
            IEnumerable<string> names = configured == null ? FontCandidates : FontCandidates.Prepend(configured);
            foreach (var name in names)
            {
                try { return _family = new FontFamily(name); }
                catch { /* not installed */ }
            }
            return _family = FontFamily.GenericSansSerif;
        }
    }

    public static string ClockText(string setting) => setting switch
    {
        "auto" or "" => DateTime.Now.ToString("h:mm"),
        "24h" => DateTime.Now.ToString("HH:mm"),
        _ => setting,
    };

    /// <summary>
    /// Draws into <paramref name="g"/>. <paramref name="origin"/> is where the display's top-left corner lies on the
    /// target, <paramref name="scale"/> is target pixels per native display pixel.
    /// </summary>
    public static void Draw(Graphics g, PointF origin, float scale, StatusBarLayout layout, bool darkGlyphs, string time, FontFamily family)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var color = darkGlyphs ? Color.Black : Color.White;
        using var brush = new SolidBrush(color);
        using var faint = new SolidBrush(Color.FromArgb(darkGlyphs ? 90 : 100, color));

        float pt = layout.Screen.Width / 402f * scale;   // target px per iOS point (402 pt = iPhone 17 Pro width)
        float cy = origin.Y + layout.CenterY * scale;
        float inset = layout.Inset * scale;
        float left = origin.X + inset, right = origin.X + layout.Screen.Width * scale - inset;

        // Time: 17 pt semibold, left edge at the inset, digits centred on the island.
        using (var path = new GraphicsPath())
        {
            // Families that ship a separate "Semibold" face are already the right weight; plain ones need Bold.
            var style = family.Name.Contains("Semib", StringComparison.OrdinalIgnoreCase) || !family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Regular : FontStyle.Bold;
            path.AddString(time, family, (int)style, 17f * pt, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            using var m = new Matrix();
            m.Translate(left - b.Left, cy - (b.Top + b.Bottom) / 2f);
            path.Transform(m);
            g.FillPath(brush, path);
        }

        // Right cluster, right-aligned: cellular · Wi-Fi · battery (7 pt apart).
        float gap = 7f * pt;
        float x = right;

        // Battery: 25 × 12 pt body with a faint outline, solid fill, and a nub.
        float bw = 25f * pt, bh = 12f * pt, nub = 1.4f * pt;
        x -= bw + nub + 0.6f * pt;
        using (var body = Rounded(x, cy - bh / 2f, bw, bh, 3.6f * pt))
        using (var pen = new Pen(faint, 1.2f * pt))
        {
            g.DrawPath(pen, body);
            float pad = 2.1f * pt;
            using var fill = Rounded(x + pad, cy - bh / 2f + pad, bw - 2 * pad, bh - 2 * pad, 2.2f * pt);
            g.FillPath(brush, fill);
            using var tip = Rounded(x + bw + 0.6f * pt, cy - 2f * pt, nub, 4f * pt, 0.7f * pt);
            g.FillPath(faint, tip);
        }
        x -= gap;

        // Wi-Fi: iOS's fan — a wedge at the bottom and two filled bands above it, each 90° wide, 16 × 11.5 pt.
        float ww = 16.3f * pt;
        x -= ww;
        {
            float cx = x + ww / 2f, tipY = cy + 5.6f * pt;   // fan vertex, just below the centre line
            foreach (var (inner, outer) in new[] { (0f, 3.7f), (5.3f, 8.0f), (9.6f, 12.3f) })
            {
                using var sector = new GraphicsPath();
                float ro = outer * pt, ri = inner * pt;
                sector.AddArc(cx - ro, tipY - ro, 2 * ro, 2 * ro, 225f, 90f);
                if (ri > 0) sector.AddArc(cx - ri, tipY - ri, 2 * ri, 2 * ri, 315f, -90f);
                else sector.AddLine(cx + ro * 0.7071f, tipY - ro * 0.7071f, cx, tipY);
                sector.CloseFigure();
                g.FillPath(brush, sector);
            }
        }
        x -= gap;

        // Cellular: four bars bottom-aligned, snapped to whole target pixels so the short ones stay bars, not dots.
        {
            float barW = MathF.Max(2f, MathF.Round(3.1f * pt)), barGap = MathF.Max(1f, MathF.Round(1.9f * pt));
            float cw = 4 * barW + 3 * barGap;
            x -= cw;
            float bottom = MathF.Round(cy + 5.5f * pt);
            float barLeft = MathF.Round(x);
            float[] heights = [4.6f, 6.8f, 9.0f, 11.2f];
            for (int i = 0; i < 4; i++)
            {
                float h = MathF.Max(barW + 1f, MathF.Round(heights[i] * pt));
                using var bar = Rounded(barLeft + i * (barW + barGap), bottom - h, barW, h, MathF.Min(1.1f * pt, barW / 2.5f));
                g.FillPath(brush, bar);
            }
        }
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Premultiplied BGRA bitmap of just the status-bar band for an emulator picture of the given size.</summary>
    public static (byte[] Pixels, int Width, int Height) RenderBand(int renderWidth, int renderHeight, StatusBarLayout layout, bool darkGlyphs, string time, FontFamily family)
    {
        float scale = (float)renderWidth / layout.Screen.Width;
        int height = Math.Min(renderHeight, (int)Math.Ceiling(Math.Max(layout.StatusBarHeight, layout.CenterY * 2 + 8) * scale));
        var pixels = new byte[renderWidth * height * 4];
        DrawInto(pixels, renderWidth, height, g => Draw(g, PointF.Empty, scale, layout, darkGlyphs, time, family));
        return (pixels, renderWidth, height);
    }

    /// <summary>Runs GDI+ drawing directly on a premultiplied BGRA buffer.</summary>
    public static void DrawInto(byte[] premultiplied, int width, int height, Action<Graphics> draw)
    {
        var handle = GCHandle.Alloc(premultiplied, GCHandleType.Pinned);
        try
        {
            using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, handle.AddrOfPinnedObject());
            using var g = Graphics.FromImage(bmp);
            draw(g);
            g.Flush(FlushIntention.Sync);
        }
        finally
        {
            handle.Free();
        }
    }
}
