using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>Windows 11-style menus: rounded corners and highlights, icon glyphs, section headers and right-aligned hints,
/// in light or dark to match the taskbar.</summary>
internal static class MenuStyle
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    public static readonly bool RoundedCorners = Environment.OSVersion.Version.Build >= 22000;
    public static readonly string GlyphFamily = FontInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>
    /// Set only while a menu is drawn into a picture (<c>--menu-shot</c>): the theme and the scale to draw it in,
    /// instead of the taskbar's theme and the monitor's DPI. Fonts and every logical size follow the scale.
    /// </summary>
    internal static bool? PreviewDark;
    internal static double? PreviewScale;

    private static (double Scale, Font Text, Font Small, Font Header, Font Title) _fonts = MakeFonts(1);

    private static (double, Font, Font, Font, Font) MakeFonts(double scale) => (scale,
        new Font("Segoe UI", (float)(9 * scale)), new Font("Segoe UI", (float)(8.25 * scale)),
        new Font("Segoe UI Semibold", (float)(8 * scale)), new Font("Segoe UI Semibold", (float)(10 * scale)));

    private static (double Scale, Font Text, Font Small, Font Header, Font Title) Fonts
    {
        get
        {
            double scale = PreviewScale ?? 1;
            if (_fonts.Scale != scale) _fonts = MakeFonts(scale);
            return _fonts;
        }
    }

    public static Font Text => Fonts.Text;
    public static Font Small => Fonts.Small;
    public static Font HeaderFont => Fonts.Header;
    public static Font TitleFont => Fonts.Title;

    /// <summary>Gives the image column its size; the renderer paints the item's glyph there instead.</summary>
    internal static readonly Bitmap Placeholder = new(16, 16);

    private static readonly MenuRenderer Light = new(Palette.ForTheme(dark: false)), Dark = new(Palette.ForTheme(dark: true));

    /// <summary>
    /// The menu's spacing grid, in logical pixels. <see cref="Edge"/>: the side inset of row highlights, separators and
    /// blocks (tiles, the pose bar, the record button), so their edges line up. <see cref="Gap"/>: the space between
    /// stacked blocks and between tiles. <see cref="Radius"/>: the corners of a block.
    /// </summary>
    internal const int Edge = 6, Gap = 6, Radius = 6;

    /// <summary>Styles a freshly built menu and every submenu below it, picking up the current Windows theme.</summary>
    public static void Apply(ToolStripDropDown menu)
    {
        var renderer = PreviewDark ?? SystemIsDark() ? Dark : Light;
        renderer.Palette.RefreshAccent();
        Apply(menu, renderer);
    }

    private static void Apply(ToolStripDropDown menu, MenuRenderer renderer)
    {
        menu.Renderer = renderer;
        menu.Font = Text;
        menu.ShowItemToolTips = true;
        menu.Padding = new Padding(0, Scale(3, menu), 0, Scale(3, menu));
        if (RoundedCorners && !menu.IsHandleCreated) menu.DropShadowEnabled = false; // DWM draws a shadow that follows the corners
        if (menu.IsHandleCreated) RoundCorners(menu, renderer.Palette);
        else menu.HandleCreated += (_, _) => RoundCorners(menu, (menu.Renderer as MenuRenderer)?.Palette ?? renderer.Palette);

        // Room for label, gap, hint and arrow on every row; ToolStrip only reserves hint space on rows without a submenu.
        int width = 0;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent) Apply(parent.DropDown, renderer);
            if (item is MenuBlock block) width = Math.Max(width, block.MinimumWidth + Scale(4, menu));
            if (item is MenuItem { Hint: { Length: > 0 } hint })
                width = Math.Max(width, Scale(TextStart + HintGap + RightColumn, menu) +
                    TextRenderer.MeasureText(item.Text, Text).Width + TextRenderer.MeasureText(hint, Small).Width);
        }
        menu.MinimumSize = new Size(width, 0);
        menu.PerformLayout(); // stretch the rows to the new width
    }

    private static void RoundCorners(ToolStripDropDown menu, Palette palette)
    {
        if (!RoundedCorners) return;
        int corner = DWMWCP_ROUND, border = ColorTranslator.ToWin32(palette.Border);
        DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        DwmSetWindowAttribute(menu.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Logical pixels: where row labels start, the least space before a hint, and the arrow column on the right.</summary>
    internal const int TextStart = 40, HintGap = 24, RightColumn = 28;

    /// <summary>A dropdown sizes each row from its own preferred width, so rows have to ask for the menu's minimum width.</summary>
    internal static Size FullRow(Size size, ToolStripItem item) =>
        item.Owner is ToolStripDropDown { MinimumSize.Width: > 0 } menu ? size with { Width = Math.Max(size.Width, menu.MinimumSize.Width - Scale(4, menu)) } : size;

    /// <summary>Device pixels per logical pixel for a control: its monitor's DPI, or the picture's scale.</summary>
    internal static double Factor(Control control) => PreviewScale ?? control.DeviceDpi / 96.0;

    internal static int Scale(int px, ToolStripItem item) => item.Owner is { } owner ? Scale(px, owner) : px;
    internal static int Scale(int px, Control control) => (int)Math.Round(px * Factor(control));

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool FontInstalled(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class Palette
    {
        public required bool IsDark { get; init; }
        public required Color Background { get; init; }
        public required Color Hover { get; init; }
        public required Color Foreground { get; init; }
        public required Color Secondary { get; init; }
        public required Color Disabled { get; init; }
        public required Color Separator { get; init; }
        public required Color Border { get; init; }
        public required Color Good { get; init; }
        public required Color Caution { get; init; }
        /// <summary>A block's surface (tiles, the record button, the pose bar's track), and it hovered and pressed.</summary>
        public required Color Fill { get; init; }
        public required Color FillHover { get; init; }
        public required Color FillPressed { get; init; }
        /// <summary>The pose bar's selection, raised off the track like a segmented control's thumb, and its edge.</summary>
        public required Color Thumb { get; init; }
        public required Color ThumbEdge { get; init; }
        /// <summary>Recording (Apple's system red for the theme), also the tint of destructive actions.</summary>
        public required Color Record { get; init; }
        public Color Accent { get; private set; }

        public static Palette ForTheme(bool dark) => dark
            ? new()
            {
                IsDark = true, Background = Color.FromArgb(44, 44, 44), Hover = Color.FromArgb(61, 61, 61),
                Foreground = Color.FromArgb(255, 255, 255), Secondary = Color.FromArgb(160, 160, 160), Disabled = Color.FromArgb(110, 110, 110),
                Separator = Color.FromArgb(64, 64, 64), Border = Color.FromArgb(70, 70, 70),
                Good = Color.FromArgb(108, 203, 95), Caution = Color.FromArgb(252, 225, 0), Accent = Color.FromArgb(96, 205, 255),
                Fill = Color.FromArgb(56, 56, 58), FillHover = Color.FromArgb(66, 66, 69), FillPressed = Color.FromArgb(77, 77, 81),
                Thumb = Color.FromArgb(99, 99, 102), ThumbEdge = Color.FromArgb(28, 255, 255, 255),
                Record = Color.FromArgb(255, 69, 58),
            }
            : new()
            {
                IsDark = false, Background = Color.FromArgb(249, 249, 249), Hover = Color.FromArgb(234, 234, 234),
                Foreground = Color.FromArgb(26, 26, 26), Secondary = Color.FromArgb(105, 105, 105), Disabled = Color.FromArgb(160, 160, 160),
                Separator = Color.FromArgb(229, 229, 229), Border = Color.FromArgb(214, 214, 214),
                Good = Color.FromArgb(15, 123, 15), Caution = Color.FromArgb(157, 93, 0), Accent = Color.FromArgb(0, 95, 184),
                Fill = Color.FromArgb(239, 239, 241), FillHover = Color.FromArgb(230, 230, 233), FillPressed = Color.FromArgb(220, 220, 224),
                Thumb = Color.FromArgb(255, 255, 255), ThumbEdge = Color.FromArgb(30, 0, 0, 0),
                Record = Color.FromArgb(255, 59, 48),
            };

        /// <summary>The Windows accent colour, in the shade Windows itself uses for accents on this background.</summary>
        public void RefreshAccent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                // AccentPalette holds eight RGBA entries from lightest to darkest; 1 is "light 2", 4 is "dark 1".
                if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } p)
                {
                    int i = (IsDark ? 1 : 4) * 4;
                    Accent = Color.FromArgb(p[i], p[i + 1], p[i + 2]);
                }
            }
            catch
            {
                // Keep the default blue.
            }
        }
    }
}

