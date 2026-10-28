using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>
/// The pose control beside an iPhone Duo frame: a vertical glass pill, one button per pose, each a small silhouette of
/// the device held that way. The current pose is lit, hovering shows the pose and its hotkey, a click switches. It is
/// its own window outside the bezel, so it never sits over the emulator and never takes the emulator's clicks; its
/// transparent pixels let clicks through to whatever is behind.
///
/// Everything is drawn from code at the monitor's scale, so it is crisp at any DPI.
/// </summary>
internal sealed class DuoPosePanel : IDisposable
{
    private const float Button = 46, Gap = 4, Pad = 7, PillWidth = Button + 2 * Pad, TipWidth = 250, TipGap = 10, Offset = 18;

    private readonly OverlayWindow _window;
    private readonly Func<DuoPose, string?> _hotkey;
    private readonly Action<DuoPose> _choose;
    private DuoPose? _current;
    private bool _foldable;
    private int _hover = -1;
    private float _scale = 1;
    private bool _tipOnLeft;
    private Point _pillOrigin; // pill's top-left on screen
    private (DuoPose? Current, bool Foldable, int Hover, float Scale, bool Left)? _drawn;

    public DuoPosePanel(IntPtr emulator, Func<DuoPose, string?> hotkey, Action<DuoPose> choose)
    {
        _hotkey = hotkey;
        _choose = choose;
        _window = new OverlayWindow(emulator, interactive: true) { Mouse = OnMouse };
    }

    public OverlayWindow Window => _window;
    public bool IsAlive => _window.IsAlive;

    /// <summary>
    /// The panel as the window shows it, without a window (for <c>--snapshot</c> and the README): premultiplied BGRA,
    /// the pill at the left (tip, when <paramref name="hover"/> is a button, to its right).
    /// </summary>
    public static (byte[] Pixels, Size Size) Preview(DuoPose? current, bool foldable, float scale, int hover, Func<DuoPose, string?> hotkey)
    {
        var panel = new DuoPosePanel(hotkey) { _current = current, _foldable = foldable, _scale = scale, _hover = hover };
        var size = panel.WindowBounds.Size;
        return (panel.Render(size), size);
    }

    public static float LogicalHeight => PillHeight;
    public static float LogicalOffset => Offset;
    public static float PillWidthLogical => PillWidth;

    private DuoPosePanel(Func<DuoPose, string?> hotkey)
    {
        _hotkey = hotkey;
        _choose = _ => { };
        _window = null!;
    }

    /// <summary>Puts the pill beside the device: right of <paramref name="device"/> (screen pixels), or left when the monitor ends.</summary>
    public void Show(Rectangle device, DuoPose? current, bool foldable, float scale)
    {
        if (!_window.IsAlive) return;
        _current = current;
        _foldable = foldable;
        _scale = scale;
        int pillW = Px(PillWidth), pillH = Px(PillHeight);
        var work = System.Windows.Forms.Screen.FromRectangle(device).WorkingArea;
        int right = device.Right + Px(Offset);
        _tipOnLeft = right + pillW + Px(TipGap + TipWidth) > work.Right;
        bool pillOnLeft = right + pillW > work.Right;
        int x = pillOnLeft ? device.Left - Px(Offset) - pillW : right;
        int y = device.Top + (device.Height - pillH) / 2;
        _pillOrigin = new Point(x, y);
        Redraw(force: false);
        _window.SetVisible(true);
    }

    public void Hide() => _window.SetVisible(false);

    private static float PillHeight => 2 * Pad + IPhoneDuo.Poses.Length * Button + (IPhoneDuo.Poses.Length - 1) * Gap;
    private int Px(float logical) => (int)Math.Round(logical * _scale);

    /// <summary>The window holds the pill plus room for the tip on the side away from the device.</summary>
    private Rectangle WindowBounds => _tipOnLeft
        ? new Rectangle(_pillOrigin.X - Px(TipGap + TipWidth), _pillOrigin.Y, Px(TipGap + TipWidth + PillWidth), Px(PillHeight))
        : new Rectangle(_pillOrigin.X, _pillOrigin.Y, Px(PillWidth + TipGap + TipWidth), Px(PillHeight));

    private RectangleF PillRect => new(_tipOnLeft ? Px(TipGap + TipWidth) : 0, 0, Px(PillWidth), Px(PillHeight));

    private RectangleF ButtonRect(int index)
    {
        var pill = PillRect;
        return new RectangleF(pill.X + Px(Pad), pill.Y + Px(Pad) + index * Px(Button + Gap), Px(Button), Px(Button));
    }

    private bool Enabled(DuoPose pose) => !IPhoneDuo.IsHalf(pose) || _foldable;

