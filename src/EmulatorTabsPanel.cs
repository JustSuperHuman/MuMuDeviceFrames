using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

internal enum TabDeviceKind { Phone, PhoneLandscape, Tablet, TabletLandscape, Duo }

internal readonly record struct EmulatorTab(IntPtr Main, string Title, TabDeviceKind Kind, DuoPose? DuoPose, bool Active);

/// <summary>
/// A horizontal smoked-glass pill above the grouped emulator frame for switching between running emulators. Styled to
/// match the side pose and motion pills: dark translucent glass, a 1 px titanium rim, and full rounded pill selection
/// (no edge accent stripes). Owned by the active emulator window so it shares its exact z-order layer and moves with
/// bezel drags in one <c>DeferWindowPos</c> batch.
/// </summary>
internal sealed class EmulatorTabsPanel : IDisposable
{
    public const float PillHeightLogical = 40, TabHeightLogical = 30, PadLogical = 5, GapLogical = 4;
    private const float MinTabWidth = 72, MaxTabWidth = 168, CompactMinTabWidth = 48, BaseGapAboveFrame = 14, TopScreenMargin = 6;

    private readonly OverlayWindow _window;
    private readonly Action<IntPtr> _select;
    private readonly Action<IntPtr> _openMenu;
    private EmulatorTab[] _tabs = [];
    private RectangleF[] _tabBoxes = [];
    private float _scale = 1f;
    private int _hover = -1, _pressed = -1;
    private string? _drawnSignature;

    public EmulatorTabsPanel(IntPtr owner, Action<IntPtr> select, Action<IntPtr> openMenu)
    {
        _select = select;
        _openMenu = openMenu;
        _window = new OverlayWindow(owner, interactive: true) { Mouse = OnMouse };
    }

    private EmulatorTabsPanel()
    {
        _window = null!;
        _select = _ => { };
        _openMenu = _ => { };
    }

    public OverlayWindow Window => _window;
    public bool IsAlive => _window.IsAlive;

    /// <summary>
    /// Vertical pixels above the rendered frame bitmap reserved on the monitor's work area when the tab bar is shown,
    /// keeping the pill clear of ShareX's <c>RecordingBounds</c> and inside the top edge of the display.
    /// </summary>
    public static int GapAboveFrame(float scale, int recordingPadding) =>
        Math.Max(Px(BaseGapAboveFrame, scale), Math.Clamp(recordingPadding, 0, 48) + Px(4, scale));

    public static int ReservedHeight(float scale, int recordingPadding) =>
        Px(PillHeightLogical, scale) + GapAboveFrame(scale, recordingPadding) + Px(TopScreenMargin, scale);

    /// <summary>Offline render for tests and diagnostics: premultiplied BGRA bytes and pixel dimensions.</summary>
    internal static (byte[] Pixels, Size Size, RectangleF[] Boxes) Preview(IReadOnlyList<EmulatorTab> tabs, float scale, int hover = -1, int maxWidth = 1200)
    {
        var panel = new EmulatorTabsPanel { _tabs = [.. tabs], _scale = scale, _hover = hover };
        var (pillSize, boxes) = LayoutTabs(panel._tabs, scale, maxWidth);
        panel._tabBoxes = boxes;
        return (panel.Render(pillSize), pillSize, boxes);
    }

    public void Show(Rectangle device, Point overlayTopLeft, IReadOnlyList<EmulatorTab> tabs, float scale, int recordingPadding)
    {
        if (!_window.IsAlive || tabs.Count < 2)
        {
            Hide();
            return;
        }
        _scale = scale;
        _tabs = [.. tabs];
        if (_hover >= _tabs.Length) _hover = -1;
        if (_pressed >= _tabs.Length) _pressed = -1;

        var work = Screen.FromRectangle(device).WorkingArea;
        int maxWidth = Math.Max(Px(160, scale), work.Width - Px(16, scale));
        var (pillSize, boxes) = LayoutTabs(_tabs, scale, maxWidth);
        _tabBoxes = boxes;

        var location = Place(device, overlayTopLeft, pillSize, work, scale, recordingPadding);
        string signature = BuildSignature(pillSize);
        if (_drawnSignature != signature)
        {
            _drawnSignature = signature;
            _window.SetBitmap(Render(pillSize), pillSize.Width, pillSize.Height, location);
        }
        else _window.MoveTo(location);

        _window.SetVisible(true);
    }

    public void Hide()
    {
        _hover = _pressed = -1;
        _drawnSignature = null;
        if (_window?.IsAlive == true) _window.SetVisible(false);
    }