/// <summary>The menu's geometry that is plain arithmetic, kept apart so it can be tested.</summary>
internal static class MenuLayout
{
    /// <summary>A block inside its row: <paramref name="edge"/> in from each side, half the gap above, the rest below.</summary>
    public static Rectangle Block(Size row, int edge, int gap) => new(edge, gap / 2, Math.Max(0, row.Width - 2 * edge), Math.Max(0, row.Height - gap));

    /// <summary>
    /// <paramref name="area"/> cut into <paramref name="count"/> cells side by side with <paramref name="gap"/> between
    /// them, in whole pixels: the cells differ by at most one pixel (the spare pixels go to the leftmost) and together
    /// with the gaps span the area exactly, so nothing is blurred by a half pixel at any scale.
    /// </summary>
    public static Rectangle[] Split(Rectangle area, int count, int gap)
    {
        if (count <= 0) return [];
        int room = Math.Max(0, area.Width - gap * (count - 1));
        int cell = room / count, spare = room - cell * count;
        var cells = new Rectangle[count];
        int x = area.X;
        for (int i = 0; i < count; i++)
        {
            int width = cell + (i < spare ? 1 : 0);
            cells[i] = new Rectangle(x, area.Y, width, area.Height);
            x += width + gap;
        }
        return cells;
    }

