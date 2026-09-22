using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>
/// Cuts an emulator window down to the phone's screen shape, and undoes it again.
///
/// A window region alone is not enough: the desktop compositor keeps painting the window's drop shadow and 1px border
/// around the *unclipped* rectangle, which shows up as a ghost box around the phone (and dirties a chroma-key
/// background). Turning DWM non-client rendering off removes both. As a side effect the theming layer then installs a
/// region of its own, so ours has to go in after it and be re-checked — see <see cref="IsIntact"/>.
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
        return GetWindowRgnBox(hwnd, out var box) is not (ERROR or NULLREGION) ? box : null;
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
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        int hr = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        if (hr < 0) Log.Write($"DwmSetWindowAttribute({attribute}) failed: 0x{hr:X8}");
    }
}
