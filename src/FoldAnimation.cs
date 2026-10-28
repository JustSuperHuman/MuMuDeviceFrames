using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>A premultiplied BGRA image (top-down), what the animation maps onto the moving panels.</summary>
/// <summary>
/// The last picture of each pose of one emulator (its frame with the screen as it was), kept in memory so the next
/// switch into that pose shows the app as it was last seen there while the live picture is on its way. A picture is
/// only handed back for the same frame at the same size (the window was not resized since), and a newer one replaces it.
/// </summary>
internal sealed class PoseShots
{
    private readonly Dictionary<DuoPose, (RenderKey Key, Texture Image)> _shots = [];

    public void Put(DuoPose pose, RenderKey key, Texture image) => _shots[pose] = (key, image);

    public Texture? Get(DuoPose pose, RenderKey key) => _shots.TryGetValue(pose, out var shot) && shot.Key == key ? shot.Image : null;

    public void Clear() => _shots.Clear();
}

internal sealed class Texture(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>A quarter turn counter-clockwise (an upright device image laid on its side).</summary>
    public Texture RotateLeft()
    {
        var output = new byte[Pixels.Length];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Buffer.BlockCopy(Pixels, (y * Width + x) * 4, output, ((Width - 1 - x) * Height + y) * 4, 4);
        return new Texture(output, Height, Width);
    }
}

/// <summary>
/// The iPhone Duo's fold, animated. Android switches displays at once, so this covers the switch with the device
/// itself moving: unfolding, the closed phone's cover swings open about the hinge in perspective, showing the outer
/// display and then its inside, while the device grows into the open phone and its inner screen wakes; folding is the
/// same backwards. Book and tabletop tilt the halves into a V and settle; changing which way round the inner display
/// is turns the device. Closed to an upright pose unfolds and then turns.
///
/// Rendering is a small perspective renderer: each panel is a textured quad mapped through a homography (true
/// perspective, bilinear, anti-aliased edges), lit from the top left by its angle, with a sheen that sweeps across it,
/// a contact shadow where the lifting cover shades the page and a soft shadow under the device. It runs on its own
/// thread into a click-through layered window, one frame per compositor frame (DwmFlush), about 600 ms, then cross-fades
/// into the live picture once the session has it.
/// </summary>
internal sealed class FoldAnimation : IDisposable
{
    /// <summary>How the device looks at either end: closed, or the inner display landscape or upright.</summary>
    private enum Shape { Closed, Landscape, Upright }

    public sealed record End(Texture Image, RectangleF Screen);

    private readonly IntPtr _owner;
    private readonly DuoPose _from, _to;
    private readonly Shape _fromShape, _toShape;
    private readonly End _before;
    private End _after;              // placeholder (frame, dark screen) until the session has the real picture
    private readonly Texture? _provisional; // the pose's last picture, shown until the real one replaces it
    private Texture? _blend;          // the provisional picture cross-fading into the real one
    private bool _afterReal;
    private double _afterArrived;    // animation time the real picture arrived (it fades in)
    private RectangleF _target;      // smoothed towards the latest known after position
    private readonly object _gate = new();
    private readonly Rectangle _bounds; // the animation window, screen pixels
    private readonly float _hinge;      // closed hinge barrel width, screen pixels
    private readonly double _duration;
    private readonly string? _dumpFolder;
    private Thread? _thread;
    private volatile bool _fadeOut, _stop, _atRest, _onScreen;
    private bool _landed;

    /// <summary>Raised on the animation thread once the device has landed and the real after picture is in.</summary>
    public event Action? Landed;

    public bool Running => _thread is { IsAlive: true };

    /// <summary>The device is on screen in its new place and shape, holding there until the new picture is in.</summary>
    public bool AtRest => _atRest;

    /// <summary>The first frame has been composited: the live device under it can go.</summary>
    public bool OnScreen => _onScreen;

    /// <summary>Raised on the animation thread once the first frame is on screen.</summary>
    public event Action? Shown;

