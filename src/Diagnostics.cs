using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

internal static class Diagnostics
{
    /// <summary>Renders a frame for an arbitrary picture size over a checkerboard, with the clip shape filled in grey.</summary>
    public static int RenderOnly(string frameKey, int renderWidth, int renderHeight, string outPath)
    {
        try
        {
            var config = AppConfig.Load();
            var frame = FrameLibrary.Load(config.FramesRoot).Find(frameKey);
            if (frame == null) { Log.Write($"Render: unknown frame '{frameKey}'"); return 2; }
            var rendered = FrameRenderer.Render(frame, new RenderKey(frame.Key, renderWidth, renderHeight, Math.Clamp(config.Overlap, 0, 8)));

            using var output = new Bitmap(rendered.Width, rendered.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(output))
            {
                g.Clear(Color.FromArgb(0, 255, 1));
                using var picture = new SolidBrush(Color.FromArgb(120, 120, 130));
                foreach (var (y, x0, x1) in rendered.ScreenRuns)
                    g.FillRectangle(picture, rendered.RenderOffset.X + x0, rendered.RenderOffset.Y + y, x1 - x0, 1);
            }
            var data = output.LockBits(new Rectangle(0, 0, rendered.Width, rendered.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var row = new byte[rendered.Width * 4];
            for (int y = 0; y < rendered.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < rendered.Width; x++)
                {
                    int s = (y * rendered.Width + x) * 4, a = rendered.Pixels[s + 3];
                    for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)(rendered.Pixels[s + c] + row[x * 4 + c] * (255 - a) / 255);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            output.UnlockBits(data);
            output.Save(outPath, ImageFormat.Png);
            Log.Write($"Render {frame.Key} for {renderWidth}x{renderHeight}: overlay {rendered.Width}x{rendered.Height}, offset {rendered.RenderOffset}, {(rendered.Stretched ? "exact fit" : "cropped fit")} -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Render failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Reproduces what the desktop compositor shows (emulator window clipped to the screen shape, frame blended on top),
    /// from the window's own pixels, so alignment can be checked even while the emulator is covered by other windows.
    /// </summary>
    public static int Snapshot(string title, string outPath, string? frameKey, string? background = null)
    {
        try
        {
            var config = AppConfig.Load();
            IntPtr main = FindEmulator(title);
            if (main == IntPtr.Zero) { Log.Write($"Snapshot: no emulator titled '{title}'"); return 2; }

            IntPtr render = FindChildByClass(main, EmulatorSession.RenderClass);
            GetWindowRect(main, out var wr);
            GetWindowRect(render, out var rr);

            var library = FrameLibrary.Load(config.FramesRoot);
            var renderSize = new Size(rr.Width, rr.Height);
            var chosen = library.Find(frameKey) ?? library.Find(config.Instance(title).Frame);
            // iPhone Duo: Android's pose decides the frame and which part of the surface is the picture.
            DuoPose? pose = IPhoneDuo.IsDuo(chosen) || IPhoneDuo.LooksLikeSurface(renderSize) ? QueryPose(title) : null;
            if (pose is { } posed && (chosen == null || IPhoneDuo.IsDuo(chosen))) chosen = library.Find(IPhoneDuo.FrameKey(posed)) ?? chosen;
            var frame = chosen ?? library.Find(config.DefaultFrame) ?? library.BestMatch(rr.Width, rr.Height);
            if (frame == null) { Log.Write("Snapshot: no frame available"); return 3; }
            var picture = new Rectangle(0, 0, rr.Width, rr.Height);
            if (IPhoneDuo.IsDuo(frame) && pose is { } shown && IPhoneDuo.PictureInRender(shown, renderSize) is { } part) picture = part;

            var sw = Stopwatch.StartNew();
            var rendered = FrameRenderer.Render(frame, new RenderKey(frame.Key, picture.Width, picture.Height, Math.Clamp(config.Overlap, 0, 8)));
            long renderMs = sw.ElapsedMilliseconds;

            using var window = PrintEmulator(main, wr);

            // iPhone Duo: the pose buttons beside the device, as the tray app shows them.
            (byte[] Pixels, Size Size)? panel = null;
            int panelX = 0, panelY = 0;
            if (IPhoneDuo.IsDuo(frame) && pose != null && config.DuoPoseControls)
            {
                uint dpi = GetDpiForWindow(main);
                float scale = dpi == 0 ? 1f : dpi / 96f;
                var display = MuMuControl.LocateByName(title) is { } at ? MuMuControl.QueryDisplay(at) : null;
                panel = DuoPosePanel.Preview(IPhoneDuo.Detect(display) ?? pose, display?.Duo?.Foldable == true, scale, -1, p => $"Ctrl+Alt+{Array.IndexOf(IPhoneDuo.Poses, p) + 1}");
                double sx = (double)picture.Width / frame.Screen.Width;
                panelX = rendered.RenderOffset.X + picture.Width + (int)Math.Round(DuoFrames.Border * sx) + (int)Math.Round(DuoPosePanel.LogicalOffset * scale);
                panelY = rendered.RenderOffset.Y + picture.Height / 2 - (int)Math.Round(DuoPosePanel.LogicalHeight * scale) / 2;
            }

            // Premultiplied BGRA canvas. "transparent" keeps the alpha, "checker" is the default, otherwise a hex colour.
            const int margin = 24;
            int width = Math.Max(rendered.Width, panel is { } pp ? panelX + (int)Math.Round(DuoPosePanel.PillWidthLogical * (pp.Size.Height / DuoPosePanel.LogicalHeight)) : 0) + 2 * margin,
                height = rendered.Height + 2 * margin;
            var canvas = new byte[width * height * 4];
            bool transparent = string.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase);
            Color? solid = background is { Length: 7 } && background[0] == '#' ? ColorTranslator.FromHtml(background) : null;
            if (!transparent)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int p = (y * width + x) * 4;
                        if (solid is { } c) { canvas[p] = c.B; canvas[p + 1] = c.G; canvas[p + 2] = c.R; }
                        else canvas[p] = canvas[p + 1] = canvas[p + 2] = ((x / 16 + y / 16) & 1) == 0 ? (byte)0xF2 : (byte)0xD4;
                        canvas[p + 3] = 255;
                    }
                }
            }

            // Emulator pixels, only where the clip region lets them through.
            var data = window.LockBits(new Rectangle(0, 0, wr.Width, wr.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int offX = rr.Left - wr.Left + picture.X, offY = rr.Top - wr.Top + picture.Y;
            foreach (var (ry, x0, x1) in rendered.ScreenRuns)
            {
                for (int rx = x0; rx < x1; rx++)
                {
                    int sx = rx + offX, sy = ry + offY;
                    int dx = margin + rendered.RenderOffset.X + rx, dy = margin + rendered.RenderOffset.Y + ry;
                    int s = Marshal.ReadInt32(data.Scan0, sy * data.Stride + sx * 4);
                    int p = (dy * width + dx) * 4;
                    canvas[p] = (byte)s; canvas[p + 1] = (byte)(s >> 8); canvas[p + 2] = (byte)(s >> 16); canvas[p + 3] = 255;
                }
            }
            window.UnlockBits(data);

            // Glass layer over the picture (its own click-through window on the desktop).
            if (rendered.Glass is { } glass)
            {
                for (int y = 0; y < glass.Bounds.Height; y++)
                {
                    for (int x = 0; x < glass.Bounds.Width; x++)
                    {
                        int s = (y * glass.Bounds.Width + x) * 4, a = glass.Pixels[s + 3];
                        if (a == 0) continue;
                        int p2 = ((margin + glass.Bounds.Y + y) * width + margin + glass.Bounds.X + x) * 4;
                        for (int c = 0; c < 4; c++) canvas[p2 + c] = (byte)(glass.Pixels[s + c] + canvas[p2 + c] * (255 - a) / 255);
                    }
                }
            }

            // Frame on top (premultiplied source-over).
            for (int y = 0; y < rendered.Height; y++)
            {
                for (int x = 0; x < rendered.Width; x++)
                {
                    int s = (y * rendered.Width + x) * 4;
                    int a = rendered.Pixels[s + 3];
                    if (a == 0) continue;
                    int p = ((y + margin) * width + x + margin) * 4;
                    for (int c = 0; c < 4; c++)
                        canvas[p + c] = (byte)(rendered.Pixels[s + c] + canvas[p + c] * (255 - a) / 255);
                }
            }

            if (panel is { } drawn)
            {
                for (int y = 0; y < drawn.Size.Height; y++)
                {
                    for (int x = 0; x < drawn.Size.Width; x++)
                    {
                        int cx = margin + panelX + x, cy = margin + panelY + y;
                        if (cx < 0 || cy < 0 || cx >= width || cy >= height) continue;
                        int s = (y * drawn.Size.Width + x) * 4, a = drawn.Pixels[s + 3];
                        if (a == 0) continue;
                        int p = (cy * width + cx) * 4;
                        for (int c = 0; c < 4; c++) canvas[p + c] = (byte)(drawn.Pixels[s + c] + canvas[p + c] * (255 - a) / 255);
                    }
                }
            }

            SavePremultiplied(canvas, width, height, outPath);

            Log.Write($"Snapshot '{title}' with {frame.Key}: surface {rr.Width}x{rr.Height}, picture {picture}, overlay {rendered.Width}x{rendered.Height}, " +
                      $"offset {rendered.RenderOffset}, {(rendered.Stretched ? "exact fit" : "cropped fit")}, render {renderMs} ms -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Snapshot failed: {ex}");
            return 1;
        }
    }

    /// <summary>The emulator window exactly as MuMu draws it (title bar, toolbar and all), for before/after pictures.</summary>
    public static int WindowShot(string title, string outPath)
    {
        try
        {
            IntPtr main = FindEmulator(title);
            if (main == IntPtr.Zero) { Log.Write($"WindowShot: no emulator titled '{title}'"); return 2; }
            GetWindowRect(main, out var wr);
            using var window = PrintEmulator(main, wr);
            window.Save(outPath, ImageFormat.Png);
            Log.Write($"WindowShot '{title}': {wr.Width}x{wr.Height} -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"WindowShot failed: {ex}");
            return 1;
        }
    }

    private static IntPtr FindEmulator(string title)
    {
        IntPtr main = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowText(hwnd) != title || FindChildByClass(hwnd, EmulatorSession.RenderClass) == IntPtr.Zero) return true;
            main = hwnd;
            return false;
        }, IntPtr.Zero);
        return main;
    }

    internal static Bitmap PrintEmulator(IntPtr main, RECT wr)
    {
        var window = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(window);
        IntPtr dc = g.GetHdc();
        PrintWindow(main, dc, PW_RENDERFULLCONTENT);
        g.ReleaseHdc(dc);
        return window;
    }

    /// <summary>Screenshot from inside Android composited into the frame at native resolution (see <see cref="FramedScreenshot"/>).</summary>
    public static int Screenshot(string title, string outPath, string? frameKey, string? background)
    {
        try
        {
            var config = AppConfig.Load();
            var instance = MuMuControl.LocateByName(title);
            if (instance == null) { Log.Write($"Screenshot: no running MuMu instance named '{title}'"); return 2; }
            var display = MuMuControl.QueryDisplay(instance);
            var library = FrameLibrary.Load(config.FramesRoot);
            var chosen = library.Find(frameKey) ?? library.Find(config.Instance(title).Frame);
            if (IPhoneDuo.Detect(display) is { } pose && (chosen == null || IPhoneDuo.IsDuo(chosen))) chosen = library.Find(IPhoneDuo.FrameKey(pose)) ?? chosen;
            var frame = chosen ?? library.Find(config.DefaultFrame)
                        ?? (display == null ? null : library.BestMatch(display.Native.Width, display.Native.Height));
            if (frame == null) { Log.Write("Screenshot: no frame available"); return 3; }

            // With the iOS-style bar on, Android's own is empty; draw ours the way the overlay does. Never on the
            // iPhone Duo, whose app draws its own rail.
            FramedScreenshot.StatusBar? bar = null;
            if (!IPhoneDuo.IsDuo(frame) && config.IosStatusBarFor(title, frame) && display != null && AndroidStatusBar.IsHidden(instance)
                && AndroidCutout.Measure(frame, display, config.StatusBarPaddingFactor, ios: true) is { } spec)
                bar = new FramedScreenshot.StatusBar(StatusBarLayout.From(spec, display), AndroidStatusBar.WantsDarkGlyphs(instance),
                    IosStatusBar.ClockText(config.StatusBarClock), IosStatusBar.Family(config.StatusBarFont));

            // The rest of the iOS chrome, as the tray app draws it.
            ChromeState? chrome = null;
            if (display != null && config.IosStatusBarFor(title, frame))
            {
                bool duo = IPhoneDuo.IsDuo(frame);
                var shownPose = IPhoneDuo.Detect(display);
                bool home = IosChrome.HomeIndicator(frame) && (!duo || shownPose != null);
                var kind = IosChrome.KindFor(frame, shownPose);
                if (kind != ChromeKind.None || home)
                    chrome = new ChromeState(kind, home, duo && shownPose is { } shown ? IPhoneDuo.ScreenSize(shown) : display.Native, display.DensityOverride ?? display.Density,
                        AndroidStatusBar.WantsDarkGlyphs(instance), IosStatusBar.ClockText(config.StatusBarClock));
            }
            var error = FramedScreenshot.Save(instance, frame, outPath, background, bar, chrome);
            Log.Write(error == null ? $"Screenshot '{title}' in {frame.Key} -> {outPath}" : $"Screenshot '{title}' failed: {error}");
            return error == null ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write($"Screenshot failed: {ex}");
            return 1;
        }
    }

    /// <summary>The iPhone Duo pose of a running emulator, asked over adb; null when it is not in one.</summary>
    private static DuoPose? QueryPose(string title)
    {
        var instance = MuMuControl.LocateByName(title);
        return instance == null ? null : IPhoneDuo.Detect(MuMuControl.QueryDisplay(instance));
    }

    internal static void SavePremultiplied(byte[] canvas, int width, int height, string outPath)
    {
        using var output = ToBitmap(canvas, width, height);
        output.Save(outPath, ImageFormat.Png);
    }

    /// <summary>A straight-alpha bitmap of a premultiplied BGRA canvas (which is un-premultiplied in place).</summary>
    internal static Bitmap ToBitmap(byte[] canvas, int width, int height)
    {
        // PNG wants straight alpha.
        for (int i = 0; i < width * height; i++)
        {
            int a = canvas[i * 4 + 3];
            if (a is 0 or 255) continue;
            for (int c = 0; c < 3; c++) canvas[i * 4 + c] = (byte)Math.Min(255, canvas[i * 4 + c] * 255 / a);
        }
        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var outData = output.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++) Marshal.Copy(canvas, y * width * 4, outData.Scan0 + y * outData.Stride, width * 4);
        output.UnlockBits(outData);
        return output;
    }
}