    /// <summary>
    /// The next choice from <paramref name="from"/> in the direction of <paramref name="delta"/> (an arrow key), skipping
    /// the disabled ones; it stops at the ends (a segmented control does not wrap) and stays put when there is none.
    /// </summary>
    public static int Step(int from, int delta, IReadOnlyList<bool> enabled)
    {
        int direction = Math.Sign(delta);
        if (direction == 0) return from;
        for (int i = from + direction; i >= 0 && i < enabled.Count; i += direction)
            if (enabled[i]) return i;
        return from;
    }

    /// <summary>A recording's length as a clock shows it: 0:42, 12:05, 1:02:03.</summary>
    public static string Elapsed(TimeSpan length)
    {
        if (length < TimeSpan.Zero) length = TimeSpan.Zero;
        return length.TotalHours >= 1
            ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}"
            : $"{length.Minutes}:{length.Seconds:00}";
    }
}

internal enum HintTone { Secondary, Good, Caution }

/// <summary>A menu item with an optional icon glyph and a dim right-aligned hint (a hotkey, the current value, a fit).</summary>
internal class MenuItem : ToolStripMenuItem
{
    public string? Glyph { get; }
    /// <summary>Shown in the shortcut column, so the menu reserves room for it. ToolStrip leaves that column out on items
    /// that open a submenu, so for those the renderer draws it beside the arrow and <see cref="MenuStyle"/> widens the menu.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Hint { get => ShortcutKeyDisplayString; init => ShortcutKeyDisplayString = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public HintTone HintTone { get; init; }

    public MenuItem(string text, string? glyph = null, Action? onClick = null) : base(text)
    {
        Glyph = glyph;
        if (glyph != null) Image = MenuStyle.Placeholder;
        if (onClick != null) Click += (_, _) => onClick();
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var size = base.GetPreferredSize(constrainingSize);
        if (!IsOnDropDown) return size;
        size.Height = Math.Max(size.Height, MenuStyle.Scale(26, this));
        return MenuStyle.FullRow(size, this);
    }
}

/// <summary>A small dim label that opens a group of items; not clickable.</summary>
internal sealed class MenuHeader : ToolStripMenuItem
{
    public MenuHeader(string text) : base(text) => Enabled = false;

    public override Size GetPreferredSize(Size constrainingSize) =>
        MenuStyle.FullRow(new(TextRenderer.MeasureText(Text, MenuStyle.HeaderFont).Width + MenuStyle.Scale(48, this), MenuStyle.Scale(22, this)), this);
}