    /// <param name="provisional">The new pose's last picture (<see cref="PoseShots"/>), the same size as the
    /// placeholder: shown in its place until the real picture arrives, then cross-faded into it. Null: a dark screen
    /// that wakes when the real picture arrives.</param>
    public FoldAnimation(IntPtr owner, DuoPose from, DuoPose to, End before, End afterPlaceholder, float hingeWidth, string? dumpFolder,
        Texture? provisional = null)
    {
        _provisional = provisional is { } p && p.Width == afterPlaceholder.Image.Width && p.Height == afterPlaceholder.Image.Height ? p : null;
        _owner = owner;
        _from = from;
        _to = to;
        _fromShape = ShapeOf(from, to);
        _toShape = ShapeOf(to, from);
        _before = before;
        _after = afterPlaceholder;
        _target = afterPlaceholder.Screen;
        _hinge = hingeWidth;
        _dumpFolder = dumpFolder;
        bool fold = (_fromShape == Shape.Closed) != (_toShape == Shape.Closed);
        bool turn = _fromShape != Shape.Closed && _toShape != Shape.Closed && _fromShape != _toShape;
        bool chain = fold && (_fromShape == Shape.Upright || _toShape == Shape.Upright);
        _duration = chain ? 0.82 : fold ? 0.62 : turn ? 0.52 : 0.46;

        // The window covers both ends, the turning device's sweep and the shadow, within the screen.
        var a = before.Screen;
        var b = afterPlaceholder.Screen;
        var union = RectangleF.Union(a, b);
        float sweep = (float)Math.Max(Math.Sqrt(a.Width * a.Width + a.Height * a.Height), Math.Sqrt(b.Width * b.Width + b.Height * b.Height)) * 1.12f;
        var centre = new PointF(union.X + union.Width / 2, union.Y + union.Height / 2);
        union = RectangleF.Union(union, new RectangleF(centre.X - sweep / 2, centre.Y - sweep / 2, sweep, sweep));
        union.Inflate(union.Width * 0.06f + 40, union.Height * 0.06f + 40);
        var screen = SystemInformation.VirtualScreen;
        _bounds = Rectangle.Intersect(Rectangle.Round(union), screen);
    }

    /// <summary>
    /// How an end of the switch animates. Tent is the closed phone turned landscape: it turns (from closed) or morphs
    /// (to and from the open display) like a landscape screen, never swings open about a hinge on its side.
    /// </summary>
    private static Shape ShapeOf(DuoPose pose, DuoPose other) => pose switch
    {
        DuoPose.Tent => Shape.Landscape,
        DuoPose.Folded when other == DuoPose.Tent => Shape.Upright,
        _ => !IPhoneDuo.IsInner(pose) ? Shape.Closed : IPhoneDuo.IsUpright(pose) ? Shape.Upright : Shape.Landscape,
    };

    /// <summary>Animations off in Windows (Settings, Accessibility, Visual effects): the switch is instant instead.</summary>
    public static bool SystemAllowsAnimation()
    {
        int enabled = 1;
        return !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled != 0;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Fold animation", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>The real picture of the new pose and where the device now is on screen (from the session, any thread).</summary>
    public void SetAfter(Texture image, RectangleF screen)
    {
        if (_dumpFolder != null) SaveTexture(image, Path.Combine(_dumpFolder, "after.png"));
        lock (_gate)
        {
            _after = new End(image, screen);
            _target = screen;
            _afterReal = true;
            _afterArrived = -1; // stamped by the render loop
        }
    }

    /// <summary>The device's final place moved (MuMu turned its window): glide there.</summary>
    public void Retarget(RectangleF screen)
    {
        lock (_gate) _target = screen;
    }

    /// <summary>After <see cref="Landed"/>: fade out over the live picture and close.</summary>
    public void FadeOut() => _fadeOut = true;

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
    }

    // ---- the render loop ----------------------------------------------------------------------------------------

