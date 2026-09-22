using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>
/// Per-pixel-alpha window that shows the device frame. It is *owned* by the emulator's top-level window, so the window
/// manager keeps it directly above the emulator in z-order (never topmost) and minimises/restores it together with it.
/// </summary>
internal sealed class OverlayWindow : NativeWindow
{
    private readonly IntPtr _emulator;
    private bool _dragging;
    private POINT _dragCursor, _dragEmulator, _dragOverlay;
    private Point _position;
    private bool _visible;

    public event Action? MenuRequested;
    public event Action? DoubleClicked;
    public bool IsAlive => Handle != IntPtr.Zero;

    public OverlayWindow(IntPtr emulator, bool interactive)
    {
        _emulator = emulator;
        uint ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (!interactive) ex |= WS_EX_TRANSPARENT;
        CreateHandle(new CreateParams
        {
            Caption = "MuMuDeviceFrame",
            ClassStyle = 0x0008, // CS_DBLCLKS, otherwise WM_LBUTTONDBLCLK never arrives
            Style = unchecked((int)WS_POPUP),
            ExStyle = unchecked((int)ex),
            Parent = emulator, // for a popup this is the owner
            X = 0, Y = 0, Width = 1, Height = 1,
        });
    }

    public void SetFrame(RenderedFrame frame, Point position)
    {
        if (!IsAlive) return;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER { Size = 40, Width = frame.Width, Height = -frame.Height, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) { Log.Write("CreateDIBSection failed"); return; }
            System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, bits, frame.Pixels.Length);
            IntPtr old = SelectObject(memDc, dib);
            var dst = new POINT { X = position.X, Y = position.Y };
            var size = new SIZE { Cx = frame.Width, Cy = frame.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA))
                Log.Write($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            SelectObject(memDc, old);
            _position = position;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void MoveTo(Point position)
    {
        if (!IsAlive || position == _position) return;
        _position = position;
        SetWindowPos(Handle, IntPtr.Zero, position.X, position.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void SetVisible(bool visible)
    {
        if (!IsAlive || visible == _visible) return;
        _visible = visible;
        ShowWindow(Handle, visible ? SW_SHOWNA : SW_HIDE);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;

            case WM_LBUTTONDOWN:
                BeginDrag();
                return;

            case WM_MOUSEMOVE when _dragging:
                ContinueDrag();
                return;

            case WM_LBUTTONUP:
                if (_dragging) { _dragging = false; ReleaseCapture(); }
                return;

            case WM_CAPTURECHANGED:
                _dragging = false;
                break;

            case WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke();
                return;

            case WM_RBUTTONUP:
                MenuRequested?.Invoke();
                return;

            case WM_NCDESTROY:
                // Happens on its own when the emulator (our owner) goes away.
                _visible = false;
                break;
        }
        base.WndProc(ref m);
    }

    // The clipped emulator has no title bar left to grab, so the bezel is the handle. Both windows move in one
    // DeferWindowPos batch, which keeps the frame locked to the picture while dragging.
    private void BeginDrag()
    {
        SetForegroundWindow(_emulator);
        if (!GetCursorPos(out _dragCursor) || !GetWindowRect(_emulator, out var er) || !GetWindowRect(Handle, out var or)) return;
        _dragEmulator = new POINT { X = er.Left, Y = er.Top };
        _dragOverlay = new POINT { X = or.Left, Y = or.Top };
        _dragging = true;
        SetCapture(Handle);
    }

    private void ContinueDrag()
    {
        if (!GetCursorPos(out var cursor)) return;
        int dx = cursor.X - _dragCursor.X, dy = cursor.Y - _dragCursor.Y;
        var overlayPos = new Point(_dragOverlay.X + dx, _dragOverlay.Y + dy);
        if (overlayPos == _position) return;

        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        IntPtr batch = BeginDeferWindowPos(2);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, _emulator, IntPtr.Zero, _dragEmulator.X + dx, _dragEmulator.Y + dy, 0, 0, flags);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, Handle, IntPtr.Zero, overlayPos.X, overlayPos.Y, 0, 0, flags);
        if (batch == IntPtr.Zero || !EndDeferWindowPos(batch))
        {
            SetWindowPos(_emulator, IntPtr.Zero, _dragEmulator.X + dx, _dragEmulator.Y + dy, 0, 0, flags);
            SetWindowPos(Handle, IntPtr.Zero, overlayPos.X, overlayPos.Y, 0, 0, flags);
        }
        _position = overlayPos;
    }
}