/// <summary>The bold name at the top of a menu, with a dim line beneath it.</summary>
internal sealed class MenuTitle : ToolStripMenuItem
{
    public string Subtitle { get; }
    public string? Glyph { get; }

    public MenuTitle(string title, string subtitle, string? glyph = null) : base(title)
    {
        Subtitle = subtitle;
        Glyph = glyph;
        if (glyph != null) Image = MenuStyle.Placeholder;
        Enabled = false;
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var size = base.GetPreferredSize(constrainingSize);
        int text = Math.Max(TextRenderer.MeasureText(Text, MenuStyle.TitleFont).Width, TextRenderer.MeasureText(Subtitle, MenuStyle.Small).Width);
        size.Width += Math.Max(0, text - TextRenderer.MeasureText(Text, MenuStyle.Text).Width) + MenuStyle.Scale(16, this);
        size.Height = TitleFontHeight + TextRenderer.MeasureText("Ag", MenuStyle.Small).Height + MenuStyle.Scale(8, this);
        return MenuStyle.FullRow(size, this);
    }

    internal static int TitleFontHeight => TextRenderer.MeasureText("Ag", MenuStyle.TitleFont).Height;
}

/// <summary>
/// A full-width block in a menu (the action tiles, the pose bar, the record button). It is a menu item so the dropdown
/// gives it the whole row; it sits <see cref="MenuStyle.Edge"/> in from the sides, like row highlights, with half of
/// <see cref="MenuStyle.Gap"/> above and below, so stacked blocks keep one rhythm.
/// </summary>
internal abstract class MenuBlock : ToolStripMenuItem
{
    /// <summary>The least room the block's content needs, in logical pixels.</summary>
    protected abstract Size Content { get; }

    public override Size GetPreferredSize(Size constrainingSize) => MenuStyle.FullRow(new(MinimumWidth,
        MenuStyle.Scale(Content.Height + MenuStyle.Gap, this)), this);

    internal int MinimumWidth => MenuStyle.Scale(Content.Width + 2 * MenuStyle.Edge, this);

    /// <summary>Where the block draws, in item coordinates.</summary>
    protected Rectangle Area => MenuLayout.Block(Size, MenuStyle.Scale(MenuStyle.Edge, this), MenuStyle.Scale(MenuStyle.Gap, this));

    protected MenuStyle.Palette? Palette => (Owner?.Renderer as MenuRenderer)?.Palette;

