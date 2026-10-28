namespace MuMuDeviceFrames;

/// <summary>Where the device sits on screen, kept per emulator: the centre of its frame, in screen pixels.</summary>
internal sealed record AnchorPoint(int X, int Y)
{
    public Point ToPoint() => new(X, Y);
}

/// <summary>
/// Keeps the device put. A pose or orientation change gives the device a new size and MuMu moves its own window as it
/// turns, restores a saved position on launch, and so on; the device should not wander with it. Its centre on screen
/// is the anchor: every layout puts the frame's centre there, and only the user dragging the device changes it.
/// </summary>
internal static class DeviceAnchor
{
    /// <summary>
    /// The frame's place on screen: centred on <paramref name="anchor"/>, then moved the least it takes to lie fully on
    /// <paramref name="work"/> (the anchor's monitor without the taskbar). A frame bigger than the work area keeps its
    /// top-left corner on it.
    /// </summary>
    public static Rectangle Place(Size frame, Point anchor, Rectangle work)
    {
        int x = anchor.X - frame.Width / 2, y = anchor.Y - frame.Height / 2;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - frame.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - frame.Height));
        return new Rectangle(x, y, frame.Width, frame.Height);
    }

    public static Point Centre(Rectangle frame) => new(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);

    /// <summary>
    /// The emulator picture size at which the whole device fits <paramref name="work"/>, when at
    /// <paramref name="picture"/> its frame (<paramref name="frame"/>, which scales with the picture) does not; null
    /// when it already fits. The frame scales with MuMu's window, so a device bigger than the screen (an iPhone 8 with
    /// its forehead and chin at MuMu's default window size) gets a smaller window rather than hanging off the screen.
    /// </summary>
    public static Size? PictureToFit(Size picture, Size frame, Rectangle work)
    {
        if (frame.Width <= work.Width && frame.Height <= work.Height) return null;
        double scale = Math.Min((double)work.Width / frame.Width, (double)work.Height / frame.Height);
        return new Size((int)Math.Floor(picture.Width * scale), (int)Math.Floor(picture.Height * scale));
    }
}