    private void Run()
    {
        OverlayWindow? window = null;
        try
        {
            window = new OverlayWindow(_owner, interactive: false);
            using var surface = new Surface(_bounds.Width, _bounds.Height);
            using var layered = new LayeredBuffer(surface.Width, surface.Height);
            var clock = Stopwatch.StartNew();
            int frame = 0;
            double t = 0, fadeStart = -1;
            bool shown = false;
            if (_dumpFolder != null)
            {
                Directory.CreateDirectory(_dumpFolder);
                SaveTexture(_before.Image, Path.Combine(_dumpFolder, "before.png"));
                SaveTexture(_after.Image, Path.Combine(_dumpFolder, "placeholder.png"));
            }
            while (!_stop)
            {
                // Dumping renders exact 60 fps steps however long saving takes; live follows the clock.
                double time = _dumpFolder != null ? frame / 60.0 : clock.Elapsed.TotalSeconds;
                t = Math.Min(1, time / _duration);
                byte opacity = 255;
                if (_fadeOut)
                {
                    if (fadeStart < 0) fadeStart = time;
                    double f = (time - fadeStart) / 0.16;
                    if (f >= 1) break;
                    opacity = (byte)Math.Round(255 * (1 - f * f * (3 - 2 * f)));
                }

                Render(surface, t, time);
                layered.Show(window.Handle, surface.Pixels, _bounds.Location, opacity);
                if (!shown)
                {
                    window.SetVisible(true);
                    SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); // above the frame
                    shown = true;
                }
                if (_dumpFolder != null) surface.Save(Path.Combine(_dumpFolder, $"{frame:D4}.png"), opacity);
                frame++;
                if (t >= 1) _atRest = true;

                bool ready;
                lock (_gate) ready = _afterReal && time - Math.Max(_afterArrived, 0) >= 0.12;
                // A switch now ends with the display off and on (the safe areas), so the new picture can take ~1.5 s.
                if (t >= 1 && !_landed && (ready || time > _duration + 2.6))
                {
                    _landed = true;
                    Landed?.Invoke();
                }
                if (_dumpFolder == null) DwmFlush(); // one frame per compositor frame: no tearing, no stutter
                if (!_onScreen)
                {
                    _onScreen = true;
                    Shown?.Invoke();
                }
                PumpMessages();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Fold animation failed: {ex}");
            if (!_landed) Landed?.Invoke();
        }
        finally
        {
            if (window is { IsAlive: true }) window.DestroyHandle();
        }
    }

    private static void SaveTexture(Texture image, string path)
    {
        var surface = new Surface(image.Width, image.Height);
        Buffer.BlockCopy(image.Pixels, 0, surface.Pixels, 0, image.Pixels.Length);
        surface.Save(path, 255);
    }

    private static void PumpMessages()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    // ---- the scene ------------------------------------------------------------------------------------------------

    /// <summary>A hinge that starts slowly, swings, and settles into place with the slightest give.</summary>
    internal static double Hinge(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in and out
        return e + 0.035 * Math.Sin(Math.PI * Math.Pow(t, 1.4)) * Math.Pow(t, 6) * (1 - t) * 18; // a touch of overshoot, gone at 1
    }

