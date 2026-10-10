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

    public static readonly Font Text = new("Segoe UI", 9f);
    public static readonly Font Small = new("Segoe UI", 8.25f);
    public static readonly Font HeaderFont = new("Segoe UI Semibold", 8f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 10f);

    /// <summary>Gives the image column its size; the renderer paints the item's glyph there instead.</summary>
    internal static readonly Bitmap Placeholder = new(16, 16);

    private static readonly MenuRenderer Light = new(Palette.ForTheme(dark: false)), Dark = new(Palette.ForTheme(dark: true));

    /// <summary>Styles a freshly built menu and every submenu below it, picking up the current Windows theme.</summary>
    public static void Apply(ToolStripDropDown menu)
    {
        var renderer = SystemIsDark() ? Dark : Light;
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
            if (item is MenuTiles tiles) width = Math.Max(width, tiles.GetPreferredSize(Size.Empty).Width + Scale(4, menu));
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

    internal static int Scale(int px, ToolStripItem item) => item.Owner is { } owner ? Scale(px, owner) : px;
    internal static int Scale(int px, Control control) => (int)Math.Round(px * control.DeviceDpi / 96.0);

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
        public Color Accent { get; private set; }

        public static Palette ForTheme(bool dark) => dark
            ? new()
            {
                IsDark = true, Background = Color.FromArgb(44, 44, 44), Hover = Color.FromArgb(61, 61, 61),
                Foreground = Color.FromArgb(255, 255, 255), Secondary = Color.FromArgb(160, 160, 160), Disabled = Color.FromArgb(110, 110, 110),
                Separator = Color.FromArgb(64, 64, 64), Border = Color.FromArgb(70, 70, 70),
                Good = Color.FromArgb(108, 203, 95), Caution = Color.FromArgb(252, 225, 0), Accent = Color.FromArgb(96, 205, 255),
            }
            : new()
            {
                IsDark = false, Background = Color.FromArgb(249, 249, 249), Hover = Color.FromArgb(234, 234, 234),
                Foreground = Color.FromArgb(26, 26, 26), Secondary = Color.FromArgb(105, 105, 105), Disabled = Color.FromArgb(160, 160, 160),
                Separator = Color.FromArgb(229, 229, 229), Border = Color.FromArgb(214, 214, 214),
                Good = Color.FromArgb(15, 123, 15), Caution = Color.FromArgb(157, 93, 0), Accent = Color.FromArgb(0, 95, 184),
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

/// <summary>One big coloured button in a <see cref="MenuTiles"/> row.</summary>
internal sealed record Tile(string Label, string Glyph, Color Light, Color Dark, Action OnClick, bool Enabled = true, string? Hint = null);

/// <summary>A row of large, colour-coded buttons for the actions people reach for most.</summary>
internal sealed class MenuTiles : ToolStripMenuItem // a menu item so the dropdown gives it the full row, not the text column
{
    private readonly Tile[] _tiles;
    private int _hot = -1;

    public static readonly Color Blue = Color.FromArgb(0, 103, 192), BlueDark = Color.FromArgb(96, 180, 255);
    public static readonly Color Green = Color.FromArgb(16, 124, 16), GreenDark = Color.FromArgb(108, 203, 95);
    public static readonly Color Orange = Color.FromArgb(202, 80, 16), OrangeDark = Color.FromArgb(255, 145, 70);
    public static readonly Color Purple = Color.FromArgb(136, 23, 152), PurpleDark = Color.FromArgb(205, 140, 255);
    public static readonly Color Red = Color.FromArgb(196, 43, 28), RedDark = Color.FromArgb(255, 120, 120);

    public MenuTiles(params Tile[] tiles) => _tiles = tiles;

    public override Size GetPreferredSize(Size constrainingSize) =>
        new(MenuStyle.Scale(Margin * 2 + _tiles.Length * TileWidth + (_tiles.Length - 1) * Gap, this), MenuStyle.Scale(TileHeight + Margin * 2, this));

    private const int TileWidth = 70, TileHeight = 66, Gap = 6, Margin = 6;

    private Rectangle TileBounds(int index)
    {
        int margin = MenuStyle.Scale(Margin, this), gap = MenuStyle.Scale(Gap, this);
        int width = (Width - margin * 2 - gap * (_tiles.Length - 1)) / _tiles.Length;
        return new Rectangle(margin + index * (width + gap), margin, width, Height - margin * 2);
    }

    private int TileAt(Point p)
    {
        for (int i = 0; i < _tiles.Length; i++)
            if (TileBounds(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(TileAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHot(-1);
    }

    private void SetHot(int index)
    {
        if (index == _hot) return;
        _hot = index;
        ToolTipText = index >= 0 ? _tiles[index].Hint : null; // what the hovered tile does, with its hotkey
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = TileAt(e.Location);
        if (e.Button != MouseButtons.Left || index < 0 || !_tiles[index].Enabled) return;
        // Close the whole menu chain, as a normal item click would.
        var top = Owner as ToolStripDropDown;
        while (top?.OwnerItem?.Owner is ToolStripDropDown parent) top = parent;
        top?.Close(ToolStripDropDownCloseReason.ItemClicked);
        _tiles[index].OnClick();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Owner?.Renderer is not MenuRenderer renderer) return;
        var palette = renderer.Palette;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = MenuStyle.Scale(6, this);
        var glyphFont = renderer.GlyphFont(Owner, 22);

        for (int i = 0; i < _tiles.Length; i++)
        {
            var tile = _tiles[i];
            var r = TileBounds(i);
            var hue = !tile.Enabled ? palette.Disabled : palette.IsDark ? tile.Dark : tile.Light;
            int alpha = !tile.Enabled ? 14 : i == _hot ? (palette.IsDark ? 64 : 48) : (palette.IsDark ? 34 : 24);
            using (var path = MenuRenderer.RoundedRect(r, radius))
            using (var fill = new SolidBrush(Color.FromArgb(alpha, hue)))
                g.FillPath(fill, path);

            int glyphTop = r.Y + MenuStyle.Scale(9, this), glyphHeight = MenuStyle.Scale(26, this);
            const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, tile.Glyph, glyphFont, new Rectangle(r.X, glyphTop, r.Width, glyphHeight), hue, centre);

            int labelTop = glyphTop + glyphHeight + MenuStyle.Scale(2, this);
            int labelHeight = TextRenderer.MeasureText("Ag", MenuStyle.Small).Height;
            var labelColor = tile.Enabled ? palette.Foreground : palette.Disabled;
            TextRenderer.DrawText(g, tile.Label, MenuStyle.Small, new Rectangle(r.X, labelTop, r.Width, labelHeight), labelColor, centre | TextFormatFlags.EndEllipsis);
        }
    }
}

internal sealed class MenuSeparator : ToolStripSeparator
{
    public override Size GetPreferredSize(Size constrainingSize) => MenuStyle.FullRow(new(base.GetPreferredSize(constrainingSize).Width, MenuStyle.Scale(7, this)), this);
}

internal sealed class MenuRenderer(MenuStyle.Palette palette) : ToolStripProfessionalRenderer
{
    private const string CheckGlyph = "", ChevronGlyph = "";
    private readonly Dictionary<(int Dpi, float Size), Font> _glyphFonts = [];

    public MenuStyle.Palette Palette { get; } = palette;

    internal Font GlyphFont(Control owner, float px)
    {
        var key = (owner.DeviceDpi, px);
        if (!_glyphFonts.TryGetValue(key, out var font))
            _glyphFonts[key] = font = new Font(MenuStyle.GlyphFamily, px * owner.DeviceDpi / 96f, GraphicsUnit.Pixel);
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
        int inset = MenuStyle.Scale(4, e.Item);
        var bounds = new Rectangle(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(bounds, MenuStyle.Scale(4, e.Item));
        using var brush = new SolidBrush(Palette.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int inset = MenuStyle.Scale(4, e.Item), y = e.Item.Height / 2;
        using var pen = new Pen(Palette.Separator);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
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
                var rect = new Rectangle(e.TextRectangle.X, 0, item.Width - e.TextRectangle.X, item.Height - MenuStyle.Scale(2, item));
                TextRenderer.DrawText(g, item.Text, MenuStyle.HeaderFont, rect, Palette.Secondary, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return;
            }
            case MenuTitle title:
            {
                int top = MenuStyle.Scale(4, item), titleHeight = MenuTitle.TitleFontHeight;
                var line1 = new Rectangle(e.TextRectangle.X, top, item.Width - e.TextRectangle.X - MenuStyle.Scale(8, item), titleHeight);
                var line2 = line1 with { Y = top + titleHeight, Height = item.Height - top - titleHeight };
                TextRenderer.DrawText(g, title.Text, MenuStyle.TitleFont, line1, Palette.Foreground, left);
                TextRenderer.DrawText(g, title.Subtitle, MenuStyle.Small, line2, Palette.Secondary, left & ~TextFormatFlags.VerticalCenter);
                return;
            }
        }

        // The item's text rectangles are laid out for a shorter row; centre them on the real one.
        var area = e.TextRectangle with { Y = 0, Height = item.Height };

        // ToolStrip draws the shortcut column through here too, which is where hints live.
        if (item is ToolStripMenuItem menuItem && e.Text != menuItem.Text)
        {
            DrawHint(g, item, e.Text);
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
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
