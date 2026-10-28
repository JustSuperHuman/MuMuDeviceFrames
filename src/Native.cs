using System.Runtime.InteropServices;
using System.Text;

namespace MuMuDeviceFrames;

internal static class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x00000020;

    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    public const int SW_HIDE = 0, SW_SHOWNA = 8;

    public const int WM_NCDESTROY = 0x0082, WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CAPTURECHANGED = 0x0215, WM_HOTKEY = 0x0312, WM_SYSCOMMAND = 0x0112;
    public const int WM_COPYDATA = 0x004A, WM_MOUSELEAVE = 0x02A3;
    public const uint TME_LEAVE = 0x00000002;
    public const int MA_NOACTIVATE = 3;
    public const int SC_MINIMIZE = 0xF020;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint ULW_ALPHA = 0x00000002;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public IntPtr DataId; public int ByteCount; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] public static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool EndDeferWindowPos(IntPtr info);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int processId);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    /// <summary>Changes only the window-wide opacity of a layered window, keeping its bitmap (for fades).</summary>
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")]
    public static extern bool UpdateLayeredWindowAlpha(IntPtr hwnd, IntPtr dstDc, IntPtr dst, IntPtr size, IntPtr srcDc, IntPtr src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, int msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr ExtCreateRegion(IntPtr xform, uint count, byte[] data);

    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr hwnd, out RECT box);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? className, string? windowName);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IntPtr FindChildByClass(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (GetClassName(h) != className) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Builds a GDI region from horizontal runs (y, x0, x1-exclusive). Identical consecutive rows are merged.</summary>
    public static IntPtr CreateRegionFromRuns(IReadOnlyList<(int Y, int X0, int X1)> runs)
    {
        var rects = new List<RECT>(runs.Count);
        foreach (var (y, x0, x1) in runs)
        {
            if (x1 <= x0) continue;
            if (rects.Count > 0)
            {
                var last = rects[^1];
                if (last.Left == x0 && last.Right == x1 && last.Bottom == y)
                {
                    last.Bottom = y + 1;
                    rects[^1] = last;
                    continue;
                }
            }
            rects.Add(new RECT { Left = x0, Top = y, Right = x1, Bottom = y + 1 });
        }

        // RGNDATAHEADER (32 bytes) followed by the RECT array.
        var data = new byte[32 + rects.Count * 16];
        var bounds = new RECT { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
        foreach (var r in rects)
        {
            bounds.Left = Math.Min(bounds.Left, r.Left); bounds.Top = Math.Min(bounds.Top, r.Top);
            bounds.Right = Math.Max(bounds.Right, r.Right); bounds.Bottom = Math.Max(bounds.Bottom, r.Bottom);
        }
        if (rects.Count == 0) bounds = default;

        var span = MemoryMarshal.Cast<byte, int>(data.AsSpan());
        span[0] = 32;               // dwSize
        span[1] = 1;                // RDH_RECTANGLES
        span[2] = rects.Count;      // nCount
        span[3] = rects.Count * 16; // nRgnSize
        span[4] = bounds.Left; span[5] = bounds.Top; span[6] = bounds.Right; span[7] = bounds.Bottom;
        for (int i = 0; i < rects.Count; i++)
        {
            span[8 + i * 4] = rects[i].Left; span[9 + i * 4] = rects[i].Top;
            span[10 + i * 4] = rects[i].Right; span[11 + i * 4] = rects[i].Bottom;
        }
        return ExtCreateRegion(IntPtr.Zero, (uint)data.Length, data);
    }
}
