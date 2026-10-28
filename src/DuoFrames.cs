using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MuMuDeviceFrames;

/// <summary>
/// The two iPhone Duo frames (closed and open). device-frames-media has no Duo, so they are drawn here, in the same
/// folder format (frame.png, mask.png, template.json), on first run and again whenever <see cref="Version"/> changes.
///
/// Everything is drawn per pixel from signed distances to rounded rectangles, which gives exact anti-aliased edges and
/// makes the closed phone's asymmetric corners trivial. Sizes are 3x pixels (1 pt = 3 px). Outside in: a titanium rim
/// with a rounded, lit profile, a dark gasket, then the black glass around the screen. Closed, a hinge barrel shows on
/// the left edge. Open, a faint crease runs down the middle of the screen; it lives in glass.png, which is drawn over
/// the picture (never under the mouse) rather than in frame.png.
/// </summary>
internal static class DuoFrames
{
    public const string Version = "MuMuDeviceFrames iPhone Duo frames v1";

    private const int Glass = 24, Gasket = 3, Rim = 12;     // black border, dark seal, titanium band (13 pt in all)
    private const int Border = Glass + Gasket + Rim;
    private const int HingeOut = 30, HingeIn = 60, HingeInset = 21; // barrel: proud of the body, tucked under it, short of the ends
    private const int Pad = 48;                              // transparent margin around the body
    private const int CreaseWidth = 132;                     // 44 pt band, almost invisible head-on

    private sealed record Layout(DuoPose Pose, Rectangle Screen, Size FrameSize, float RadiusLeft, float RadiusRight);

    private static Layout For(DuoPose pose)
    {
        var size = IPhoneDuo.ScreenSize(pose);
        if (pose == DuoPose.Folded)
        {
            int left = Pad + HingeOut + Border;
            var screen = new Rectangle(left, Pad + Border, size.Width, size.Height);
            return new Layout(pose, screen, new Size(screen.Right + Border + Pad, screen.Bottom + Border + Pad), 60, 180);
        }
        var inner = new Rectangle(Pad + Border, Pad + Border, size.Width, size.Height);
        return new Layout(pose, inner, new Size(inner.Right + Border + Pad, inner.Bottom + Border + Pad), 132, 132);
    }

    public static string Dir(string root, DuoPose pose) => Path.Combine(root, IPhoneDuo.Category, IPhoneDuo.Model, IPhoneDuo.Variant(pose));

    /// <summary>The frame entries, as <see cref="FrameLibrary"/> lists them.</summary>
    public static FrameInfo Frame(string root, DuoPose pose)
    {
        var layout = For(pose);
        return new FrameInfo(IPhoneDuo.Category, IPhoneDuo.Model, IPhoneDuo.Variant(pose), Dir(root, pose), layout.Screen, layout.FrameSize);
    }

