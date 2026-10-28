namespace MuMuDeviceFrames;

/// <summary>
/// MuMu's window around its picture (the render child): about 4 px either side, its title bar and toolbar above, a few
/// px below. When the picture changes shape and MuMu does not resize the window, it centres the smaller picture in the
/// old window instead (letterboxed) until the window is resized by hand. This tells that apart and works out the
/// window that fits the picture again.
/// </summary>
internal static class WindowFit
{
    public const int Side = 4, Below = 3, Slack = 12;

    /// <param name="left">Picture's distance from the window's left edge; likewise the others.</param>
    public static bool IsLetterboxed(int left, int right, int top, int bottom) =>
        Horizontal(left, right) || Vertical(bottom);

    // Centred between wide side margins (a side toolbar would make them unequal, which is not letterboxing).
    private static bool Horizontal(int left, int right) => left > Slack && right > Slack && Math.Abs(left - right) <= 2;
    private static bool Vertical(int bottom) => bottom > Slack;

    /// <summary>
    /// The window that fits <paramref name="picture"/>'s shape: the picture area keeps its longer side (so a quarter
    /// turn swaps the window round at the same scale), smaller if that would not fit <paramref name="work"/>, centred
    /// where the window was and kept on the screen.
    /// </summary>
    public static Rectangle Fit(Rectangle window, Size picture, int left, int right, int top, int bottom, Rectangle work)
    {
        // The window's own chrome, from the side the picture still touches.
        int chromeW = Horizontal(left, right) ? 2 * Side : left + right;
        int chromeH = Vertical(bottom) ? top - bottom + 2 * Below : top + bottom;
        int areaW = window.Width - chromeW, areaH = window.Height - chromeH;
        double longest = Math.Max(areaW, areaH), aspect = (double)picture.Width / picture.Height;
        double w = aspect < 1 ? longest * aspect : longest, h = aspect < 1 ? longest : longest / aspect;
        double shrink = Math.Min(1, Math.Min((work.Width - chromeW) / w, (work.Height - chromeH) / h));
        int width = (int)Math.Round(w * shrink) + chromeW, height = (int)Math.Round(h * shrink) + chromeH;
        int x = window.X + window.Width / 2 - width / 2, y = window.Y + window.Height / 2 - height / 2;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new Rectangle(x, y, width, height);
    }
}