    protected static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half; // whole-pixel edges land on whole pixels: crisp at every scale
    }

    protected static void Fill(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = MenuRenderer.RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Closes the whole menu chain, as a normal item click would.</summary>
    protected void CloseMenus()
    {
        var top = Owner as ToolStripDropDown;
        while (top?.OwnerItem?.Owner is ToolStripDropDown parent) top = parent;
        top?.Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    protected const TextFormatFlags Centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
}

/// <summary>One button in a <see cref="MenuTiles"/> row; a destructive one has its glyph in red.</summary>
internal sealed record Tile(string Label, string Glyph, Action OnClick, bool Enabled = true, string? Hint = null, bool Destructive = false);

/// <summary>A row of large buttons for the actions people reach for most: a glyph over a label on a quiet surface.</summary>
internal sealed class MenuTiles(params Tile[] tiles) : MenuBlock
{
    private int _hot = -1, _pressed = -1;

    private const int TileWidth = 70, TileHeight = 58;

    protected override Size Content => new(tiles.Length * TileWidth + (tiles.Length - 1) * MenuStyle.Gap, TileHeight);

    private Rectangle[] Tiles => MenuLayout.Split(Area, tiles.Length, MenuStyle.Scale(MenuStyle.Gap, this));

    private int TileAt(Point p) => Array.FindIndex(Tiles, r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(TileAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pressed = -1;
        SetHot(-1);
    }

    private void SetHot(int index)
    {
        if (index == _hot) return;
        _hot = index;
        ToolTipText = index >= 0 ? tiles[index].Hint : null; // what the hovered tile does, with its hotkey
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left ? TileAt(e.Location) : -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = TileAt(e.Location);
        bool clicked = e.Button == MouseButtons.Left && index >= 0 && index == _pressed && tiles[index].Enabled;
        _pressed = -1;
        Invalidate();
        if (!clicked) return;
        CloseMenus();
        tiles[index].OnClick();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Palette is not { } palette || Owner is null || Owner.Renderer is not MenuRenderer renderer) return;
        var g = e.Graphics;
        Prepare(g);
        int radius = MenuStyle.Scale(MenuStyle.Radius, this);
        var glyphFont = renderer.GlyphFont(Owner, 20);
        int glyphHeight = MenuStyle.Scale(24, this), labelHeight = TextRenderer.MeasureText("Ag", MenuStyle.Small).Height;
        int content = glyphHeight + MenuStyle.Scale(3, this) + labelHeight;
        var cells = Tiles;
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            var r = cells[i];
            Fill(g, r, radius, !tile.Enabled ? palette.Fill : i == _pressed && i == _hot ? palette.FillPressed : i == _hot ? palette.FillHover : palette.Fill);

            int top = r.Y + (r.Height - content) / 2;
            var ink = !tile.Enabled ? palette.Disabled : tile.Destructive ? palette.Record : palette.Foreground;
            TextRenderer.DrawText(g, tile.Glyph, glyphFont, new Rectangle(r.X, top, r.Width, glyphHeight), ink, Centre);
            TextRenderer.DrawText(g, tile.Label, MenuStyle.Small, new Rectangle(r.X, top + glyphHeight + MenuStyle.Scale(3, this), r.Width, labelHeight),
                tile.Enabled ? palette.Foreground : palette.Disabled, Centre | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>One choice in a <see cref="MenuPoseBar"/>.</summary>
internal sealed record PoseChoice(DuoPose Pose, string Label, bool Enabled, string Tip);

/// <summary>
/// The iPhone Duo's poses as one horizontal selector, like a segmented control: each pose is the device silhouette the
/// pose panel draws, with a short label, and the current one sits on a raised thumb. Click a pose, or move with the
/// arrow keys and press Enter, to switch to it; the menu closes.
/// </summary>
internal sealed class MenuPoseBar(IReadOnlyList<PoseChoice> choices, DuoPose? current, Action<DuoPose> choose) : MenuBlock
{
    private int _hot = -1, _pressed = -1, _key = -1;

    private const int TrackPadding = 2, SegmentWidth = 56, SegmentHeight = 50, TrackRadius = 8;

    protected override Size Content => new(choices.Count * SegmentWidth + 2 * TrackPadding, SegmentHeight + 2 * TrackPadding);

    private int Current => choices.Select(c => c.Pose).ToList().IndexOf(current ?? (DuoPose)(-1));

    private Rectangle[] Segments => MenuLayout.Split(Rectangle.Inflate(Area, -MenuStyle.Scale(TrackPadding, this), -MenuStyle.Scale(TrackPadding, this)), choices.Count, 0);

    private int SegmentAt(Point p) => Array.FindIndex(Segments, r => r.Contains(p));

    /// <summary>Keyboard focus is on the bar (it is selected and the mouse is not over it): the thumb follows the arrows.</summary>
    private bool KeyboardActive => Selected && _hot < 0 && _pressed < 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = SegmentAt(e.Location);
        if (index == _hot) return;
        _hot = index;
        _key = -1;
        ToolTipText = index >= 0 ? choices[index].Tip : null;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = _pressed = -1;
        ToolTipText = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left ? SegmentAt(e.Location) : -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = SegmentAt(e.Location);
        bool clicked = e.Button == MouseButtons.Left && index >= 0 && index == _pressed;
        _pressed = -1;
        Invalidate();
        if (clicked) Apply(index);
    }

    /// <summary>The menu gives the selected item the first look at a key: arrows move the thumb, Enter switches.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        var enabled = choices.Select(c => c.Enabled).ToList();
        int from = _key >= 0 ? _key : Current;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
                int next = MenuLayout.Step(from < 0 ? (keyData & Keys.KeyCode) == Keys.Left ? choices.Count : -1 : from,
                    (keyData & Keys.KeyCode) == Keys.Left ? -1 : 1, enabled);
                if (next >= 0 && next < choices.Count) _key = next;
                ToolTipText = _key >= 0 ? choices[_key].Tip : null;
                Invalidate();
                return true;
            case Keys.Enter:
            case Keys.Space:
                if (from >= 0) Apply(from);
                return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    private void Apply(int index)
    {
        if (index < 0 || index >= choices.Count || !choices[index].Enabled) return;
        CloseMenus();
        if (choices[index].Pose != current) choose(choices[index].Pose);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Palette is not { } palette) return;
        var g = e.Graphics;
        Prepare(g);
        var track = Area;
        int trackRadius = MenuStyle.Scale(TrackRadius, this), radius = trackRadius - MenuStyle.Scale(TrackPadding, this);
        Fill(g, track, trackRadius, palette.Fill);
        if (KeyboardActive)
        {
            using var path = MenuRenderer.RoundedRect(Rectangle.Inflate(track, -1, -1), trackRadius - 1);
            using var ring = new Pen(palette.Accent, MenuStyle.Scale(2, this));
            g.DrawPath(ring, path);
        }

        var segments = Segments;
        int selected = KeyboardActive && _key >= 0 ? _key : Current;
        for (int i = 0; i < choices.Count; i++)
        {
            var r = segments[i];
            var choice = choices[i];
            if (i == selected)
            {
                // The thumb: raised off the track by a hairline edge and, in light, a soft shadow beneath.
                if (!palette.IsDark)
                {
                    var shadow = r with { Y = r.Y + MenuStyle.Scale(1, this) };
                    Fill(g, shadow, radius, Color.FromArgb(18, 0, 0, 0));
                }
                Fill(g, r, radius, palette.Thumb);
                using var path = MenuRenderer.RoundedRect(r, radius);
                using var edge = new Pen(palette.ThumbEdge, 1);
                g.DrawPath(edge, path);
            }
            else if (choice.Enabled && i == _hot)
            {
                Fill(g, r, radius, i == _pressed ? palette.FillPressed : palette.FillHover);
            }

            var ink = !choice.Enabled ? palette.Disabled : i == selected ? palette.Foreground : palette.Secondary;
            int icon = MenuStyle.Scale(24, this), labelHeight = TextRenderer.MeasureText("Ag", MenuStyle.Small).Height;
            int top = r.Y + (r.Height - icon - labelHeight) / 2;
            var box = new RectangleF(r.X + (r.Width - icon) / 2f, top, icon, icon);
            DuoPosePanel.DrawIcon(g, choice.Pose, box, ink, lit: i == selected);
            TextRenderer.DrawText(g, choice.Label, MenuStyle.Small, new Rectangle(r.X, top + icon, r.Width, labelHeight), ink, Centre | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>
/// "Record entire frame" as a full-width button with a red record dot and the padding as quiet text; while a recording
/// runs it is "Stop recording" with a stop square and the time so far, kept current while the menu is open.
/// </summary>
internal sealed class MenuRecordButton : MenuBlock
{
    private readonly DateTime? _since;
    private readonly string _detail;
    private System.Windows.Forms.Timer? _clock;

    private const int ButtonHeight = 34, Inset = 12, Mark = 10, MinWidth = 240;

    public MenuRecordButton(DateTime? recordingSince, string detail, Action record, Action stop)
    {
        _since = recordingSince;
        _detail = detail;
        Text = recordingSince != null ? "Stop recording" : "Record entire frame";
        Click += (_, _) => (recordingSince != null ? stop : record)();
    }

    public bool Recording => _since != null;

    protected override Size Content => new(MinWidth, ButtonHeight);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clock?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Palette is not { } palette) return;
        var g = e.Graphics;
        Prepare(g);
        var r = Area;
        Fill(g, r, MenuStyle.Scale(MenuStyle.Radius, this), !Enabled ? palette.Fill : Pressed ? palette.FillPressed : Selected ? palette.FillHover : palette.Fill);

        int inset = MenuStyle.Scale(Inset, this), mark = MenuStyle.Scale(Mark, this);
        var markBox = new Rectangle(r.X + inset, r.Y + (r.Height - mark) / 2, mark, mark);
        var red = Enabled ? palette.Record : palette.Disabled;
        using (var brush = new SolidBrush(red))
        {
            if (Recording)
            {
                using var square = MenuRenderer.RoundedRect(markBox, Math.Max(1, mark / 4));
                g.FillPath(brush, square);
            }
            else g.FillEllipse(brush, markBox);
        }

        const TextFormatFlags line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int textLeft = markBox.Right + MenuStyle.Scale(10, this);
        string detail = Recording ? MenuLayout.Elapsed(DateTime.Now - _since!.Value) : _detail;
        var detailColor = !Enabled ? palette.Disabled : Recording ? palette.Record : palette.Secondary;
        int detailWidth = TextRenderer.MeasureText(detail, MenuStyle.Small).Width;
        var detailBox = new Rectangle(r.Right - inset - detailWidth, r.Y, detailWidth, r.Height);
        TextRenderer.DrawText(g, Text, MenuStyle.Text, Rectangle.FromLTRB(textLeft, r.Y, detailBox.Left - MenuStyle.Scale(8, this), r.Bottom),
            Enabled ? palette.Foreground : palette.Disabled, line | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, detail, MenuStyle.Small, detailBox, detailColor, line | TextFormatFlags.Right);

        // While recording, tick the time over for as long as the menu is on screen.
        if (Recording && MenuStyle.PreviewScale == null && _clock == null)
        {
            _clock = new System.Windows.Forms.Timer { Interval = 500 };
            _clock.Tick += (_, _) =>
            {
                if (Owner is { Visible: true }) Invalidate();
                else
                {
                    _clock?.Dispose();
                    _clock = null;
                }
            };
            _clock.Start();
        }
    }
}

internal sealed class MenuSeparator : ToolStripSeparator
{
    public override Size GetPreferredSize(Size constrainingSize) => MenuStyle.FullRow(new(base.GetPreferredSize(constrainingSize).Width, MenuStyle.Scale(7, this)), this);
}

internal sealed class MenuRenderer(MenuStyle.Palette palette) : ToolStripProfessionalRenderer
{
    private const string CheckGlyph = "", ChevronGlyph = ""; // Segoe Fluent Icons: CheckMark, ChevronRight
    private readonly Dictionary<(double Factor, float Size), Font> _glyphFonts = [];

    public MenuStyle.Palette Palette { get; } = palette;

    internal Font GlyphFont(Control owner, float px)
    {
        var key = (MenuStyle.Factor(owner), px);
        if (!_glyphFonts.TryGetValue(key, out var font))
            _glyphFonts[key] = font = new Font(MenuStyle.GlyphFamily, (float)(px * key.Item1), GraphicsUnit.Pixel);
        return font;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Palette.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (MenuStyle.RoundedCorners) return; // DWM draws the rounded border
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
        int inset = MenuStyle.Scale(MenuStyle.Edge, e.Item);
        var bounds = new Rectangle(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var path = RoundedRect(bounds, MenuStyle.Scale(4, e.Item));
        using var brush = new SolidBrush(Palette.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int inset = MenuStyle.Scale(MenuStyle.Edge, e.Item), y = e.Item.Height / 2;
        using var pen = new Pen(Palette.Separator);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    /// <summary>
    /// A row's own offsets (icon column, text start) are laid out by WinForms for the menu's DPI. In a picture at
    /// another scale (<see cref="MenuStyle.PreviewScale"/>) the menu is laid out at the monitor's DPI, so they are
    /// scaled here the way WinForms scales them on a monitor at that scale.
    /// </summary>
    private static Rectangle Scaled(Rectangle r)
    {
        if (MenuStyle.PreviewScale is not { } scale || scale == 1) return r;
        int x = (int)Math.Round(r.X * scale);
        return new Rectangle(x, r.Y, Math.Max(0, r.Width - (x - r.X)), r.Height);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        DrawGlyph(e.Graphics, e.Item, CheckGlyph, Column(e), e.Item.Enabled ? Palette.Accent : Palette.Disabled, 14);

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item is ToolStripMenuItem { Checked: true }) return;
        string? glyph = e.Item switch { MenuItem m => m.Glyph, MenuTitle t => t.Glyph, _ => null };
        if (glyph == null) { base.OnRenderItemImage(e); return; }
        var color = e.Item is MenuTitle ? Palette.Accent : e.Item.Enabled ? Palette.Foreground : Palette.Disabled;
        DrawGlyph(e.Graphics, e.Item, glyph, Column(e), color, e.Item is MenuTitle ? 20 : 16);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item == null) return;
        // Pinned to the right edge: ToolStrip computes the arrow's place before the menu is widened for hints.
        int column = MenuStyle.Scale(MenuStyle.RightColumn, e.Item);
        var arrow = new Rectangle(e.Item.Width - column, 0, column - MenuStyle.Scale(6, e.Item), e.Item.Height);
        DrawGlyph(e.Graphics, e.Item, ChevronGlyph, arrow, e.Item.Enabled ? Palette.Secondary : Palette.Disabled, 10);
        if (e.Item is MenuItem { Hint: { Length: > 0 } hint } item) DrawHint(e.Graphics, item, hint);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        var g = e.Graphics;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        switch (item)
        {
            case MenuHeader:
            {
                var textRect = Scaled(e.TextRectangle);
                var rect = new Rectangle(textRect.X, 0, item.Width - textRect.X, item.Height - MenuStyle.Scale(2, item));
                TextRenderer.DrawText(g, item.Text, MenuStyle.HeaderFont, rect, Palette.Secondary, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return;
            }
            case MenuTitle title:
            {
                int top = MenuStyle.Scale(4, item), titleHeight = MenuTitle.TitleFontHeight;
                var textRect = Scaled(e.TextRectangle);
                var line1 = new Rectangle(textRect.X, top, item.Width - textRect.X - MenuStyle.Scale(8, item), titleHeight);
                var line2 = line1 with { Y = top + titleHeight, Height = item.Height - top - titleHeight };
                TextRenderer.DrawText(g, title.Text, MenuStyle.TitleFont, line1, Palette.Foreground, left);
                TextRenderer.DrawText(g, title.Subtitle, MenuStyle.Small, line2, Palette.Secondary, left & ~TextFormatFlags.VerticalCenter);
                return;
            }
        }

        // The item's text rectangles are laid out for a shorter row; centre them on the real one.
        var area = Scaled(e.TextRectangle) with { Y = 0, Height = item.Height };

        // ToolStrip draws the shortcut column through here too, which is where hints live.
        if (item is ToolStripMenuItem menuItem && e.Text != menuItem.Text)
        {
            if (!string.IsNullOrEmpty(e.Text)) DrawHint(g, item, e.Text);
            return;
        }

        TextRenderer.DrawText(g, e.Text, e.TextFont ?? MenuStyle.Text, area, item.Enabled ? Palette.Foreground : Palette.Disabled, left);
    }

    /// <summary>Right-aligned against the arrow column, so every hint in a menu lines up.</summary>
    private void DrawHint(Graphics g, ToolStripItem item, string hint)
    {
        var area = new Rectangle(0, 0, item.Width - MenuStyle.Scale(MenuStyle.RightColumn, item), item.Height);
        var tone = (item as MenuItem)?.HintTone ?? HintTone.Secondary;
        var color = !item.Enabled ? Palette.Disabled : tone switch
        {
            HintTone.Good => Palette.Good,
            HintTone.Caution => Palette.Caution,
            _ => Palette.Secondary,
        };
        TextRenderer.DrawText(g, hint, MenuStyle.Small, area, color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    /// <summary>The icon column of the row, full height and wide enough for a large glyph.</summary>
    private static Rectangle Column(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        if (MenuStyle.PreviewScale is { } scale && scale != 1)
        {
            // See Scaled: the image column where and as wide as WinForms makes it at that scale.
            r = new Rectangle((int)Math.Round(r.X * scale), r.Y, (int)Math.Round(r.Width * scale), r.Height);
        }
        int pad = MenuStyle.Scale(4, e.Item);
        return Rectangle.FromLTRB(r.Left - pad, 0, r.Right + pad, e.Item.Height);
    }

    private void DrawGlyph(Graphics g, ToolStripItem item, string glyph, Rectangle area, Color color, float px)
    {
        if (item.Owner is not { } owner) return;
        TextRenderer.DrawText(g, glyph, GlyphFont(owner, px), area, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