    private static double Smooth(double a, double b, double t)
    {
        double x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private void Render(Surface surface, double t, double time)
    {
        End before = _before, after;
        RectangleF target;
        double arrived;
        lock (_gate)
        {
            if (_afterReal && _afterArrived < 0) _afterArrived = time;
            after = _after;
            target = _target;
            arrived = _afterArrived;
        }
        // The new picture fades in over the placeholder (an inner screen waking up), not a pop. With the pose's last
        // picture standing in, the screen is lit from the start and that picture cross-fades into the real one.
        float wake = _afterReal ? (float)Math.Clamp((time - arrived) / 0.18, 0, 1) : 0;
        var afterImage = after.Image;
        if (_provisional is { } provisional)
        {
            afterImage = !_afterReal ? provisional : wake >= 1 ? after.Image : Blend(provisional, after.Image, wake);
            wake = 1;
        }
        // Glide to the latest target (MuMu may move its window when it turns it).
        after = new End(afterImage, target);

        surface.Clear();
        var origin = new PointF(_bounds.X, _bounds.Y);
        bool fold = (_fromShape == Shape.Closed) != (_toShape == Shape.Closed);
        if (fold)
        {
            // Unfold forwards, fold backwards; an upright end turns before folding or after unfolding.
            bool unfolding = _fromShape == Shape.Closed;
            var closedEnd = unfolding ? before : after;
            var innerEnd = unfolding ? after : before;
            var innerShape = unfolding ? _toShape : _fromShape;
            bool upright = innerShape == Shape.Upright;
            double swingT = upright ? (unfolding ? Smooth(0, 0.66, t) : Smooth(0.34, 1, t)) : t;
            double turnT = upright ? (unfolding ? Smooth(0.5, 1, t) : 1 - Smooth(0, 0.5, t)) : 0;
            double p = Hinge(unfolding ? swingT : 1 - swingT);
            // The inner device laid landscape: an upright end's image and place turned onto its side.
            var innerImage = upright ? Rotated(innerEnd.Image) : innerEnd.Image;
            var innerRect = upright ? Sideways(innerEnd.Screen) : innerEnd.Screen;
            float innerWake = unfolding ? wake : 1, closedWake = unfolding ? 1 : wake;
            if (p < 0.999 || turnT <= 0)
                DrawSwing(surface, origin, closedEnd, closedWake, innerImage, innerRect, innerWake, (float)p);
            else
                DrawInner(surface, origin, innerImage, null, 0, innerRect, innerEnd.Screen, (float)Hinge(turnT), 0, innerWake);
        }
        else
        {
            // Inner to inner: tilt into a V (book, tabletop) and settle; turn when the way round changes.
            var fromImage = _fromShape == Shape.Upright ? Rotated(before.Image) : before.Image;
            var toImage = _toShape == Shape.Upright ? Rotated(after.Image) : after.Image;
            var fromRect = _fromShape == Shape.Upright ? Sideways(before.Screen) : before.Screen;
            float startTurn = _fromShape == Shape.Upright ? 1 : 0, endTurn = _toShape == Shape.Upright ? 1 : 0;
            double e = Hinge(t);
            float turn = (float)(startTurn + (endTurn - startTurn) * e);
            float tilt = (float)(Math.Sin(Math.PI * Math.Clamp(t, 0, 1)) * (startTurn != endTurn ? 10 : 22) * Math.PI / 180);
            // Cross-fade into the new pose's frame as the device turns or tilts; until the new picture is there its
            // screen is dark (the placeholder), and the picture wakes in when it arrives. The old content stays
            // bright early on, so it never lingers turned sideways.
            float mix = (float)Smooth(0.25, 0.75, t);
            float screenWake = 1 - mix + mix * wake;
            var landscapeTo = _toShape == Shape.Upright ? Sideways(after.Screen) : after.Screen;
            var rect = Lerp(fromRect, landscapeTo, (float)e);
            var finalUpright = _toShape == Shape.Upright ? after.Screen : _fromShape == Shape.Upright ? before.Screen : rect;
            DrawInner(surface, origin, fromImage, toImage, mix, rect, finalUpright, turn, tilt, screenWake);
        }
    }

    /// <summary><paramref name="a"/> cross-faded into <paramref name="b"/> by <paramref name="k"/>, into a buffer reused frame to frame.</summary>
    private Texture Blend(Texture a, Texture b, float k)
    {
        if (a.Width != b.Width || a.Height != b.Height) return b;
        if (_blend is not { } buffer || buffer.Width != b.Width || buffer.Height != b.Height) _blend = buffer = new Texture(new byte[b.Pixels.Length], b.Width, b.Height);
        int w = (int)Math.Round(k * 256);
        byte[] pa = a.Pixels, pb = b.Pixels, po = buffer.Pixels;
        for (int i = 0; i < po.Length; i++) po[i] = (byte)((pa[i] * (256 - w) + pb[i] * w) >> 8);
        // A new texture object each frame, so the rotation cache does not hand back last frame's blend.
        return new Texture(po, buffer.Width, buffer.Height);
    }

    private Texture? _rotatedBefore, _rotatedAfter;
    private Texture? _rotatedSource1, _rotatedSource2;

    /// <summary>An upright inner image laid on its side, cached (the source changes once, when the real picture arrives).</summary>
    private Texture Rotated(Texture image)
    {
        if (ReferenceEquals(image, _rotatedSource1)) return _rotatedBefore!;
        if (ReferenceEquals(image, _rotatedSource2)) return _rotatedAfter!;
        var turned = image.RotateLeft();
        if (_rotatedSource1 == null) (_rotatedSource1, _rotatedBefore) = (image, turned);
        else (_rotatedSource2, _rotatedAfter) = (image, turned);
        return turned;
    }

    private static RectangleF Sideways(RectangleF r) =>
        new(r.X + r.Width / 2 - r.Height / 2, r.Y + r.Height / 2 - r.Width / 2, r.Height, r.Width);

    private static RectangleF Lerp(RectangleF a, RectangleF b, float k) =>
        new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.Width + (b.Width - a.Width) * k, a.Height + (b.Height - a.Height) * k);

    private static float Lerp(float a, float b, float k) => a + (b - a) * k;

    // Light from the top left and in front; the eye looks straight at the screen.
    private static float Lit(float nx, float nz) => Math.Clamp(0.58f + 0.42f * (-0.34f * nx + 0.94f * nz), 0.25f, 1.08f);