    private void Redraw(bool force)
    {
        var state = (_current, _foldable, _hover, _scale, _tipOnLeft);
        var bounds = WindowBounds;
        if (!force && _drawn == state)
        {
            _window.MoveTo(bounds.Location);
            return;
        }
        _drawn = state;
        var pixels = Render(bounds.Size);
        _window.SetBitmap(pixels, bounds.Width, bounds.Height, bounds.Location);
    }

    private byte[] Render(Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Glass: dark and mostly opaque, a titanium-grey edge like the frame's rim, a soft light along the top.
            var pill = PillRect;
            using (var path = Rounded(pill, pill.Width / 2))
            {
                using var fill = new LinearGradientBrush(pill, Color.FromArgb(232, 34, 34, 38), Color.FromArgb(232, 14, 14, 16), 90f);
                g.FillPath(fill, path);
                using var edge = new Pen(Color.FromArgb(200, 96, 96, 102), Math.Max(1f, _scale));
                g.DrawPath(edge, path);
            }

            for (int i = 0; i < IPhoneDuo.Poses.Length; i++)
            {
                var pose = IPhoneDuo.Poses[i];
                var box = ButtonRect(i);
                bool on = pose == _current, enabled = Enabled(pose);
                if (on || i == _hover && enabled)
                {
                    using var highlight = Rounded(RectangleF.Inflate(box, -Px(1), -Px(1)), Px(12));
                    using var brush = new SolidBrush(Color.FromArgb(on ? 58 : 26, 255, 255, 255));
                    g.FillPath(brush, highlight);
                }
                int alpha = !enabled ? 80 : on ? 255 : i == _hover ? 235 : 190;
                DrawIcon(g, pose, RectangleF.Inflate(box, -Px(7), -Px(7)), Color.FromArgb(alpha, 240, 240, 244), on);
            }

            if (_hover >= 0) DrawTip(g, _hover);
        }

        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size.Width * 4, size.Width * 4);
        bitmap.UnlockBits(data);
        return pixels;
    }

    private void DrawTip(Graphics g, int index)
    {
        var pose = IPhoneDuo.Poses[index];
        string title = IPhoneDuo.Variant(pose) + (_hotkey(pose) is { } key ? $"   {key}" : "");
        string detail = Enabled(pose) ? IPhoneDuo.Describe(pose).Split(": ", 2)[^1] : "Needs Set up as iPhone Duo (foldable)";
        using var titleFont = new Font("Segoe UI Semibold", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        using var detailFont = new Font("Segoe UI", 8.5f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        var titleSize = g.MeasureString(title, titleFont);
        var detailSize = g.MeasureString(detail, detailFont, Px(TipWidth - 20));
        float w = Math.Min(Px(TipWidth), Math.Max(titleSize.Width, detailSize.Width) + Px(20)), h = titleSize.Height + detailSize.Height + Px(14);
        var button = ButtonRect(index);
        float x = _tipOnLeft ? PillRect.Left - Px(TipGap) - w : PillRect.Right + Px(TipGap);
        float y = Math.Clamp(button.Top + button.Height / 2 - h / 2, 0, Px(PillHeight) - h);
        var tip = new RectangleF(x, y, w, h);
        using (var path = Rounded(tip, Px(9)))
        {
            using var fill = new SolidBrush(Color.FromArgb(240, 24, 24, 27));
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(190, 96, 96, 102), Math.Max(1f, _scale));
            g.DrawPath(edge, path);
        }
        using var white = new SolidBrush(Color.FromArgb(250, 245, 245, 247));
        using var grey = new SolidBrush(Color.FromArgb(230, 170, 170, 178));
        g.DrawString(title, titleFont, white, x + Px(10), y + Px(6));
        g.DrawString(detail, detailFont, grey, new RectangleF(x + Px(10), y + Px(7) + titleSize.Height, w - Px(20), detailSize.Height + Px(2)));
    }

    /// <summary>A silhouette of the device in a pose, in a square box: outline, a lighter screen, the fold where there is one.</summary>
    internal static void DrawIcon(Graphics g, DuoPose pose, RectangleF box, Color ink, bool lit)
    {
        float S(float v) => v * box.Width;
        PointF P(float x, float y) => new(box.X + S(x), box.Y + S(y));
        float stroke = Math.Max(1.2f, box.Width / 17f);
        using var pen = new Pen(ink, stroke) { LineJoin = LineJoin.Round };
        using var screen = new SolidBrush(Color.FromArgb(ink.A * (lit ? 70 : 40) / 255, ink));
        using var crease = new Pen(Color.FromArgb(ink.A * 150 / 255, ink), Math.Max(1f, stroke * 0.7f)) { DashStyle = DashStyle.Custom, DashPattern = [1.6f, 1.4f] };

        void Shape(GraphicsPath path)
        {
            g.FillPath(screen, path);
            g.DrawPath(pen, path);
        }

        switch (pose)
        {
            case DuoPose.Folded:
            {
                // The closed phone: small corners at the hinge (left), large ones opposite, the hinge barrel proud of it.
                using var body = Rounded(new RectangleF(P(0.33f, 0.08f), new SizeF(S(0.42f), S(0.84f))), S(0.05f), S(0.15f));
                Shape(body);
                using var hinge = Rounded(new RectangleF(P(0.24f, 0.13f), new SizeF(S(0.07f), S(0.74f))), S(0.035f));
                using var solid = new SolidBrush(ink);
                g.FillPath(solid, hinge);
                break;
            }
            case DuoPose.Open:
            case DuoPose.OpenPortrait:
            {
                bool upright = pose == DuoPose.OpenPortrait;
                var rect = upright ? new RectangleF(P(0.24f, 0.06f), new SizeF(S(0.52f), S(0.88f))) : new RectangleF(P(0.06f, 0.24f), new SizeF(S(0.88f), S(0.52f)));
                using var body = Rounded(rect, S(0.09f));
                Shape(body);
                if (upright) g.DrawLine(crease, rect.Left + stroke, rect.Top + rect.Height / 2, rect.Right - stroke, rect.Top + rect.Height / 2);
                else g.DrawLine(crease, rect.Left + rect.Width / 2, rect.Top + stroke, rect.Left + rect.Width / 2, rect.Bottom - stroke);
                break;
            }
            case DuoPose.HalfOpen:
            {
                // An open book seen from the front: two halves angled back from a vertical spine.
                using var left = Polygon(P(0.06f, 0.22f), P(0.50f, 0.32f), P(0.50f, 0.82f), P(0.06f, 0.72f));
                using var right = Polygon(P(0.94f, 0.22f), P(0.50f, 0.32f), P(0.50f, 0.82f), P(0.94f, 0.72f));
                Shape(left);
                Shape(right);
                break;
            }
            case DuoPose.Tent:
            {
                // Tent: folded and standing, the outer display facing you, the back half leaning away behind it.
                using var back = Polygon(P(0.66f, 0.18f), P(0.92f, 0.84f), P(0.62f, 0.84f));
                using var front = Polygon(P(0.18f, 0.18f), P(0.66f, 0.18f), P(0.62f, 0.84f), P(0.08f, 0.84f));
                Shape(back);
                Shape(front);
                break;
            }
            default:
            {
                // Tabletop: the lower half flat on the table, the upper half standing, the fold across between them.
                using var upper = Polygon(P(0.22f, 0.08f), P(0.78f, 0.08f), P(0.78f, 0.54f), P(0.22f, 0.54f));
                using var lower = Polygon(P(0.22f, 0.54f), P(0.78f, 0.54f), P(0.94f, 0.88f), P(0.06f, 0.88f));
                Shape(lower);
                Shape(upper);
                break;
            }
        }
    }

    private bool OnMouse(int message, Point point)
    {
        int hit = -1;
        for (int i = 0; i < IPhoneDuo.Poses.Length; i++)
            if (ButtonRect(i).Contains(point)) hit = i;
        switch (message)
        {
            case WM_MOUSEMOVE:
                SetHover(hit);
                break;
            case WM_MOUSELEAVE:
                SetHover(-1);
                break;
            case WM_LBUTTONUP when hit >= 0 && Enabled(IPhoneDuo.Poses[hit]):
                _choose(IPhoneDuo.Poses[hit]);
                break;
        }
        return true; // never a drag handle or a menu
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        Redraw(force: false);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius) => Rounded(r, radius, radius);

    /// <summary>Rounded rectangle with one radius for the left corners and another for the right.</summary>
    private static GraphicsPath Rounded(RectangleF r, float left, float right)
    {
        left = Math.Min(left, Math.Min(r.Width, r.Height) / 2);
        right = Math.Min(right, Math.Min(r.Width, r.Height) / 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, 2 * left, 2 * left, 180, 90);
        path.AddArc(r.Right - 2 * right, r.Y, 2 * right, 2 * right, 270, 90);
        path.AddArc(r.Right - 2 * right, r.Bottom - 2 * right, 2 * right, 2 * right, 0, 90);
        path.AddArc(r.X, r.Bottom - 2 * left, 2 * left, 2 * left, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Polygon(params PointF[] points)
    {
        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    public void Dispose()
    {
        if (_window?.IsAlive == true) _window.DestroyHandle();
    }
}
