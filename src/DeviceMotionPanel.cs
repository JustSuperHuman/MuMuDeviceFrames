using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>
/// Floating side controls beside the device while Alt is held: repeatable Shake on top and one-click Reset to frame
/// beneath it, separate from the recorded picture and its touch targets.
/// </summary>
internal sealed class DeviceMotionPanel : IDisposable
{
    private const int Width = 60, ShakeHeight = 72, Gap = 8, ResetHeight = 62, TotalHeight = ShakeHeight + Gap + ResetHeight;
    private const int Tip = 250, TipGap = 10;

    private readonly OverlayWindow _window;
    private readonly Action _shake;
    private readonly Action _reset;
    private float _scale;
    private bool _left, _shakeEnabled, _resetEnabled, _resetting;
    private int _hover = -1, _pressed = -1; // -1 none, 0 Shake, 1 Reset
    private int _strength;
    private (bool Left, int Hover, int Pressed, bool ShakeEnabled, bool ResetEnabled, bool Resetting, int Strength, float Scale)? _drawn;

    public DeviceMotionPanel(IntPtr owner, Action shake, Action? reset = null)
    {
        _shake = shake;
        _reset = reset ?? (() => { });
        _window = new OverlayWindow(owner, interactive: true) { Mouse = OnMouse };
    }

    private DeviceMotionPanel()
    {
        _window = null!;
        _shake = () => { };
        _reset = () => { };
    }

    internal static (byte[] Pixels, Size Size) Preview(float scale, int strength, bool hover) =>
        Preview(scale, strength, hover ? 0 : -1);

    internal static (byte[] Pixels, Size Size) Preview(float scale, int strength, int hoverButton, bool resetting = false)
    {
        var panel = new DeviceMotionPanel
        {
            _scale = scale,
            _shakeEnabled = true,
            _resetEnabled = !resetting,
            _resetting = resetting,
            _strength = strength,
            _hover = hoverButton,
        };
        var size = new Size(panel.Px(Width + TipGap + Tip), panel.Px(TotalHeight));
        return (panel.Render(size), size);
    }

    public OverlayWindow Window => _window;
    private int Px(float value) => (int)Math.Round(value * _scale);
    private int ButtonX => _left ? Px(Tip + TipGap) : 0;
    private Rectangle ShakeBox => new(ButtonX, 0, Px(Width), Px(ShakeHeight));
    private Rectangle ResetBox => new(ButtonX, Px(ShakeHeight + Gap), Px(Width), Px(ResetHeight));

    public void Show(Rectangle device, bool belowPoses, float scale, bool canMove, int strength, bool canReset = true, bool resetting = false)
    {
        _scale = scale;
        _shakeEnabled = canMove;
        _resetEnabled = canReset && !resetting;
        _resetting = resetting;
        _strength = strength;

        var work = Screen.FromRectangle(device).WorkingArea;
        int right = device.Right + Px(18), width = Px(Width), height = Px(TotalHeight);
        bool onLeft = right + width > work.Right;
        _left = right + Px(Width + TipGap + Tip) > work.Right;
        int x = onLeft ? device.Left - Px(18) - width : right;
        int y = belowPoses ? device.Top + device.Height / 2 + Px(DuoPosePanel.LogicalHeight / 2 + 10)
            : device.Top + (device.Height - height) / 2;
        x = Math.Clamp(x, work.Left + Px(4), Math.Max(work.Left + Px(4), work.Right - width - Px(4)));
        y = Math.Clamp(y, work.Top + Px(4), Math.Max(work.Top + Px(4), work.Bottom - height - Px(4)));
        var position = new Point(x - (_left ? Px(Tip + TipGap) : 0), y);

        var state = (_left, _hover, _pressed, _shakeEnabled, _resetEnabled, _resetting, _strength, _scale);
        if (_drawn != state)
        {
            _drawn = state;
            var size = new Size(Px(Width + TipGap + Tip), height);
            _window.SetBitmap(Render(size), size.Width, size.Height, position);
        }
        else _window.MoveTo(position);
        _window.SetVisible(true);
    }