    /// <summary>
    /// The book opening: the inner page lies flat right of the hinge, the cover turns about the hinge from closed (0)
    /// to open (1), its outer display facing us until it is edge-on, its inside after.
    /// </summary>
    private void DrawSwing(Surface surface, PointF origin, End closed, float closedWake, Texture inner, RectangleF innerRect, float innerWake, float p)
    {
        var c = closed.Screen;
        float pe = Math.Clamp(p, 0, 1);
        float hinge = Lerp(c.Left + _hinge, innerRect.Left + innerRect.Width / 2, pe);
        float top = Lerp(c.Top, innerRect.Top, pe), bottom = Lerp(c.Bottom, innerRect.Bottom, pe);
        float pageWidth = Lerp(c.Width - _hinge, innerRect.Width / 2, pe);
        float theta = (float)(Math.PI * p);
        float camera = Math.Max(c.Height, innerRect.Height) * 2.6f;
        var eye = new PointF(Lerp(c.Left + c.Width / 2, innerRect.Left + innerRect.Width / 2, pe) - origin.X, (top + bottom) / 2 - origin.Y);

        // Soft shadow under the device, widening as it opens.
        float coverReach = pageWidth * MathF.Cos(theta);
        float left = Math.Min(hinge + Math.Min(coverReach, 0), hinge - _hinge * (1 - pe)), right = hinge + pageWidth;
        surface.Shadow(new RectangleF(left - origin.X, top - origin.Y, right - left, bottom - top), 0.30f, (bottom - top) * 0.05f);

        // The inner page: its right half; the lifting cover shades it near the hinge.
        float shade = 0.42f * MathF.Sin(Math.Clamp(theta, 0, MathF.PI));
        var page = Quad(origin, eye, camera, (hinge, top, 0), (hinge + pageWidth, top, 0), (hinge + pageWidth, bottom, 0), (hinge, bottom, 0));
        surface.Draw(page, inner, 0.5f, 1, null, 0, Lit(0, 1) * (1 - shade), Lit(0, 1), 0, 1, innerWake);

        // The cover: point u from the hinge sits at (hinge + u cos θ, ·, u sin θ). Front face until edge-on.
        float coverWidth = pageWidth;
        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
        float barrel = _hinge * (1 - pe); // the closed phone's hinge barrel, left of the hinge
        var cover = Quad(origin, eye, camera,
            (hinge - barrel * cos, top, -barrel * sin), (hinge + coverWidth * cos, top, coverWidth * sin),
            (hinge + coverWidth * cos, bottom, coverWidth * sin), (hinge - barrel * cos, bottom, -barrel * sin));
        float sheen = 0.20f * MathF.Exp(-MathF.Pow((theta - 0.75f) / 0.32f, 2)) + 0.14f * MathF.Exp(-MathF.Pow((theta - 2.35f) / 0.32f, 2));
        if (cos >= 0)
        {
            float light = Lit(-sin, cos);
            surface.Draw(cover, closed.Image, 0, 1, null, 0, light, light * 0.94f, sheen, 1, closedWake);
        }
        else
        {
            // Its inside: the left half of the inner display, hinge edge at the texture's middle.
            float light = Lit(sin, -cos);
            surface.Draw(cover, inner, 0.5f, 0, null, 0, light, light * 0.9f, sheen, 1, innerWake);
        }
    }

