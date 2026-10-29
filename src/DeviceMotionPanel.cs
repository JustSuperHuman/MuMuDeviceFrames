using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>A repeatable Shake button beside the device, separate from the recorded picture and its touch targets.</summary>
internal sealed class DeviceMotionPanel : IDisposable
{
    private const int Width = 60, Height = 72, Tip = 250, TipGap = 10;
    private readonly OverlayWindow _window;
    private readonly Action _shake;
    private float _scale;
    private bool _left, _hover, _pressed, _enabled;
    private int _strength;
    private (bool Left, bool Hover, bool Pressed, bool Enabled, int Strength, float Scale)? _drawn;

    public DeviceMotionPanel(IntPtr owner, Action shake)
    {
        _shake = shake;
        _window = new OverlayWindow(owner, interactive: true) { Mouse = OnMouse };
    }

    private DeviceMotionPanel() { _window = null!; _shake = () => { }; }

    internal static (byte[] Pixels, Size Size) Preview(float scale, int strength, bool hover)
    {
        var panel = new DeviceMotionPanel { _scale = scale, _enabled = true, _strength = strength, _hover = hover };
        var size = new Size(panel.Px(Width + TipGap + Tip), panel.Px(Height));
        return (panel.Render(size), size);
    }

    public OverlayWindow Window => _window;
    private int Px(float value) => (int)Math.Round(value * _scale);
    private Rectangle Button => new(_left ? Px(Tip + TipGap) : 0, 0, Px(Width), Px(Height));

    public void Show(Rectangle device, bool belowPoses, float scale, bool enabled, int strength)
    {
        _scale = scale; _enabled = enabled; _strength = strength;
        var work = Screen.FromRectangle(device).WorkingArea;
        int right = device.Right + Px(18), width = Px(Width);
        bool onLeft = right + width > work.Right;
        _left = right + Px(Width + TipGap + Tip) > work.Right;
        int x = onLeft ? device.Left - Px(18) - width : right;
        int y = belowPoses ? device.Top + device.Height / 2 + Px(DuoPosePanel.LogicalHeight / 2 + 10)
            : device.Top + (device.Height - Px(Height)) / 2;
        x = Math.Clamp(x, work.Left + Px(4), Math.Max(work.Left + Px(4), work.Right - width - Px(4)));
        y = Math.Clamp(y, work.Top + Px(4), Math.Max(work.Top + Px(4), work.Bottom - Px(Height) - Px(4)));
        var position = new Point(x - (_left ? Px(Tip + TipGap) : 0), y);
        var state = (_left, _hover, _pressed, _enabled, _strength, _scale);
        if (_drawn != state)
        {
            _drawn = state;
            var size = new Size(Px(Width + TipGap + Tip), Px(Height));
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
            var box = Button;
            using var body = MenuRenderer.RoundedRect(Rectangle.Inflate(box, -Px(1), -Px(1)), Px(18));
            using var fill = new SolidBrush(Color.FromArgb(240, _pressed ? 57 : _hover ? 43 : 24, _pressed ? 57 : _hover ? 43 : 24, _pressed ? 63 : _hover ? 49 : 28));
            using var edge = new Pen(Color.FromArgb(200, 96, 96, 102), Math.Max(1, _scale));
            g.FillPath(fill, body); g.DrawPath(edge, body);
            var ink = Color.FromArgb(_enabled ? 245 : 90, 240, 240, 244);
            using var pen = new Pen(ink, Math.Max(1.4f, _scale * 1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var phone = MenuRenderer.RoundedRect(new Rectangle(box.X + Px(23), Px(10), Px(14), Px(24)), Px(3));
            g.DrawPath(pen, phone);
            foreach (int sign in new[] { -1, 1 })
            {
                float x = box.X + Px(sign < 0 ? 14 : 43);
                g.DrawLines(pen, [new PointF(x, Px(13)), new PointF(x + Px(2) * sign, Px(18)), new PointF(x, Px(23)), new PointF(x + Px(2) * sign, Px(28))]);
            }
            using var font = new Font("Segoe UI", 8.5f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(ink);
            using var centre = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("Shake", font, brush, new RectangleF(box.X, Px(38), box.Width, Px(20)), centre);
            for (int i = 0; i < 5; i++)
            {
                using var bar = new SolidBrush(Color.FromArgb(_enabled && i < _strength ? 240 : 65, 225, 225, 234));
                g.FillRectangle(bar, box.X + Px(15 + i * 6), Px(62), Px(4), Px(3));
            }
            if (_hover)
            {
                var tip = new Rectangle(_left ? Px(2) : box.Right + Px(TipGap), Px(1), Px(Tip - 4), Px(Height - 2));
                using var path = MenuRenderer.RoundedRect(tip, Px(9));
                using var background = new SolidBrush(Color.FromArgb(245, 24, 24, 27));
                g.FillPath(background, path);
                using var title = new Font("Segoe UI Semibold", 9f * _scale, FontStyle.Regular, GraphicsUnit.Point);
                using var detail = new Font("Segoe UI", 8f * _scale, FontStyle.Regular, GraphicsUnit.Point);
                g.DrawString(_strength > 0 ? $"Shake · strength {_strength}/5" : "Shake device", title, brush, tip.X + Px(10), Px(6));
                g.DrawString("Tap again to increase strength.\nCtrl+Shift+Alt+drag to tilt.", detail, brush,
                    new RectangleF(tip.X + Px(10), Px(28), tip.Width - Px(20), Px(40)));
            }
        }
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size.Width * 4, size.Width * 4);
        bitmap.UnlockBits(data);
        return pixels;
    }

    private bool OnMouse(int message, Point point)
    {
        bool hit = Button.Contains(point);
        bool hover = message != WM_MOUSELEAVE && hit;
        if (message is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK) _pressed = hit && _enabled;
        if (message == WM_LBUTTONUP)
        {
            bool click = _pressed && hit && _enabled;
            _pressed = false;
            if (click) _shake();
        }
        if (message == WM_MOUSELEAVE) _pressed = false;
        if (hover != _hover || message is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK)
        {
            _hover = hover;
            _drawn = null;
            // The session's timer will also refresh the strength while it decays.
            var at = _window.Position;
            var size = new Size(Px(Width + TipGap + Tip), Px(Height));
            _window.SetBitmap(Render(size), size.Width, size.Height, at);
        }
        return true;
    }

    public void Hide() { _hover = _pressed = false; _drawn = null; _window.SetVisible(false); }
    public void Dispose() { if (_window.IsAlive) _window.DestroyHandle(); }
}