    private byte[] Render(Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 8.5f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            using var centre = new StringFormat { Alignment = StringAlignment.Center };

            // 1. Shake card
            DrawShakeCard(g, ShakeBox, font, centre);

            // 2. Reset card
            DrawResetCard(g, ResetBox, font, centre);

            // Hover tip card beside whichever button is hovered
            if (_hover == 0)
            {
                DrawTipCard(g, ShakeBox,
                    _strength > 0 ? $"Shake · strength {_strength}/5" : "Shake device",
                    "Tap again to increase strength.\nCtrl+Shift+Alt+drag to tilt.");
            }
            else if (_hover == 1)
            {
                DrawTipCard(g, ResetBox,
                    _resetting ? "Resetting device…" : "Reset to frame",
                    "Fixes orientation, screen size,\nwindow fit and safe areas.");
            }
        }
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size.Width * 4, size.Width * 4);
        bitmap.UnlockBits(data);
        return pixels;
    }

    private void DrawShakeCard(Graphics g, Rectangle box, Font font, StringFormat centre)
    {
        bool hover = _hover == 0 && _shakeEnabled, pressed = _pressed == 0 && hover;
        DrawCardSurface(g, box, hover, pressed);

        var ink = Color.FromArgb(_shakeEnabled ? 245 : 90, 240, 240, 244);
        using var pen = new Pen(ink, Math.Max(1.4f, _scale * 1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var phone = MenuRenderer.RoundedRect(new Rectangle(box.X + Px(23), box.Y + Px(10), Px(14), Px(24)), Px(3));
        g.DrawPath(pen, phone);
        foreach (int sign in new[] { -1, 1 })
        {
            float x = box.X + Px(sign < 0 ? 14 : 43);
            g.DrawLines(pen,
            [
                new PointF(x, box.Y + Px(13)),
                new PointF(x + Px(2) * sign, box.Y + Px(18)),
                new PointF(x, box.Y + Px(23)),
                new PointF(x + Px(2) * sign, box.Y + Px(28)),
            ]);
        }
        using var brush = new SolidBrush(ink);
        g.DrawString("Shake", font, brush, new RectangleF(box.X, box.Y + Px(38), box.Width, Px(20)), centre);
        for (int i = 0; i < 5; i++)
        {
            using var bar = new SolidBrush(Color.FromArgb(_shakeEnabled && i < _strength ? 240 : 65, 225, 225, 234));
            g.FillRectangle(bar, box.X + Px(15 + i * 6), box.Y + Px(62), Px(4), Px(3));
        }
    }

    private void DrawResetCard(Graphics g, Rectangle box, Font font, StringFormat centre)
    {
        bool hover = _hover == 1 && _resetEnabled, pressed = _pressed == 1 && hover;
        DrawCardSurface(g, box, hover, pressed);

        var ink = Color.FromArgb(_resetEnabled ? 245 : 95, 240, 240, 244);
        using var pen = new Pen(ink, Math.Max(1.4f, _scale * 1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Circular clockwise reset arc with arrowhead anchored at the exact 245° endpoint
        var arcRect = new RectangleF(box.X + 20f * _scale, box.Y + 9f * _scale, 20f * _scale, 20f * _scale);
        g.DrawArc(pen, arcRect, -45f, 290f);
        float tipX = box.X + 25.77f * _scale, tipY = box.Y + 9.94f * _scale;
        g.DrawLines(pen,
        [
            new PointF(tipX - 4.6f * _scale, tipY - 0.9f * _scale),
            new PointF(tipX, tipY),
            new PointF(tipX - 1.3f * _scale, tipY + 4.5f * _scale),
        ]);

        using var brush = new SolidBrush(ink);
        g.DrawString(_resetting ? "Busy" : "Reset", font, brush, new RectangleF(box.X, box.Y + Px(35), box.Width, Px(20)), centre);
    }

    private void DrawCardSurface(Graphics g, Rectangle box, bool hover, bool pressed)
    {
        using var body = MenuRenderer.RoundedRect(Rectangle.Inflate(box, -Px(1), -Px(1)), Px(18));
        using var fill = new SolidBrush(Color.FromArgb(240, pressed ? 57 : hover ? 43 : 24, pressed ? 57 : hover ? 43 : 24, pressed ? 63 : hover ? 49 : 28));
        using var edge = new Pen(Color.FromArgb(200, 96, 96, 102), Math.Max(1f, _scale));
        g.FillPath(fill, body);
        g.DrawPath(edge, body);
    }

    private void DrawTipCard(Graphics g, Rectangle anchorBox, string heading, string bodyText)
    {
        int tipH = Px(70);
        int tipY = Math.Clamp(anchorBox.Top + (anchorBox.Height - tipH) / 2, Px(1), Px(TotalHeight) - tipH - Px(1));
        var tip = new Rectangle(_left ? Px(2) : anchorBox.Right + Px(TipGap), tipY, Px(Tip - 4), tipH);
        using var path = MenuRenderer.RoundedRect(tip, Px(9));
        using var background = new SolidBrush(Color.FromArgb(245, 24, 24, 27));
        using var border = new Pen(Color.FromArgb(190, 96, 96, 102), Math.Max(1f, _scale));
        g.FillPath(background, path);
        g.DrawPath(border, path);

        using var white = new SolidBrush(Color.FromArgb(250, 245, 245, 247));
        using var grey = new SolidBrush(Color.FromArgb(230, 175, 175, 184));
        using var title = new Font("Segoe UI Semibold", 9f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        using var detail = new Font("Segoe UI", 8f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        g.DrawString(heading, title, white, tip.X + Px(10), tip.Y + Px(6));
        g.DrawString(bodyText, detail, grey, new RectangleF(tip.X + Px(10), tip.Y + Px(27), tip.Width - Px(20), Px(40)));
    }

    internal static int HitTestPreview(float scale, Point point)
    {
        var panel = new DeviceMotionPanel { _scale = scale, _shakeEnabled = true, _resetEnabled = true };
        return panel.HitTest(point);
    }

    internal int HitTest(Point point)
    {
        if (ShakeBox.Contains(point)) return 0;
        if (ResetBox.Contains(point)) return 1;
        return -1;
    }

    private bool IsButtonEnabled(int index) => index switch
    {
        0 => _shakeEnabled,
        1 => _resetEnabled,
        _ => false,
    };

    private bool OnMouse(int message, Point point)
    {
        int hit = message == WM_MOUSELEAVE ? -1 : HitTest(point);
        if (message is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            _pressed = IsButtonEnabled(hit) ? hit : -1;

        if (message == WM_LBUTTONUP)
        {
            int clicked = hit >= 0 && hit == _pressed && IsButtonEnabled(hit) ? hit : -1;
            _pressed = -1;
            if (clicked == 0) _shake();
            else if (clicked == 1) _reset();
        }
        if (message == WM_MOUSELEAVE) _pressed = -1;

        if (hit != _hover || message is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK)
        {
            _hover = hit;
            _drawn = null;
            var at = _window.Position;
            var size = new Size(Px(Width + TipGap + Tip), Px(TotalHeight));
            _window.SetBitmap(Render(size), size.Width, size.Height, at);
        }
        return true;
    }

    public void Hide()
    {
        _hover = _pressed = -1;
        _drawn = null;
        _window.SetVisible(false);
    }

    public void Dispose()
    {
        if (_window.IsAlive) _window.DestroyHandle();
    }
}
