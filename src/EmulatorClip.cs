using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>
/// Cuts an emulator window down to the phone's screen shape, and undoes it again.
///
/// A window region alone is not enough: the desktop compositor keeps painting the window's drop shadow and 1px border
/// around the *unclipped* rectangle, which shows up as a ghost box around the phone (and dirties a chroma-key
/// background). Turning DWM non-client rendering off removes both. As a side effect the theming layer then installs a
/// region of its own, so ours has to go in after it and be re-checked (see <see cref="IsIntact"/>).
/// </summary>
internal static class EmulatorClip
{
    private const int DWMWA_NCRENDERING_POLICY = 2, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const int DWMNCRP_USEWINDOWSTYLE = 0, DWMNCRP_DISABLED = 1;
    private const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_DEFAULT = -1, DWMWA_COLOR_NONE = -2;
    private const int NULLREGION = 1, ERROR = 0;

    /// <summary>Applies the clip. Returns the region's bounding box (window coordinates) for later integrity checks.</summary>
    public static RECT? Apply(IntPtr hwnd, IReadOnlyList<(int Y, int X0, int X1)> runsInWindowCoords)
    {
        SetAttribute(hwnd, DWMWA_NCRENDERING_POLICY, DWMNCRP_DISABLED);
        SetAttribute(hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
        SetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND);

        IntPtr region = CreateRegionFromRuns(runsInWindowCoords);
        if (region == IntPtr.Zero) return null;
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region); // on success the system owns the region
            return null;
        }
        PlacePictureAgain(hwnd);
        return GetWindowRgnBox(hwnd, out var box) is not (ERROR or NULLREGION) ? box : null;
    }

    /// <summary>
    /// Hides the window while something is drawn in its place (the fold animation), all but one pixel at
    /// <paramref name="keep"/> (window coordinates) that the drawing covers. With nothing visible at all the emulator
    /// stops presenting and its picture, which the animation still needs, goes black.
    /// </summary>
    public static void Hide(IntPtr hwnd, Point keep)
    {
        SetAttribute(hwnd, DWMWA_NCRENDERING_POLICY, DWMNCRP_DISABLED);
        SetAttribute(hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
        IntPtr dot = CreateRectRgn(keep.X, keep.Y, keep.X + 1, keep.Y + 1);
        if (SetWindowRgn(hwnd, dot, true) == 0) DeleteObject(dot);
        else PlacePictureAgain(hwnd);
    }

    /// <summary>True while the window still carries the region we installed (nobody replaced or removed it).</summary>
    public static bool IsIntact(IntPtr hwnd, RECT expected)
    {
        if (GetWindowRgnBox(hwnd, out var box) is ERROR or NULLREGION) return false;
        return box.Left == expected.Left && box.Top == expected.Top && box.Right == expected.Right && box.Bottom == expected.Bottom;
    }

    /// <summary>Back to how MuMu created the window: no region, normal shadow, border and rounded corners.</summary>
    public static void Remove(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        SetWindowRgn(hwnd, IntPtr.Zero, true);
        SetAttribute(hwnd, DWMWA_NCRENDERING_POLICY, DWMNCRP_USEWINDOWSTYLE);
        SetAttribute(hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_DEFAULT);
        SetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DEFAULT);
        PlacePictureAgain(hwnd);
    }

    /// <summary>
    /// MuMu lays its picture out again whenever its window region changes, and gets it 4 px too far left (a gap at the
    /// right, the left edge under the bezel) until the window next moves. That is what a drag used to fix. Every region
    /// change here is followed by what the drag does: a 1 px move and straight back, which MuMu answers by placing the
    /// picture right. Measured with PrintWindow: a frame change moves the picture from 78 to 74 px about 24 ms later, the
    /// move puts it back to 78. More frame changes can follow ours (the theming layer answers a DWM change with a region
    /// of its own), so the session repeats this a little later too (<see cref="EmulatorSession"/>).
    /// </summary>
    public static void PlacePictureAgain(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        SetWindowPos(hwnd, IntPtr.Zero, r.Left + 1, r.Top, 0, 0, flags);
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0, flags);
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        int hr = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        if (hr < 0) Log.Write($"DwmSetWindowAttribute({attribute}) failed: 0x{hr:X8}");
    }
}