    /// <summary>
    /// The inner device, laid landscape in <paramref name="rect"/>, turned by <paramref name="turn"/> quarter turns
    /// clockwise about its centre (gliding towards <paramref name="uprightRect"/>'s centre as it turns), its halves
    /// tilted back by <paramref name="tilt"/> about the hinge, cross-faded from one image to another.
    /// </summary>
    private void DrawInner(Surface surface, PointF origin, Texture image, Texture? next, float mix, RectangleF rect, RectangleF uprightRect, float turn, float tilt, float wake)
    {
        float w = rect.Width, h = rect.Height;
        var from = new PointF(rect.X + w / 2, rect.Y + h / 2);
        var to = new PointF(uprightRect.X + uprightRect.Width / 2, uprightRect.Y + uprightRect.Height / 2);
        float glide = Math.Clamp(turn, 0, 1);
        var centre = new PointF(Lerp(from.X, to.X, turn > 0 && rect != uprightRect ? glide : 0), Lerp(from.Y, to.Y, turn > 0 && rect != uprightRect ? glide : 0));
        float lift = 1 - 0.05f * MathF.Sin(MathF.PI * glide); // the device rises a little as it turns
        float angle = turn * MathF.PI / 2;
        float camera = Math.Max(w, h) * 2.6f;
        var eye = new PointF(centre.X - origin.X, centre.Y - origin.Y);
        (float X, float Y, float Z) Place(float x, float y, float z)
        {
            float rx = (x * MathF.Cos(angle) - y * MathF.Sin(angle)) * lift, ry = (x * MathF.Sin(angle) + y * MathF.Cos(angle)) * lift;
            return (centre.X + rx, centre.Y + ry, z * lift);
        }

        surface.Shadow(new RectangleF(eye.X - w * lift / 2, eye.Y - h * lift / 2, w * lift, h * lift).Turned(angle, eye), 0.30f, h * 0.05f);

        float cos = MathF.Cos(tilt), sin = MathF.Sin(tilt), half = w / 2;
        // Left half: from the outer edge (x = -half) to the hinge; tilted back, its outer edge recedes.
        var leftQuad = Quad(origin, eye, camera, Place(-half * cos, -h / 2, -half * sin), Place(0, -h / 2, 0), Place(0, h / 2, 0), Place(-half * cos, h / 2, -half * sin));
        var rightQuad = Quad(origin, eye, camera, Place(0, -h / 2, 0), Place(half * cos, -h / 2, -half * sin), Place(half * cos, h / 2, -half * sin), Place(0, h / 2, 0));
        float leftLight = Lit(-sin * MathF.Cos(angle), cos), rightLight = Lit(sin * MathF.Cos(angle), cos);
        float sheen = 0.10f * MathF.Sin(MathF.PI * Math.Clamp(Math.Abs(tilt) * 4, 0, 1));
        surface.Draw(leftQuad, image, 0, 0.5f, next, mix, leftLight, leftLight * (1 - 0.18f * sin), sheen, 1, wake);
        surface.Draw(rightQuad, image, 0.5f, 1, next, mix, rightLight * (1 - 0.18f * sin), rightLight, sheen * 0.6f, 1, wake);
    }

    /// <summary>Projects four 3D corners (screen pixels, z towards the eye) with a camera <paramref name="camera"/> px away.</summary>
    private static PointF[] Quad(PointF origin, PointF eye, float camera, params (float X, float Y, float Z)[] corners) =>
        corners.Select(p =>
        {
            float k = camera / (camera - p.Z);
            float x = p.X - origin.X, y = p.Y - origin.Y;
            return new PointF(eye.X + (x - eye.X) * k, eye.Y + (y - eye.Y) * k);
        }).ToArray();

    // ---- Win32 ---------------------------------------------------------------------------------------------------

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042, PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public POINT Point; }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG message);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}

/// <summary>A DIB section and memory DC kept for the whole animation: each frame is one copy and one UpdateLayeredWindow.</summary>
internal sealed class LayeredBuffer : IDisposable
{
    private readonly int _width, _height;
    private readonly IntPtr _screenDc, _memDc, _dib, _old, _bits;

    public LayeredBuffer(int width, int height)
    {
        (_width, _height) = (width, height);
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var header = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        _dib = CreateDIBSection(_screenDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_memDc, _dib);
    }

    public void Show(IntPtr window, byte[] premultiplied, Point position, byte opacity)
    {
        if (_dib == IntPtr.Zero) return;
        Marshal.Copy(premultiplied, 0, _bits, _width * _height * 4);
        var dst = new POINT { X = position.X, Y = position.Y };
        var size = new SIZE { Cx = _width, Cy = _height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
        UpdateLayeredWindow(window, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
    }

    public void Dispose()
    {
        SelectObject(_memDc, _old);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }
}

internal static class RectangleTurn
{
    /// <summary>The bounding box of a rectangle turned by <paramref name="angle"/> about <paramref name="centre"/>.</summary>
    public static RectangleF Turned(this RectangleF r, float angle, PointF centre)
    {
        float c = MathF.Abs(MathF.Cos(angle)), s = MathF.Abs(MathF.Sin(angle));
        float w = r.Width * c + r.Height * s, h = r.Width * s + r.Height * c;
        return new RectangleF(centre.X - w / 2, centre.Y - h / 2, w, h);
    }
}

/// <summary>The animation's canvas: premultiplied BGRA, plus a perspective textured-quad rasterizer.</summary>
internal sealed class Surface : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Surface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new byte[Width * Height * 4];
    }

    public void Clear() => Array.Clear(Pixels);

