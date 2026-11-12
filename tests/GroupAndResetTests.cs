using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using MuMuDeviceFrames;

internal static class GroupAndResetTests
{
    public static void RenderReleaseShowcase(string heroPath, string outPath)
    {
        using var hero = new Bitmap(heroPath);
        // Middle phone + its soft drop shadow in docs/hero.png (2281x1480), cleanly between the side phones.
        var phoneSrc = new Rectangle(808, 72, 665, 1336);
        float bezelInsetX = 28f, bezelInsetTop = 36f, bezelW = 609f, bezelH = 1268f;

        float phoneScale = 0.73f;
        int drawW = (int)Math.Round(phoneSrc.Width * phoneScale);
        int drawH = (int)Math.Round(phoneSrc.Height * phoneScale);
        int phoneBezelW = (int)Math.Round(bezelW * phoneScale);
        int phoneBezelH = (int)Math.Round(bezelH * phoneScale);

        const float uiScale = 1.6f;
        var tabs = new[]
        {
            new EmulatorTab((IntPtr)1, "JG-1", TabDeviceKind.Phone, null, Active: true),
            new EmulatorTab((IntPtr)2, "JG-2", TabDeviceKind.Phone, null, Active: false),
            new EmulatorTab((IntPtr)3, "JG-3", TabDeviceKind.Phone, null, Active: false),
            new EmulatorTab((IntPtr)4, "JG-Duo", TabDeviceKind.Duo, DuoPose.Open, Active: false),
        };
        var tabRender = EmulatorTabsPanel.Preview(tabs, uiScale, hover: 1);
        var sideRender = DeviceMotionPanel.Preview(uiScale, strength: 3, hoverButton: 1);

        int canvasW = 1320, canvasH = 1160;
        int totalGroupW = phoneBezelW + (int)Math.Round(18 * uiScale) + sideRender.Size.Width;
        int bezelLeft = Math.Max(110, (canvasW - totalGroupW) / 2 + 16);
        int bezelTop = 148;
        int drawX = bezelLeft - (int)Math.Round(bezelInsetX * phoneScale);
        int drawY = bezelTop - (int)Math.Round(bezelInsetTop * phoneScale);

        int tabX = bezelLeft + (phoneBezelW - tabRender.Size.Width) / 2;
        int tabY = bezelTop - (int)Math.Round(16 * uiScale) - tabRender.Size.Height;
        int sideX = bezelLeft + phoneBezelW + (int)Math.Round(18 * uiScale);
        int sideY = bezelTop + (phoneBezelH - sideRender.Size.Height) / 2;

        Color topBg = hero.GetPixel(8, phoneSrc.Top);
        Color botBg = hero.GetPixel(8, phoneSrc.Bottom - 1);

        using var canvas = new Bitmap(canvasW, canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, canvasW, canvasH),
                Color.FromArgb(255, 31, 35, 48),
                Color.FromArgb(255, 20, 23, 33),
                90f))
            {
                g.FillRectangle(bg, 0, 0, canvasW, canvasH);
            }

            // Soft drop shadow + exact titanium bezel silhouette clip so zero hero.png backdrop rectangle shows.
            using (var shadowPath = new System.Drawing.Drawing2D.GraphicsPath())
            {
                var sRect = new RectangleF(bezelLeft - 14, bezelTop - 6, phoneBezelW + 28, phoneBezelH + 28);
                float sr = phoneBezelW * 0.31f;
                shadowPath.AddArc(sRect.X, sRect.Y, sr, sr, 180, 90);
                shadowPath.AddArc(sRect.Right - sr, sRect.Y, sr, sr, 270, 90);
                shadowPath.AddArc(sRect.Right - sr, sRect.Bottom - sr, sr, sr, 0, 90);
                shadowPath.AddArc(sRect.X, sRect.Bottom - sr, sr, sr, 90, 90);
                shadowPath.CloseFigure();
                using var pgb = new System.Drawing.Drawing2D.PathGradientBrush(shadowPath)
                {
                    CenterColor = Color.FromArgb(125, 0, 0, 0),
                    SurroundColors = [Color.FromArgb(0, 0, 0, 0)],
                    FocusScales = new PointF(0.86f, 0.92f),
                };
                g.FillPath(pgb, shadowPath);
            }

            using (var bezelClip = new System.Drawing.Drawing2D.GraphicsPath())
            {
                var bRect = new RectangleF(bezelLeft + 1.5f, bezelTop + 1.5f, phoneBezelW - 3f, phoneBezelH - 3f);
                float br = phoneBezelW * 0.295f;
                bezelClip.AddArc(bRect.X, bRect.Y, br, br, 180, 90);
                bezelClip.AddArc(bRect.Right - br, bRect.Y, br, br, 270, 90);
                bezelClip.AddArc(bRect.Right - br, bRect.Bottom - br, br, br, 0, 90);
                bezelClip.AddArc(bRect.X, bRect.Bottom - br, br, br, 90, 90);
                bezelClip.CloseFigure();
                var prev = g.Clip;
                g.SetClip(bezelClip);
                g.DrawImage(hero, new Rectangle(drawX, drawY, drawW, drawH), phoneSrc, GraphicsUnit.Pixel);
                g.Clip = prev;
            }

            DrawPremulWithShadow(g, tabRender.Pixels, tabRender.Size, new Point(tabX, tabY), tabRender.Size.Height / 2f);
            DrawPremul(g, sideRender.Pixels, sideRender.Size, new Point(sideX, sideY));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        canvas.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void DrawPremulWithShadow(Graphics g, byte[] premul, Size size, Point at, float radius)
    {
        using (var shadowPath = new System.Drawing.Drawing2D.GraphicsPath())
        {
            var r = new RectangleF(at.X - 8, at.Y - 4, size.Width + 16, size.Height + 20);
            float d = Math.Min(r.Height, radius * 2 + 12);
            shadowPath.AddArc(r.X, r.Y, d, d, 180, 90);
            shadowPath.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            shadowPath.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            shadowPath.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            shadowPath.CloseFigure();
            using var pgb = new System.Drawing.Drawing2D.PathGradientBrush(shadowPath)
            {
                CenterColor = Color.FromArgb(95, 0, 0, 0),
                SurroundColors = [Color.FromArgb(0, 0, 0, 0)],
                FocusScales = new PointF(0.88f, 0.65f),
            };
            g.FillPath(pgb, shadowPath);
        }
        DrawPremul(g, premul, size, at);
    }

    private static void DrawPremul(Graphics g, byte[] premul, Size size, Point at)
    {
        using var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(Point.Empty, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        for (int y = 0; y < size.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(premul, y * size.Width * 4, data.Scan0 + y * data.Stride, size.Width * 4);
        bmp.UnlockBits(data);
        g.DrawImageUnscaled(bmp, at.X, at.Y);
    }

    public static void RenderPreviews(string folder)
    {
        Directory.CreateDirectory(folder);
        var tabs = new[]
        {
            new EmulatorTab((IntPtr)1, "JG-1", TabDeviceKind.Phone, null, Active: true),
            new EmulatorTab((IntPtr)2, "JG-2", TabDeviceKind.Phone, null, Active: false),
            new EmulatorTab((IntPtr)3, "JG-3", TabDeviceKind.PhoneLandscape, null, Active: false),
            new EmulatorTab((IntPtr)4, "JG-Duo", TabDeviceKind.Duo, DuoPose.Open, Active: false),
        };
        foreach (float scale in new[] { 1.0f, 1.5f })
        {
            var tabPreview = EmulatorTabsPanel.Preview(tabs, scale, hover: 1);
            SaveBgra(tabPreview.Pixels, tabPreview.Size, Path.Combine(folder, $"tabs-{scale:0.0}.png"));
            SaveOverGreen(tabPreview.Pixels, tabPreview.Size, Path.Combine(folder, $"tabs-green-{scale:0.0}.png"));

            var shakeHover = DeviceMotionPanel.Preview(scale, strength: 3, hoverButton: 0);
            SaveBgra(shakeHover.Pixels, shakeHover.Size, Path.Combine(folder, $"side-shake-{scale:0.0}.png"));

            var resetHover = DeviceMotionPanel.Preview(scale, strength: 0, hoverButton: 1);
            SaveBgra(resetHover.Pixels, resetHover.Size, Path.Combine(folder, $"side-reset-{scale:0.0}.png"));
        }
    }

    private static void SaveBgra(byte[] pixels, Size size, string path)
    {
        using var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(Point.Empty, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        for (int y = 0; y < size.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(pixels, y * size.Width * 4, data.Scan0 + y * data.Stride, size.Width * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void SaveOverGreen(byte[] premul, Size size, string path)
    {
        int pad = 16;
        using var bmp = new Bitmap(size.Width + pad * 2, size.Height + pad * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 0, 255, 1));
            using var fg = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var data = fg.LockBits(new Rectangle(Point.Empty, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            for (int y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(premul, y * size.Width * 4, data.Scan0 + y * data.Stride, size.Width * 4);
            fg.UnlockBits(data);
            g.DrawImageUnscaled(fg, pad, pad);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static void Run(Action<bool, string> check)
    {
        // 1. Config defaults: grouping is on by default on fresh installs and when loading older config.json files.
        var fresh = new AppConfig();
        check(fresh.GroupEmulators, "GroupEmulators is enabled by default on a fresh config");

        var legacyJson = """{ "Enabled": true, "RecordingPadding": 15 }""";
        var loadedLegacy = JsonSerializer.Deserialize<AppConfig>(legacyJson)!;
        check(loadedLegacy.GroupEmulators, "Older config.json without GroupEmulators defaults to grouped mode on");

        var disabledJson = """{ "GroupEmulators": false }""";
        var loadedDisabled = JsonSerializer.Deserialize<AppConfig>(disabledJson)!;
        check(!loadedDisabled.GroupEmulators, "Explicit GroupEmulators=false in config.json is respected");

        // 2. Win32 empty-region group hiding: completely hidden, click-through, and cleanly reversible.
        using (var dummy = new DummyPopupWindow())
        {
            check(!EmulatorClip.IsCompletelyHidden(dummy.Handle), "Unclipped window is not marked completely hidden");
            check(EmulatorClip.HideCompletely(dummy.Handle), "EmulatorClip.HideCompletely succeeds on a live HWND");
            check(EmulatorClip.IsCompletelyHidden(dummy.Handle), "EmulatorClip.IsCompletelyHidden detects empty region");
            var centre = new Native.POINT { X = 250, Y = 250 };
            check(Native.WindowFromPoint(centre) != dummy.Handle, "Empty-region grouped window is 100% click-through to WindowFromPoint");
            EmulatorClip.Remove(dummy.Handle);
            check(!EmulatorClip.IsCompletelyHidden(dummy.Handle), "EmulatorClip.Remove restores normal window region state");
        }

        // 3. Top tab bar layout, ShareX recording clearance, and chroma-safe capsule rendering.
        var sampleTabs = new[]
        {
            new EmulatorTab((IntPtr)1, "JG-1", TabDeviceKind.Phone, null, Active: true),
            new EmulatorTab((IntPtr)2, "JG-2", TabDeviceKind.Phone, null, Active: false),
            new EmulatorTab((IntPtr)3, "JG-Duo", TabDeviceKind.Duo, DuoPose.Open, Active: false),
        };
        foreach (float scale in new[] { 1.0f, 1.5f })
        {
            var (pillSize, boxes) = EmulatorTabsPanel.LayoutTabs(sampleTabs, scale, maxWidth: 1200);
            check(boxes.Length == 3 && boxes[0].Left >= 4 && boxes[^1].Right <= pillSize.Width - 4
                  && boxes[1].Left > boxes[0].Right && boxes[2].Left > boxes[1].Right,
                $"Tab bar layout places 3 tabs cleanly at {scale:0.0}x DPI ({pillSize})");

            var preview = EmulatorTabsPanel.Preview(sampleTabs, scale, hover: 1);
            check(preview.Pixels[3] == 0 && preview.Pixels[(preview.Size.Width - 1) * 4 + 3] == 0,
                $"Tab bar capsule corners are 100% transparent at {scale:0.0}x DPI");
            int midY = preview.Size.Height / 2;
            int activeMidX = (int)(boxes[0].Left + boxes[0].Width / 2);
            check(preview.Pixels[(midY * preview.Size.Width + activeMidX) * 4 + 3] >= 220,
                $"Tab bar interior renders smoked glass at {scale:0.0}x DPI");

            foreach (int recPad in new[] { 0, 15, 30 })
            {
                var rawWork = new Rectangle(0, 0, 5120, 1392);
                int reserve = EmulatorTabsPanel.ReservedHeight(scale, recPad);
                var effectiveWork = new Rectangle(rawWork.Left, rawWork.Top + reserve, rawWork.Width, rawWork.Height - reserve);
                var placedDevice = DeviceAnchor.Place(new Size(620, 1310), new Point(2560, 700), effectiveWork);
                var tabOrigin = EmulatorTabsPanel.Place(placedDevice, placedDevice.Location, pillSize, rawWork, scale, recPad);
                int tabBottom = tabOrigin.Y + pillSize.Height;
                int shareXRecordingTop = placedDevice.Top - recPad;
                check(tabOrigin.Y >= rawWork.Top + 4 && tabBottom < shareXRecordingTop,
                    $"Tab bar ({tabOrigin.Y}..{tabBottom}) stays on screen and above ShareX recording top ({shareXRecordingTop}) at {scale:0.0}x, pad {recPad}px");
            }
        }

        var manyTabs = Enumerable.Range(1, 12)
            .Select(i => new EmulatorTab((IntPtr)i, $"Emulator-Instance-{i}", TabDeviceKind.Phone, null, i == 1))
            .ToArray();
        var (compressedSize, compressedBoxes) = EmulatorTabsPanel.LayoutTabs(manyTabs, 1.0f, maxWidth: 760);
        check(compressedSize.Width <= 760 && compressedBoxes.All(b => b.Width >= 48),
            $"12 tabs compress cleanly into a 760px width budget ({compressedSize.Width}px)");

        // 4. Side controls panel: Shake + Reset stacked cards, transparent gap between cards, and accurate hit-testing.
        var sidePreview = DeviceMotionPanel.Preview(1.0f, strength: 3, hoverButton: 1);
        check(sidePreview.Size.Height == 142, $"Side control panel includes both Shake and Reset cards ({sidePreview.Size})");
        int gapPixelAlpha = sidePreview.Pixels[(76 * sidePreview.Size.Width + 30) * 4 + 3];
        check(gapPixelAlpha == 0, "Gap between Shake and Reset cards is 100% transparent");
        check(DeviceMotionPanel.HitTestPreview(1.0f, new Point(30, 35)) == 0
              && DeviceMotionPanel.HitTestPreview(1.0f, new Point(30, 76)) == -1
              && DeviceMotionPanel.HitTestPreview(1.0f, new Point(30, 110)) == 1,
            "Side control HitTest distinguishes Shake (0), transparent gap (-1), and Reset (1)");

        // 5. DeviceReset pure logic: orientation, panel restart checks, window de-letterboxing/scaling, and anchor healing.
        check(DeviceReset.TargetRotation(frameIsLandscape: false, new Size(1206, 2622)) == 0, "Portrait frame on portrait panel locks rotation 0");
        check(DeviceReset.TargetRotation(frameIsLandscape: false, new Size(2622, 1206)) == 1, "Portrait frame on landscape panel locks rotation 1");
        check(DeviceReset.TargetRotation(frameIsLandscape: true, new Size(1640, 2360)) == 1, "Landscape frame on MuMu's turned portrait panel locks rotation 1");
        check(DeviceReset.TargetRotation(frameIsLandscape: true, new Size(2360, 1640)) == 0, "Landscape frame on landscape panel locks rotation 0");

        var iphone17Pro = new DeviceSpec(new Size(1206, 2622), 480, true);
        check(!DeviceReset.NeedsPanelRestart(iphone17Pro, new AndroidDisplay(new Size(1206, 2622), 480), frameIsLandscape: false),
            "Matching portrait panel needs no restart");
        check(DeviceReset.NeedsPanelRestart(iphone17Pro, new AndroidDisplay(new Size(2160, 3840), 540), frameIsLandscape: false),
            "Mismatched physical panel triggers restart");
        check(DeviceReset.NeedsPanelRestart(iphone17Pro, new AndroidDisplay(new Size(2622, 1206), 480), frameIsLandscape: false),
            "Swapped physical panel on a portrait frame triggers restart so cutouts measure 1:1");

        var ipadLandscape = new DeviceSpec(new Size(2360, 1640), 320, true);
        check(!DeviceReset.NeedsPanelRestart(ipadLandscape, new AndroidDisplay(new Size(1640, 2360), 320), frameIsLandscape: true),
            "Landscape frame accepts MuMu's turned portrait panel without restarting");

        var work = new Rectangle(0, 65, 5120, 1327);
        check(DeviceReset.IdealWindowBounds(new Rectangle(1000, 120, 568, 1255), new Rectangle(1004, 156, 560, 1216),
            new Size(620, 1280), new Size(1206, 2622), work) == null,
            "Healthy window bounds are left untouched by IdealWindowBounds");

        var unletterboxed = DeviceReset.IdealWindowBounds(
            new Rectangle(800, 200, 1017, 812),
            new Rectangle(1015, 240, 586, 769),
            new Size(640, 840),
            new Size(1206, 2622),
            work);
        check(unletterboxed is { } fixedBox && fixedBox.Width < fixedBox.Height && fixedBox.Width < 700,
            $"Letterboxed portrait picture in landscape window is resized to upright portrait ({unletterboxed})");

        var unshrunk = DeviceReset.IdealWindowBounds(
            new Rectangle(1200, 400, 208, 455),
            new Rectangle(1204, 436, 200, 416),
            new Size(228, 448),
            new Size(1206, 2622),
            work);
        check(unshrunk is { } grown && grown.Height >= 950 && grown.Height <= work.Height,
            $"Tiny shrunk window is restored to a comfortable desktop scale ({unshrunk})");

        var healedAnchor = DeviceReset.ClampAnchor(new Size(620, 1300), new Point(5035, 63), work);
        var placedAfterHeal = DeviceAnchor.Place(new Size(620, 1300), healedAnchor, work);
        check(placedAfterHeal.Right <= work.Right && placedAfterHeal.Top >= work.Top && healedAnchor == DeviceAnchor.Centre(placedAfterHeal),
            $"Corner-jammed anchor (5035, 63) heals cleanly inside work area ({healedAnchor})");
    }

    private sealed class DummyPopupWindow : NativeWindow, IDisposable
    {
        public DummyPopupWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "MuMuDeviceFramesGroupTest",
                Style = unchecked((int)(Native.WS_POPUP | 0x10000000u)), // WS_POPUP | WS_VISIBLE
                ExStyle = unchecked((int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)),
                X = 200, Y = 200, Width = 100, Height = 100,
            });
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