    internal static Point Place(Rectangle device, Point overlayTopLeft, Size pillSize, Rectangle work, float scale, int recordingPadding)
    {
        int x = device.Left + (device.Width - pillSize.Width) / 2;
        int marginX = Px(8, scale), marginY = Px(4, scale);
        x = Math.Clamp(x, work.Left + marginX, Math.Max(work.Left + marginX, work.Right - pillSize.Width - marginX));

        int frameTop = Math.Min(device.Top, overlayTopLeft.Y);
        int gap = GapAboveFrame(scale, recordingPadding);
        int y = Math.Max(work.Top + marginY, frameTop - gap - pillSize.Height);
        return new Point(x, y);
    }

    /// <summary>Computes the pill size and whole-pixel tab boxes for a set of tabs within <paramref name="maxWidth"/>.</summary>
    internal static (Size PillSize, RectangleF[] Boxes) LayoutTabs(IReadOnlyList<EmulatorTab> tabs, float scale, int maxWidth)
    {
        int pad = Px(PadLogical, scale), gap = Px(GapLogical, scale);
        int tabH = Px(TabHeightLogical, scale), pillH = tabH + 2 * pad;
        if (tabs.Count == 0) return (new Size(2 * pad, pillH), []);

        int minW = Px(MinTabWidth, scale), capW = Px(MaxTabWidth, scale), compactMin = Px(CompactMinTabWidth, scale);
        int chromeW = Px(10 + 15 + 6 + 10, scale);
        var widths = new int[tabs.Count];

        using (var measureBmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(measureBmp))
        using (var activeFont = new Font("Segoe UI Semibold", 9f * scale, FontStyle.Regular, GraphicsUnit.Point))
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                int textW = (int)Math.Ceiling(g.MeasureString(tabs[i].Title, activeFont, 1000, StringFormat.GenericTypographic).Width);
                widths[i] = Math.Clamp(chromeW + textW + Px(2, scale), minW, capW);
            }
        }

        int totalGaps = gap * (tabs.Count - 1);
        int budget = Math.Max(tabs.Count * compactMin, maxWidth - 2 * pad - totalGaps);
        int sum = widths.Sum();
        if (sum > budget && sum > 0)
        {
            double ratio = (double)budget / sum;
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Max(compactMin, (int)Math.Floor(widths[i] * ratio));
        }

        var boxes = new RectangleF[tabs.Count];
        int x = pad;
        for (int i = 0; i < tabs.Count; i++)
        {
            boxes[i] = new RectangleF(x, pad, widths[i], tabH);
            x += widths[i] + (i < tabs.Count - 1 ? gap : 0);
        }
        return (new Size(x + pad, pillH), boxes);
    }

    private string BuildSignature(Size size) =>
        $"{size.Width}x{size.Height}|{_scale:0.###}|h{_hover}|p{_pressed}|" +
        string.Join(';', _tabs.Select(t => $"{t.Main}:{t.Title}:{(int)t.Kind}:{t.DuoPose}:{(t.Active ? 1 : 0)}"));

    private static int Px(float logical, float scale) => (int)Math.Round(logical * scale);
    private int Px(float logical) => Px(logical, _scale);

    private byte[] Render(Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float inset = Math.Max(0.5f, _scale * 0.5f);
            var pill = new RectangleF(inset, inset, Math.Max(2, size.Width - inset * 2), Math.Max(2, size.Height - inset * 2));
            using (var path = Rounded(pill, pill.Height / 2f))
            {
                using var fill = new LinearGradientBrush(pill, Color.FromArgb(234, 34, 34, 38), Color.FromArgb(234, 14, 14, 16), 90f);
                g.FillPath(fill, path);
                using var edge = new Pen(Color.FromArgb(205, 96, 96, 102), Math.Max(1f, _scale));
                g.DrawPath(edge, path);
            }

            using var activeFont = new Font("Segoe UI Semibold", 9f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            using var idleFont = new Font("Segoe UI", 9f * _scale, FontStyle.Regular, GraphicsUnit.Point);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            for (int i = 0; i < _tabs.Length; i++)
            {
                var tab = _tabs[i];
                var box = _tabBoxes[i];
                bool on = tab.Active, hot = i == _hover, down = i == _pressed && hot;

                if (on || hot)
                {
                    var highlightRect = RectangleF.Inflate(box, -Px(0.5f), -Px(0.5f));
                    using var highlight = Rounded(highlightRect, highlightRect.Height / 2f);
                    int fillAlpha = on ? 62 : down ? 44 : 28;
                    using var brush = new SolidBrush(Color.FromArgb(fillAlpha, 255, 255, 255));
                    g.FillPath(brush, highlight);
                    if (on)
                    {
                        using var tabEdge = new Pen(Color.FromArgb(52, 255, 255, 255), Math.Max(1f, _scale));
                        g.DrawPath(tabEdge, highlight);
                    }
                }

                int inkAlpha = on ? 255 : hot ? 235 : 188;
                var ink = Color.FromArgb(inkAlpha, 242, 242, 246);
                float iconSize = Px(15), iconGap = Px(6), sidePad = Px(10);
                var font = on ? activeFont : idleFont;
                float measuredTextW = g.MeasureString(tab.Title, font, 1000, StringFormat.GenericTypographic).Width;
                float maxContentW = Math.Max(0, box.Width - sidePad * 2);
                float contentW = Math.Min(maxContentW, iconSize + iconGap + measuredTextW);
                float startX = box.X + (box.Width - contentW) / 2f;

                var iconBox = new RectangleF(startX, box.Y + (box.Height - iconSize) / 2f, iconSize, iconSize);
                DrawTabIcon(g, tab, iconBox, ink, on);

                float textLeft = iconBox.Right + iconGap;
                float textWidth = Math.Max(0, box.Right - sidePad - textLeft + Px(2));
                if (textWidth > 2)
                {
                    var textRect = new RectangleF(textLeft, box.Y - 0.5f * _scale, textWidth, box.Height);
                    using var textBrush = new SolidBrush(ink);
                    g.DrawString(tab.Title, font, textBrush, textRect, format);
                }
            }
        }

        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size.Width * 4, size.Width * 4);
        bitmap.UnlockBits(data);
        return pixels;
    }

    private static void DrawTabIcon(Graphics g, EmulatorTab tab, RectangleF box, Color ink, bool lit)
    {
        if (tab.Kind == TabDeviceKind.Duo)
        {
            DuoPosePanel.DrawIcon(g, tab.DuoPose ?? DuoPose.Folded, box, ink, lit);
            return;
        }
        float stroke = Math.Max(1.15f, box.Width / 12.5f);
        using var pen = new Pen(ink, stroke) { LineJoin = LineJoin.Round };
        using var screen = new SolidBrush(Color.FromArgb(ink.A * (lit ? 72 : 38) / 255, ink));

        RectangleF body = tab.Kind switch
        {
            TabDeviceKind.PhoneLandscape => new RectangleF(box.X + box.Width * 0.08f, box.Y + box.Height * 0.26f, box.Width * 0.84f, box.Height * 0.48f),
            TabDeviceKind.Tablet => new RectangleF(box.X + box.Width * 0.18f, box.Y + box.Height * 0.10f, box.Width * 0.64f, box.Height * 0.80f),
            TabDeviceKind.TabletLandscape => new RectangleF(box.X + box.Width * 0.10f, box.Y + box.Height * 0.18f, box.Width * 0.80f, box.Height * 0.64f),
            _ => new RectangleF(box.X + box.Width * 0.26f, box.Y + box.Height * 0.08f, box.Width * 0.48f, box.Height * 0.84f),
        };
        using var path = Rounded(body, Math.Min(body.Width, body.Height) * 0.24f);
        g.FillPath(screen, path);
        g.DrawPath(pen, path);
    }

    private bool OnMouse(int message, Point point)
    {
        int hit = -1;
        for (int i = 0; i < _tabBoxes.Length; i++)
            if (_tabBoxes[i].Contains(point)) { hit = i; break; }

        int prevHover = _hover, prevPressed = _pressed;
        switch (message)
        {
            case WM_MOUSEMOVE:
                _hover = hit;
                break;
            case WM_MOUSELEAVE:
                _hover = _pressed = -1;
                break;
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK:
                _hover = hit;
                _pressed = hit;
                break;
            case WM_LBUTTONUP:
                bool click = hit >= 0 && hit == _pressed && hit < _tabs.Length;
                _pressed = -1;
                if (click) _select(_tabs[hit].Main);
                break;
            case WM_RBUTTONUP:
                _pressed = -1;
                if (hit >= 0 && hit < _tabs.Length) _openMenu(_tabs[hit].Main);
                break;
        }

        if (_hover != prevHover || _pressed != prevPressed)
        {
            _drawnSignature = null;
            if (_window.Bitmap is { } current)
                _window.SetBitmap(Render(current.Size), current.Size.Width, current.Size.Height, _window.Position);
        }
        return true;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_window?.IsAlive == true) _window.DestroyHandle();
    }
}