    /// <summary>A soft rounded shadow, a little below <paramref name="r"/>.</summary>
    public void Shadow(RectangleF r, float opacity, float blur)
    {
        blur = Math.Max(4, blur);
        r.Offset(0, blur * 0.6f);
        int x0 = Math.Max(0, (int)(r.Left - blur * 2)), x1 = Math.Min(Width, (int)(r.Right + blur * 2));
        int y0 = Math.Max(0, (int)(r.Top - blur * 2)), y1 = Math.Min(Height, (int)(r.Bottom + blur * 2));
        float cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, hw = r.Width / 2 - blur, hh = r.Height / 2 - blur;
        Parallel.For(y0, y1, y =>
        {
            for (int x = x0; x < x1; x++)
            {
                float dx = Math.Max(Math.Abs(x + 0.5f - cx) - hw, 0), dy = Math.Max(Math.Abs(y + 0.5f - cy) - hh, 0);
                float d = MathF.Sqrt(dx * dx + dy * dy) / (blur * 2);
                if (d >= 1) continue;
                float a = opacity * (1 - d) * (1 - d);
                int p = (y * Width + x) * 4;
                for (int c = 0; c < 4; c++) Pixels[p + c] = (byte)(c == 3 ? a * 255 + Pixels[p + c] * (1 - a) : Pixels[p + c] * (1 - a));
            }
        });
    }

    /// <summary>
    /// Draws the texture's horizontal span [<paramref name="s0"/>, <paramref name="s1"/>] (full height) onto a projected
    /// quad (corners: top-left, top-right, bottom-right, bottom-left of that span), perspective-correct, bilinear, with
    /// anti-aliased edges; brightness runs from <paramref name="lightStart"/> at the quad's left edge to
    /// <paramref name="lightEnd"/> at its right, <paramref name="sheen"/> adds a soft white. <paramref name="next"/>
    /// cross-fades in by <paramref name="mix"/>; <paramref name="wake"/> below 1 darkens the screen part (a screen
    /// coming on) by mixing towards black.
    /// </summary>
    public unsafe void Draw(PointF[] quad, Texture image, float s0, float s1, Texture? next, float mix, float lightStart, float lightEnd, float sheen, float opacity, float wake)
    {
        // Square-to-quad homography (Heckbert), then its inverse: screen pixel -> unit square.
        if (!Homography(quad, out var m) || !Invert(m, out var inv)) return;
        float minX = quad.Min(q => q.X), maxX = quad.Max(q => q.X), minY = quad.Min(q => q.Y), maxY = quad.Max(q => q.Y);
        int x0 = Math.Max(0, (int)MathF.Floor(minX) - 1), x1 = Math.Min(Width, (int)MathF.Ceiling(maxX) + 1);
        int y0 = Math.Max(0, (int)MathF.Floor(minY) - 1), y1 = Math.Min(Height, (int)MathF.Ceiling(maxY) + 1);
        if (x1 <= x0 || y1 <= y0) return;

        // Edge lines for coverage (signed distance, positive inside whichever way the quad winds).
        var edges = new (float A, float B, float C)[4];
        float area = 0;
        for (int i = 0; i < 4; i++) area += quad[i].X * quad[(i + 1) % 4].Y - quad[(i + 1) % 4].X * quad[i].Y;
        float sign = area >= 0 ? 1 : -1;
        for (int i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            float ex = b.X - a.X, ey = b.Y - a.Y, len = MathF.Sqrt(ex * ex + ey * ey);
            if (len < 1e-3f) { edges[i] = (0, 0, 1e6f); continue; }
            edges[i] = (-ey / len * sign, ex / len * sign, (ey * a.X - ex * a.Y) / len * sign);
        }

        byte[] src = image.Pixels, dst = Pixels;
        int tw = image.Width, th = image.Height;
        byte[]? alt = next?.Pixels;
        int aw = next?.Width ?? 0, ah = next?.Height ?? 0;
        int width = Width;
        Parallel.For(y0, y1, y =>
        {
            float py = y + 0.5f;
            fixed (byte* sp = src, dp = dst)
            fixed (byte* ap = alt)
            {
                for (int x = x0; x < x1; x++)
                {
                    float px = x + 0.5f;
                    float coverage = 1;
                    for (int e = 0; e < 4; e++)
                    {
                        float d = edges[e].A * px + edges[e].B * py + edges[e].C;
                        if (d < coverage - 0.5f) coverage = d + 0.5f;
                    }
                    if (coverage <= 0) continue;
                    if (coverage > 1) coverage = 1;
                    float w = inv[6] * px + inv[7] * py + inv[8];
                    if (Math.Abs(w) < 1e-9f) continue;
                    float u = (inv[0] * px + inv[1] * py + inv[2]) / w, v = (inv[3] * px + inv[4] * py + inv[5]) / w;
                    u = Math.Clamp(u, 0, 1);
                    v = Math.Clamp(v, 0, 1);
                    float s = s0 + (s1 - s0) * u;
                    Sample(sp, tw, th, s, v, out float b, out float g, out float r, out float a);
                    if (ap != null && mix > 0)
                    {
                        Sample(ap, aw, ah, s, v, out float b2, out float g2, out float r2, out float a2);
                        b += (b2 - b) * mix; g += (g2 - g) * mix; r += (r2 - r) * mix; a += (a2 - a) * mix;
                    }
                    if (a <= 0.5f) continue;
                    if (wake < 1)
                    {
                        // Dark until the screen wakes (keeps alpha, so the frame and its glass still show).
                        float keep = 0.12f + 0.88f * wake;
                        b *= keep; g *= keep; r *= keep;
                    }
                    float light = lightStart + (lightEnd - lightStart) * u;
                    float shine = sheen * a;
                    float k = coverage * opacity;
                    int p = (y * width + x) * 4;
                    float ia = 1 - a * k / 255f;
                    dp[p] = (byte)Math.Min(255, (b * light + shine) * k + dp[p] * ia);
                    dp[p + 1] = (byte)Math.Min(255, (g * light + shine) * k + dp[p + 1] * ia);
                    dp[p + 2] = (byte)Math.Min(255, (r * light + shine) * k + dp[p + 2] * ia);
                    dp[p + 3] = (byte)Math.Min(255, a * k + dp[p + 3] * ia);
                }
            }
        });
    }

