using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MuMuDeviceFrames;

/// <summary>Decoded frame + mask at native resolution (straight alpha).</summary>
internal sealed class FrameSource
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public required byte[] Mask { get; init; }
    public required Rectangle Screen { get; init; }
    /// <summary>Optional glass.png (straight-alpha BGRA, frame-sized): drawn over the picture, not part of the bezel.</summary>
    public byte[]? Glass { get; init; }

    /// <summary>Uncached decode; rendering goes through <see cref="FrameRenderer"/>, which keeps recent sources around.</summary>
    public static FrameSource Load(FrameInfo info)
    {
        var (w, h, bgra) = Decode(info.FramePath);
        var (mw, mh, maskBgra) = Decode(info.MaskPath);
        if (mw != w || mh != h) throw new InvalidDataException($"mask.png is {mw}x{mh} but frame.png is {w}x{h} ({info.Key})");

        var mask = new byte[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = maskBgra[i * 4];

        byte[]? glass = null;
        if (File.Exists(info.GlassPath))
        {
            var (gw, gh, glassBgra) = Decode(info.GlassPath);
            if (gw == w && gh == h) glass = glassBgra;
            else Log.Write($"Ignoring glass.png of {info.Key}: {gw}x{gh}, frame is {w}x{h}");
        }
        return new FrameSource { Width = w, Height = h, Bgra = bgra, Mask = mask, Screen = info.Screen, Glass = glass };
    }

    private static (int W, int H, byte[] Bgra) Decode(string path)
    {
        using var bmp = new Bitmap(path);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
            return (bmp.Width, bmp.Height, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Quarter turn. Counter-clockwise puts the top of a portrait phone on the left, like Android's default landscape.</summary>
    public FrameSource Rotate(bool counterClockwise)
    {
        int nw = Height, nh = Width;
        var bgra = new byte[Bgra.Length];
        var mask = new byte[Mask.Length];
        var glass = Glass == null ? null : new byte[Glass.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int nx = counterClockwise ? y : Height - 1 - y;
                int ny = counterClockwise ? Width - 1 - x : x;
                int s = y * Width + x, d = ny * nw + nx;
                mask[d] = Mask[s];
                Buffer.BlockCopy(Bgra, s * 4, bgra, d * 4, 4);
                if (glass != null) Buffer.BlockCopy(Glass!, s * 4, glass, d * 4, 4);
            }
        }
        var screen = counterClockwise
            ? new Rectangle(Screen.Y, Width - Screen.Right, Screen.Height, Screen.Width)
            : new Rectangle(Height - Screen.Bottom, Screen.X, Screen.Height, Screen.Width);
        return new FrameSource { Width = nw, Height = nh, Bgra = bgra, Mask = mask, Screen = screen, Glass = glass };
    }
}

internal readonly record struct RenderKey(string Frame, int RenderWidth, int RenderHeight, int Overlap);

/// <summary>A frame scaled for one emulator picture size.</summary>
internal sealed class RenderedFrame
{
    public required RenderKey Key { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Premultiplied BGRA, top-down.</summary>
    public required byte[] Pixels { get; init; }
    /// <summary>Where the emulator picture's top-left corner sits inside the overlay bitmap.</summary>
    public required Point RenderOffset { get; init; }
    /// <summary>Visible part of the emulator picture as runs (y, x0, x1-exclusive), relative to the picture's top-left.</summary>
    public required (int Y, int X0, int X1)[] ScreenRuns { get; init; }
    public required bool Stretched { get; init; }
    /// <summary>
    /// What the glass draws over the picture (premultiplied BGRA), cropped to <c>Bounds</c> in overlay coordinates. It
    /// goes in a separate click-through window: in the bezel window any visible pixel would swallow taps on the app.
    /// </summary>
    public (byte[] Pixels, Rectangle Bounds)? Glass { get; init; }
}

internal static class FrameRenderer
{
    private const int RegionDilate = 2;

    private static readonly object CacheGate = new();
    private static readonly List<(string Key, FrameSource Source)> SourceCache = [];

    private static FrameSource GetSource(FrameInfo info, bool rotate)
    {
        string key = info.Key + (rotate ? "|rot" : "");
        lock (CacheGate)
        {
            var hit = SourceCache.FindIndex(e => e.Key == key);
            if (hit >= 0) return SourceCache[hit].Source;
        }

        var source = FrameSource.Load(info);
        // Portrait frame on a landscape picture turns counter-clockwise; a landscape frame on a portrait picture turns back.
        if (rotate) source = source.Rotate(counterClockwise: !info.IsLandscape);

        lock (CacheGate)
        {
            SourceCache.Add((key, source));
            while (SourceCache.Count > 4) SourceCache.RemoveAt(0);
        }
        return source;
    }

    private static readonly object RenderGate = new();
    private static readonly List<RenderedFrame> RenderCache = [];

    /// <summary>Emulators of the same size share one rendered bitmap. Renders are serialised; each one is parallel inside.</summary>
    public static RenderedFrame Render(FrameInfo info, RenderKey key)
    {
        lock (RenderGate)
        {
            var hit = RenderCache.Find(r => r.Key == key);
            if (hit != null) return hit;

            var rendered = RenderCore(info, key);
            RenderCache.Add(rendered);
            while (RenderCache.Count > 6) RenderCache.RemoveAt(0);

            // The resampler's scratch buffers are tens of megabytes on the large object heap; hand them back now.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            return rendered;
        }
    }

    public static void ClearCaches()
    {
        lock (RenderGate) RenderCache.Clear();
        lock (CacheGate) SourceCache.Clear();
    }

    private static RenderedFrame RenderCore(FrameInfo info, RenderKey key)
    {
        int rw = key.RenderWidth, rh = key.RenderHeight, overlap = key.Overlap;
        bool rotate = info.IsLandscape != rw > rh;
        var src = GetSource(info, rotate);
        var screen = src.Screen;

        // Where the transparent screen hole lands inside the emulator picture.
        int holeW, holeH, insetX, insetY;
        bool stretched = info.Mismatch(rw, rh) <= FrameLibrary.StretchTolerance;
        if (stretched)
        {
            holeW = rw - 2 * overlap; holeH = rh - 2 * overlap;
            insetX = insetY = overlap;
        }
        else
        {
            // Aspect differs too much to hide: keep the frame's proportions and crop the picture under the bezel.
            double s = Math.Min((rw - 2.0 * overlap) / screen.Width, (rh - 2.0 * overlap) / screen.Height);
            holeW = (int)Math.Round(screen.Width * s); holeH = (int)Math.Round(screen.Height * s);
            insetX = (rw - holeW) / 2; insetY = (rh - holeH) / 2;
        }

        double sx = (double)holeW / screen.Width, sy = (double)holeH / screen.Height;
        int padL = (int)Math.Ceiling(screen.X * sx), padT = (int)Math.Ceiling(screen.Y * sy);
        int padR = (int)Math.Ceiling((src.Width - screen.Right) * sx), padB = (int)Math.Ceiling((src.Height - screen.Bottom) * sy);
        int width = padL + holeW + padR, height = padT + holeH + padB;

        // The hole edges map onto whole destination pixels, so straight bezel edges stay razor sharp.
        var cx = Resampler.BuildContribs(width, src.Width, sx, screen.X, padL);
        var cy = Resampler.BuildContribs(height, src.Height, sy, screen.Y, padT);

        var color = Resampler.Resample(src.Bgra, src.Width, src.Height, 4, premultiply: true, cx, cy);
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            float a = Math.Clamp(color[i * 4 + 3], 0f, 255f);
            // Drop sub-1% alpha: it is filter ringing inside the screen hole, not image content.
            if (a < 3f) continue;
            pixels[i * 4 + 0] = (byte)(Math.Clamp(color[i * 4 + 0], 0f, a) + 0.5f);
            pixels[i * 4 + 1] = (byte)(Math.Clamp(color[i * 4 + 1], 0f, a) + 0.5f);
            pixels[i * 4 + 2] = (byte)(Math.Clamp(color[i * 4 + 2], 0f, a) + 0.5f);
            pixels[i * 4 + 3] = (byte)(a + 0.5f);
        }

        var mask = Resampler.Resample(src.Mask, src.Width, src.Height, 1, premultiply: false, cx, cy);
        var renderOffset = new Point(padL - insetX, padT - insetY);
        var runs = BuildScreenRuns(mask, width, height, renderOffset, rw, rh);
        var glass = src.Glass == null ? null : RenderGlass(src.Glass, src.Width, src.Height, cx, cy, mask, width, height);

        return new RenderedFrame
        {
            Key = key, Width = width, Height = height, Pixels = pixels,
            RenderOffset = renderOffset, ScreenRuns = runs, Stretched = stretched, Glass = glass,
        };
    }

    /// <summary>The glass layer at the overlay's scale, kept to the screen and cropped to what is visible.</summary>
    private static (byte[] Pixels, Rectangle Bounds)? RenderGlass(byte[] glass, int srcW, int srcH, Resampler.Contrib[] cx, Resampler.Contrib[] cy,
        float[] mask, int width, int height)
    {
        var color = Resampler.Resample(glass, srcW, srcH, 4, premultiply: true, cx, cy);
        int left = width, top = height, right = -1, bottom = -1;
        var alpha = new byte[width * height];
        for (int i = 0; i < width * height; i++)
        {
            if (mask[i] <= 127.5f) continue; // filter spill onto the bezel
            float a = Math.Clamp(color[i * 4 + 3], 0f, 255f);
            if (a < 1f) continue;
            alpha[i] = (byte)(a + 0.5f);
            int x = i % width, y = i / width;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (right < 0) return null;

        var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                int i = y * width + x, a = alpha[i];
                if (a == 0) continue;
                int d = ((y - bounds.Top) * bounds.Width + x - bounds.Left) * 4;
                for (int c = 0; c < 3; c++) pixels[d + c] = (byte)(Math.Clamp(color[i * 4 + c], 0f, a) + 0.5f);
                pixels[d + 3] = (byte)a;
            }
        }
        return (pixels, bounds);
    }

    /// <summary>
    /// Screen shape for clipping the emulator window: the mask outline, filled across notches/islands (the frame paints
    /// over those), grown a little so the hard region edge hides under the opaque bezel, and limited to the picture.
    /// </summary>
    private static (int Y, int X0, int X1)[] BuildScreenRuns(float[] mask, int width, int height, Point renderOffset, int rw, int rh)
    {
        var min = new int[height];
        var max = new int[height];
        for (int y = 0; y < height; y++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x] <= 127.5f) continue;
                if (x < lo) lo = x;
                hi = x;
            }
            min[y] = lo; max[y] = hi;
        }

        var runs = new List<(int, int, int)>(rh);
        for (int y = 0; y < height; y++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            for (int dy = -RegionDilate; dy <= RegionDilate; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= height || min[yy] == int.MaxValue) continue;
                int dx = (int)Math.Sqrt(RegionDilate * RegionDilate - dy * dy);
                lo = Math.Min(lo, min[yy] - dx);
                hi = Math.Max(hi, max[yy] + dx);
            }
            if (lo == int.MaxValue) continue;

            int ry = y - renderOffset.Y;
            if (ry < 0 || ry >= rh) continue;
            int x0 = Math.Max(lo - renderOffset.X, 0), x1 = Math.Min(hi + 1 - renderOffset.X, rw);
            if (x1 > x0) runs.Add((ry, x0, x1));
        }
        return runs.ToArray();
    }
}

