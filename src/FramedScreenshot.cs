using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MuMuDeviceFrames;

/// <summary>
/// A screenshot taken inside Android, placed into the frame at the frame's native resolution. Nothing is captured from
/// the desktop, so it is pixel-perfect no matter what covers the emulator window.
/// </summary>
internal static class FramedScreenshot
{
    /// <summary>iOS-style status bar to draw over the screenshot (when the emulator's own is hidden).</summary>
    public sealed record StatusBar(StatusBarLayout Layout, bool DarkGlyphs, string Time, FontFamily Font);

    /// <param name="background">"transparent" (default), "checker" or a #RRGGBB colour.</param>
    public static string? Save(MuMuInstance instance, FrameInfo frame, string outPath, string? background = null, StatusBar? statusBar = null)
    {
        var png = instance.Screencap();
        if (png == null) return "adb screencap failed";
        return Compose(frame, png, outPath, background, statusBar);
    }

    public static string? Compose(FrameInfo frame, byte[] screenshotPng, string outPath, string? background, StatusBar? statusBar = null)
    {
        var source = FrameSource.Load(frame);
        var screen = source.Screen;
        int width = source.Width, height = source.Height;

        // Screenshot scaled to fill the screen hole (centre-cropped when the aspect differs), rotated if needed.
        using var shot = new Bitmap(new MemoryStream(screenshotPng));
        using var fitted = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fitted))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            if (shot.Width > shot.Height != screen.Width > screen.Height) shot.RotateFlip(RotateFlipType.Rotate270FlipNone);
            double scale = Math.Max((double)screen.Width / shot.Width, (double)screen.Height / shot.Height);
            int w = (int)Math.Round(shot.Width * scale), h = (int)Math.Round(shot.Height * scale);
            g.DrawImage(shot, (screen.Width - w) / 2, (screen.Height - h) / 2, w, h);
        }

        var canvas = new byte[width * height * 4];
        bool transparent = background == null || string.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase);
        Color? solid = background is { Length: 7 } && background[0] == '#' ? ColorTranslator.FromHtml(background) : null;
        if (!transparent)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 4;
                    if (solid is { } c) { canvas[p] = c.B; canvas[p + 1] = c.G; canvas[p + 2] = c.R; }
                    else canvas[p] = canvas[p + 1] = canvas[p + 2] = ((x / 32 + y / 32) & 1) == 0 ? (byte)0xF2 : (byte)0xD4;
                    canvas[p + 3] = 255;
                }
            }
        }

        var data = fitted.LockBits(new Rectangle(0, 0, screen.Width, screen.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < screen.Height; y++)
        {
            for (int x = 0; x < screen.Width; x++)
            {
                int cx = screen.X + x, cy = screen.Y + y;
                if (source.Mask[cy * width + cx] < 128) continue;
                int s = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                int p = (cy * width + cx) * 4;
                canvas[p] = (byte)s; canvas[p + 1] = (byte)(s >> 8); canvas[p + 2] = (byte)(s >> 16); canvas[p + 3] = 255;
            }
        }
        fitted.UnlockBits(data);

        if (statusBar != null)
        {
            float scale = (float)screen.Width / statusBar.Layout.Screen.Width;
            IosStatusBar.DrawInto(canvas, width, height, g =>
                IosStatusBar.Draw(g, new PointF(screen.X, screen.Y), scale, statusBar.Layout, statusBar.DarkGlyphs, statusBar.Time, statusBar.Font));
        }

        // Frame over the top (premultiplied source-over).
        for (int i = 0; i < width * height; i++)
        {
            int a = source.Bgra[i * 4 + 3];
            if (a == 0) continue;
            int p = i * 4;
            for (int c = 0; c < 3; c++) canvas[p + c] = (byte)(source.Bgra[p + c] * a / 255 + canvas[p + c] * (255 - a) / 255);
            canvas[p + 3] = (byte)(a + canvas[p + 3] * (255 - a) / 255);
        }

        Diagnostics.SavePremultiplied(canvas, width, height, outPath);
        return null;
    }
}