    private static unsafe void Sample(byte* image, int width, int height, float s, float t, out float b, out float g, out float r, out float a)
    {
        float fx = s * width - 0.5f, fy = t * height - 0.5f;
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float ax = fx - ix, ay = fy - iy;
        int x0 = Math.Clamp(ix, 0, width - 1), x1 = Math.Clamp(ix + 1, 0, width - 1);
        int y0 = Math.Clamp(iy, 0, height - 1), y1 = Math.Clamp(iy + 1, 0, height - 1);
        byte* p00 = image + (y0 * width + x0) * 4, p10 = image + (y0 * width + x1) * 4, p01 = image + (y1 * width + x0) * 4, p11 = image + (y1 * width + x1) * 4;
        float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
        b = p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11;
        g = p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11;
        r = p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11;
        a = p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11;
    }

    /// <summary>The 3x3 matrix taking the unit square (0,0),(1,0),(1,1),(0,1) to the quad's corners.</summary>
    internal static bool Homography(PointF[] q, out float[] m)
    {
        float x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        float sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        float dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        float den = dx1 * dy2 - dx2 * dy1;
        m = new float[9];
        if (Math.Abs(den) < 1e-6f) return false;
        float g = (sx * dy2 - dx2 * sy) / den, h = (dx1 * sy - sx * dy1) / den;
        m[0] = x1 - x0 + g * x1; m[1] = x3 - x0 + h * x3; m[2] = x0;
        m[3] = y1 - y0 + g * y1; m[4] = y3 - y0 + h * y3; m[5] = y0;
        m[6] = g; m[7] = h; m[8] = 1;
        return true;
    }

    internal static bool Invert(float[] m, out float[] inv)
    {
        inv = new float[9];
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-12) return false;
        double k = 1 / det;
        inv[0] = (float)((e * i - f * h) * k); inv[1] = (float)((c * h - b * i) * k); inv[2] = (float)((b * f - c * e) * k);
        inv[3] = (float)((f * g - d * i) * k); inv[4] = (float)((a * i - c * g) * k); inv[5] = (float)((c * d - a * f) * k);
        inv[6] = (float)((d * h - e * g) * k); inv[7] = (float)((b * g - a * h) * k); inv[8] = (float)((a * e - b * d) * k);
        return true;
    }

    /// <summary>For the frame dump (the video): on white, so the frames read on their own.</summary>
    public void Save(string path, byte opacity)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var row = new byte[data.Stride];
        float k = opacity / 255f;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int p = (y * Width + x) * 4;
                float a = Pixels[p + 3] * k / 255f;
                for (int c = 0; c < 3; c++) row[x * 3 + c] = (byte)Math.Min(255, Pixels[p + c] * k + 255 * (1 - a));
            }
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
        }
        bitmap.UnlockBits(data);
        bitmap.Save(path, ImageFormat.Png);
    }

    public void Dispose() { }
}