/// <summary>Separable Lanczos-3 resampler working on premultiplied alpha, with sub-pixel exact placement.</summary>
internal static class Resampler
{
    internal readonly record struct Contrib(int Start, float[] Weights);

    /// <summary>Maps destination pixels to weighted source pixels for: dst = (src - srcOrigin) * scale + dstOrigin.</summary>
    public static Contrib[] BuildContribs(int dstLength, int srcLength, double scale, double srcOrigin, double dstOrigin)
    {
        double filterScale = Math.Max(1.0, 1.0 / scale);
        double support = 3.0 * filterScale;
        var result = new Contrib[dstLength];
        var weights = new List<double>();

        for (int i = 0; i < dstLength; i++)
        {
            double center = (i + 0.5 - dstOrigin) / scale + srcOrigin;
            int j0 = (int)Math.Floor(center - support), j1 = (int)Math.Ceiling(center + support);

            weights.Clear();
            double total = 0;
            for (int j = j0; j <= j1; j++)
            {
                double w = Lanczos3((j + 0.5 - center) / filterScale);
                weights.Add(w);
                total += w;
            }

            // Taps outside the image count as transparent, so normalise by the full kernel and drop them.
            int first = Math.Max(j0, 0), last = Math.Min(j1, srcLength - 1);
            if (last < first || total == 0)
            {
                result[i] = new Contrib(0, []);
                continue;
            }
            var kept = new float[last - first + 1];
            for (int j = first; j <= last; j++) kept[j - first] = (float)(weights[j - j0] / total);
            result[i] = new Contrib(first, kept);
        }
        return result;
    }

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1.0;
        if (x >= 3.0) return 0.0;
        double px = Math.PI * x;
        return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
    }

    public static float[] Resample(byte[] src, int srcW, int srcH, int channels, bool premultiply, Contrib[] cx, Contrib[] cy)
    {
        int dstW = cx.Length, dstH = cy.Length;
        int rowLen = dstW * channels;

        // Horizontal pass, only over the source rows the vertical pass will read.
        int firstRow = srcH, lastRow = -1;
        foreach (var c in cy)
        {
            if (c.Weights.Length == 0) continue;
            firstRow = Math.Min(firstRow, c.Start);
            lastRow = Math.Max(lastRow, c.Start + c.Weights.Length - 1);
        }
        var dst = new float[dstW * dstH * channels];
        if (lastRow < firstRow) return dst;

        var tmp = new float[(lastRow - firstRow + 1) * rowLen];
        Parallel.For(firstRow, lastRow + 1, () => new float[srcW * channels], (y, _, line) =>
        {
            int srcRow = y * srcW * channels;
            if (premultiply)
            {
                for (int x = 0; x < srcW; x++)
                {
                    int p = srcRow + x * 4;
                    float a = src[p + 3];
                    float k = a / 255f;
                    line[x * 4] = src[p] * k; line[x * 4 + 1] = src[p + 1] * k; line[x * 4 + 2] = src[p + 2] * k; line[x * 4 + 3] = a;
                }
            }
            else
            {
                for (int i = 0; i < srcW * channels; i++) line[i] = src[srcRow + i];
            }

            int outRow = (y - firstRow) * rowLen;
            for (int x = 0; x < dstW; x++)
            {
                var c = cx[x];
                var w = c.Weights;
                for (int ch = 0; ch < channels; ch++)
                {
                    float sum = 0;
                    int p = c.Start * channels + ch;
                    for (int t = 0; t < w.Length; t++, p += channels) sum += w[t] * line[p];
                    tmp[outRow + x * channels + ch] = sum;
                }
            }
            return line;
        }, _ => { });

        // Vertical pass.
        Parallel.For(0, dstH, y =>
        {
            var c = cy[y];
            var outSpan = dst.AsSpan(y * rowLen, rowLen);
            for (int t = 0; t < c.Weights.Length; t++)
            {
                float w = c.Weights[t];
                var inSpan = tmp.AsSpan((c.Start + t - firstRow) * rowLen, rowLen);
                for (int i = 0; i < rowLen; i++) outSpan[i] += w * inSpan[i];
            }
        });
        return dst;
    }
}