    /// <summary>Draws both frames into <paramref name="root"/> unless this version is already there. Returns the ones available.</summary>
    public static List<FrameInfo> Ensure(string root)
    {
        var frames = new List<FrameInfo>();
        foreach (var pose in new[] { DuoPose.Folded, DuoPose.Open })
        {
            string dir = Dir(root, pose);
            try
            {
                if (!IsCurrent(dir))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // Draw next to the target and swap it in, so a second process never sees half a frame.
                    string temp = dir + ".tmp-" + Environment.ProcessId;
                    Write(pose, temp);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    Directory.Move(temp, dir);
                    Log.Write($"Drew {IPhoneDuo.FrameKey(pose)} in {sw.ElapsedMilliseconds} ms -> {dir}");
                }
                frames.Add(Frame(root, pose));
            }
            catch (Exception ex)
            {
                Log.Write($"Could not draw {IPhoneDuo.FrameKey(pose)}: {ex.Message}");
                if (IsCurrent(dir)) frames.Add(Frame(root, pose)); // another process won the race
            }
        }
        return frames;
    }

    private static bool IsCurrent(string dir)
    {
        try
        {
            string template = Path.Combine(dir, "template.json");
            if (!File.Exists(template) || !File.Exists(Path.Combine(dir, "frame.png")) || !File.Exists(Path.Combine(dir, "mask.png"))) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(template));
            return doc.RootElement.TryGetProperty("generator", out var g) && g.GetString() == Version;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes frame.png, mask.png, glass.png (open only) and template.json for one pose into <paramref name="dir"/>.</summary>
    public static void Write(DuoPose pose, string dir)
    {
        var layout = For(pose);
        int width = layout.FrameSize.Width, height = layout.FrameSize.Height;
        var frame = new byte[width * height * 4];
        var mask = new byte[width * height * 4];
        byte[]? glass = pose == DuoPose.Open ? new byte[width * height * 4] : null;

        var screen = layout.Screen;
        RectangleF Grow(int by) => RectangleF.FromLTRB(screen.Left - by, screen.Top - by, screen.Right + by, screen.Bottom + by);
        var shapes = new
        {
            Screen = (Box: (RectangleF)screen, L: layout.RadiusLeft, R: layout.RadiusRight),
            Glass = (Box: Grow(Glass), L: layout.RadiusLeft + Glass, R: layout.RadiusRight + Glass),
            Gasket = (Box: Grow(Glass + Gasket), L: layout.RadiusLeft + Glass + Gasket, R: layout.RadiusRight + Glass + Gasket),
            Body = (Box: Grow(Border), L: layout.RadiusLeft + Border, R: layout.RadiusRight + Border),
        };
        var body = shapes.Body.Box;
        var hinge = RectangleF.FromLTRB(body.Left - HingeOut, body.Top + HingeInset, body.Left + HingeIn, body.Bottom - HingeInset);
        float hingeRadius = HingeOut;
        float centre = screen.Left + screen.Width / 2f;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float dBody = RoundedBox(px, py, shapes.Body.Box, shapes.Body.L, shapes.Body.R);
                float dScreen = RoundedBox(px, py, shapes.Screen.Box, shapes.Screen.L, shapes.Screen.R);
                float screenCover = Cover(dScreen);
                int p = (y * width + x) * 4;
                byte m = (byte)Math.Round(screenCover * 255);
                mask[p] = mask[p + 1] = mask[p + 2] = m;
                mask[p + 3] = 255;

                // Light from the top left, across the whole phone.
                float light = 1.10f - 0.20f * (0.5f * (px - body.Left) / body.Width + 0.5f * (py - body.Top) / body.Height);

                // Back to front, premultiplied: hinge barrel, then the body with its screen hole.
                float r = 0, g = 0, b = 0, a = 0;
                if (pose == DuoPose.Folded)
                {
                    // Tucked under the body, so never through the screen hole.
                    float hingeCover = Cover(RoundedBox(px, py, hinge, hingeRadius, hingeRadius)) * (1 - screenCover);
                    if (hingeCover > 0)
                    {
                        var c = Barrel((px - hinge.Left) / HingeOut, light, dBody);
                        (r, g, b, a) = (c.R * hingeCover, c.G * hingeCover, c.B * hingeCover, hingeCover);
                    }
                }

                float bodyCover = Cover(dBody) * (1 - screenCover);
                if (bodyCover > 0)
                {
                    var c = Titanium(Math.Clamp(-dBody / Rim, 0, 1), light);
                    if (pose == DuoPose.Open) c = Seam(c, px - centre, -dBody);
                    float gasket = Cover(RoundedBox(px, py, shapes.Gasket.Box, shapes.Gasket.L, shapes.Gasket.R));
                    float glassCover = Cover(RoundedBox(px, py, shapes.Glass.Box, shapes.Glass.L, shapes.Glass.R));
                    c = Mix(c, (0.075f, 0.075f, 0.082f), gasket);
                    c = Mix(c, (0.020f * light, 0.020f * light, 0.024f * light), glassCover);
                    r = c.R * bodyCover + r * (1 - bodyCover);
                    g = c.G * bodyCover + g * (1 - bodyCover);
                    b = c.B * bodyCover + b * (1 - bodyCover);
                    a = bodyCover + a * (1 - bodyCover);
                }
                Store(frame, p, r, g, b, a);

                if (glass != null && screenCover > 0)
                {
                    var (cr, cg, cb, ca) = Crease((px - centre) / CreaseWidth + 0.5f);
                    Store(glass, p, cr * screenCover, cg * screenCover, cb * screenCover, ca * screenCover);
                }
            }
        });

        Directory.CreateDirectory(dir);
        SavePng(frame, width, height, Path.Combine(dir, "frame.png"));
        SavePng(mask, width, height, Path.Combine(dir, "mask.png"));
        if (glass != null) SavePng(glass, width, height, Path.Combine(dir, "glass.png"));
        File.WriteAllText(Path.Combine(dir, "template.json"), JsonSerializer.Serialize(new
        {
            frame = "frame.png",
            mask = "mask.png",
            glass = glass != null ? "glass.png" : null,
            screen = new { x = screen.X, y = screen.Y, width = screen.Width, height = screen.Height },
            frameSize = new { width, height },
            generator = Version,
        }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }

    /// <summary>
    /// Signed distance (negative inside) to a rounded rectangle whose two left corners have one radius and two right
    /// corners another, which is the closed Duo's shape: tight at the hinge, generous on the opening side.
    /// </summary>
    internal static float RoundedBox(float px, float py, RectangleF box, float radiusLeft, float radiusRight)
    {
        float cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
        float dx = px - cx, dy = py - cy;
        float radius = Math.Min(dx > 0 ? radiusRight : radiusLeft, Math.Min(box.Width, box.Height) / 2);
        float qx = Math.Abs(dx) - box.Width / 2 + radius, qy = Math.Abs(dy) - box.Height / 2 + radius;
        float outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    /// <summary>Pixel coverage from a signed distance: a one-pixel ramp centred on the edge.</summary>
    private static float Cover(float distance) => Math.Clamp(0.5f - distance, 0, 1);

    private static float Bump(float t, float at, float width) => MathF.Exp(-(t - at) * (t - at) / (width * width));

    /// <summary>
    /// The titanium band, outside (t = 0) to inside (t = 1): a thin catch-light on the outer roundover, a broad highlight
    /// where the edge turns towards the viewer, then darker into the gasket. A touch cool, like brushed titanium.
    /// </summary>
    private static (float R, float G, float B) Titanium(float t, float light)
    {
        float v = 0.33f + 0.20f * Bump(t, 0.66f, 0.20f) + 0.16f * Bump(t, 0.10f, 0.07f) - 0.10f * Math.Clamp((t - 0.88f) / 0.12f, 0, 1);
        v *= light;
        return (v, v, v * 1.03f);
    }

    /// <summary>The hinge barrel seen from the front: a cylinder, lit like the rim, shaded where it meets the body.</summary>
    private static (float R, float G, float B) Barrel(float u, float light, float distanceToBody)
    {
        u = Math.Clamp(u, 0, 1);
        float v = 0.16f + 0.30f * MathF.Pow(MathF.Sin(MathF.PI * Math.Clamp(u * 0.85f + 0.05f, 0, 1)), 0.7f) + 0.12f * Bump(u, 0.32f, 0.10f);
        float occlusion = 0.55f + 0.45f * Math.Clamp(distanceToBody / 8f, 0, 1);
        v *= light * occlusion;
        return (v, v, v * 1.03f);
    }

    /// <summary>Open, the rim is split where the two halves meet: a hairline across the titanium at the fold.</summary>
    private static (float R, float G, float B) Seam((float R, float G, float B) c, float offset, float depth)
    {
        if (depth > Rim + Gasket) return c;
        float k = 1 - 0.55f * Bump(offset, 0, 1.6f);
        return (c.R * k, c.G * k, c.B * k);
    }

    /// <summary>
    /// The crease across a band (0..1): a shade, a sheen in the middle, a shade, as premultiplied colour. Strength as in
    /// the HTML boards: 2.8 % black either side of 5 % white.
    /// </summary>
    private static (float R, float G, float B, float A) Crease(float t)
    {
        if (t <= 0 || t >= 1) return (0, 0, 0, 0);
        (float Pos, float W, float A)[] stops = [(0, 0, 0), (0.42f, 0, 0.028f), (0.5f, 1, 0.05f), (0.58f, 0, 0.028f), (1, 0, 0)];
        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].Pos) continue;
            var (p0, w0, a0) = stops[i - 1];
            var (p1, w1, a1) = stops[i];
            float k = (t - p0) / (p1 - p0);
            float premultiplied = w0 * a0 + (w1 * a1 - w0 * a0) * k;
            float alpha = a0 + (a1 - a0) * k;
            return (premultiplied, premultiplied, premultiplied, alpha);
        }
        return (0, 0, 0, 0);
    }

    private static (float R, float G, float B) Mix((float R, float G, float B) a, (float R, float G, float B) b, float k) =>
        (a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);

    /// <summary>Premultiplied 0..1 in, straight-alpha BGRA bytes out (what PNG wants).</summary>
    private static void Store(byte[] target, int p, float r, float g, float b, float a)
    {
        if (a <= 0) return;
        target[p] = ToByte(b / a);
        target[p + 1] = ToByte(g / a);
        target[p + 2] = ToByte(r / a);
        target[p + 3] = ToByte(a);
    }

    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static void SavePng(byte[] bgra, int width, int height, string path)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++) Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        bitmap.UnlockBits(data);
        bitmap.Save(path, ImageFormat.Png);
    }
}
