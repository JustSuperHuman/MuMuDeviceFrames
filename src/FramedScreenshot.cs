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
        using var image = Compose(frame, png, background, statusBar);
        image.Save(outPath, ImageFormat.Png);
        return null;
    }

    /// <summary>
    /// Puts the framed screenshot on the clipboard. Call on the UI (STA) thread with the bytes of <see cref="Png"/>.
    /// Apps that keep transparency (Figma, Photoshop, browsers, Slack, Discord) read the "PNG" format; the plain bitmap
    /// next to it is for everything else, on white because CF_DIB has no alpha and would turn the corners black.
    /// </summary>
    public static void CopyToClipboard(byte[] png)
    {
        using var image = new Bitmap(new MemoryStream(png));
        using var flat = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.DrawImageUnscaled(image, 0, 0);
        }
        var data = new DataObject();
        data.SetData("PNG", false, new MemoryStream(png));
        data.SetData(DataFormats.Bitmap, true, flat);
        Clipboard.SetDataObject(data, true, 5, 100);
    }

    /// <summary>The framed screenshot as PNG bytes (transparent around the phone); null with an error if the capture failed.</summary>
    public static byte[]? Png(MuMuInstance instance, FrameInfo frame, StatusBar? statusBar, out string? error)
    {
        error = null;
        var png = instance.Screencap();
        if (png == null) { error = "adb screencap failed"; return null; }
        using var image = Compose(frame, png, null, statusBar);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static Bitmap Compose(FrameInfo frame, byte[] screenshotPng, string? background, StatusBar? statusBar = null)
    {
        using var shot = new Bitmap(new MemoryStream(screenshotPng));
        // iPhone Duo: the screenshot's size says which display was on, so it always lands in the matching frame.
        if (IPhoneDuo.IsDuo(frame) && IPhoneDuo.PoseForScreen(shot.Size) is { } pose && IPhoneDuo.PoseOf(frame) != pose)
            frame = DuoFrames.Frame(AppConfig.BuiltInFramesRoot, pose);

        var source = FrameSource.Load(frame);
        var screen = source.Screen;
        int width = source.Width, height = source.Height;

        // Screenshot scaled to fill the screen hole (centre-cropped when the aspect differs), rotated if needed.
        using var fitted = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fitted))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            if (shot.Width > shot.Height != screen.Width > screen.Height) shot.RotateFlip(RotateFlipType.Rotate270FlipNone);
            double scale = Math.Max((double)screen.Width / shot.Width, (double)screen.Height / shot.Height);
            int w = (int)Math.Round(shot.Width * scale), h = (int)Math.Round(shot.Height * scale);
            if (w == shot.Width && h == shot.Height)
            {
                // Native resolution: copy the pixels as they are rather than resampling at scale 1.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
            }
            g.DrawImage(shot, new Rectangle((screen.Width - w) / 2, (screen.Height - h) / 2, w, h), new Rectangle(0, 0, shot.Width, shot.Height), GraphicsUnit.Pixel);
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

        // What the glass adds over the picture (the open iPhone Duo's crease), straight alpha.
        if (source.Glass is { } glass)
        {
            for (int i = 0; i < width * height; i++)
            {
                int a = glass[i * 4 + 3], p = i * 4;
                if (a == 0 || canvas[p + 3] != 255) continue; // only over the picture
                for (int c = 0; c < 3; c++) canvas[p + c] = (byte)((glass[p + c] * a + canvas[p + c] * (255 - a) + 127) / 255);
            }
        }

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

        return Diagnostics.ToBitmap(canvas, width, height);
    }
}
