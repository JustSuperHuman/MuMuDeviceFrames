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

    /// <summary>The user is dragging the device by its bezel (the emulator moves with it).</summary>
    public bool IsDragging => _dragging;

    /// <summary>A bezel drag just ended.</summary>
    public event Action? DragEnded;
    public bool IsAlive => Handle != IntPtr.Zero;
    public bool IsVisible => _visible;

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

    /// <summary>Other owned windows (the status-bar band, the glass layer) that move together with this one while dragging.</summary>
    public OverlayWindow?[] Companions { get; set; } = [];

    public void SetFrame(RenderedFrame frame, Point position, byte opacity = 255) => SetBitmap(frame.Pixels, frame.Width, frame.Height, position, opacity);

    /// <summary>
    /// Mouse messages (WM_MOUSEMOVE, WM_LBUTTONDOWN/UP, WM_MOUSELEAVE) with the point in window coordinates, for windows
    /// that are controls rather than a bezel. Returning true means handled: no dragging, no menu.
    /// </summary>
    public Func<int, Point, bool>? Mouse { get; set; }

    public byte Opacity { get; private set; } = 255;
    internal (byte[] Pixels, Size Size)? Bitmap { get; private set; }

    /// <summary>Window-wide opacity, keeping the bitmap: what a cross-fade animates.</summary>
    public void SetOpacity(byte opacity)
    {
        if (!IsAlive || opacity == Opacity) return;
        Opacity = opacity;
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
        UpdateLayeredWindowAlpha(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
    }

    /// <summary>Shows a premultiplied BGRA bitmap at the given screen position.</summary>
    public void SetBitmap(byte[] premultiplied, int width, int height, Point position, byte opacity = 255)
    {
        if (!IsAlive) return;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) { Log.Write("CreateDIBSection failed"); return; }
            System.Runtime.InteropServices.Marshal.Copy(premultiplied, 0, bits, width * height * 4);
            IntPtr old = SelectObject(memDc, dib);
            var dst = new POINT { X = position.X, Y = position.Y };
            var size = new SIZE { Cx = width, Cy = height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            Opacity = opacity;
            if (!UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA))
                Log.Write($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            SelectObject(memDc, old);
            _position = position;
            Bitmap = (premultiplied, new Size(width, height));
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public Point Position => _position;

    /// <summary>The window really is shown (or hidden) and placed as this object last set it.</summary>
    public bool IsAsSet()
    {
        if (!IsAlive) return true;
        if (IsWindowVisible(Handle) != _visible) return false;
        return !_visible || (GetWindowRect(Handle, out var r) && r.Left == _position.X && r.Top == _position.Y);
    }

    /// <summary>Applies the last visibility and position again (after something else hid or moved the window).</summary>
    public void Reassert()
    {
        if (!IsAlive) return;
        SetWindowPos(Handle, IntPtr.Zero, _position.X, _position.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        ShowWindow(Handle, _visible ? SW_SHOWNA : SW_HIDE);
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
        if (Mouse != null && m.Msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MOUSELEAVE or WM_LBUTTONDBLCLK)
        {
            if (m.Msg == WM_MOUSEMOVE)
            {
                var track = new TRACKMOUSEEVENT { Size = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = TME_LEAVE, Window = Handle };
                TrackMouseEvent(ref track);
            }
            long l = m.LParam.ToInt64();
            var point = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
            if (Mouse(m.Msg, point)) return;
        }
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
                if (_dragging)
                {
                    _dragging = false;
                    ReleaseCapture();
                    DragEnded?.Invoke();
                }
                return;

            case WM_CAPTURECHANGED:
                if (_dragging)
                {
                    _dragging = false;
                    DragEnded?.Invoke();
                }
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

        var companions = Companions.Where(c => c is { IsAlive: true }).Select(c => (Window: c!,
            Position: new Point(c!.Position.X + (overlayPos.X - _position.X), c.Position.Y + (overlayPos.Y - _position.Y)))).ToList();

        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        IntPtr batch = BeginDeferWindowPos(2 + companions.Count);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, _emulator, IntPtr.Zero, _dragEmulator.X + dx, _dragEmulator.Y + dy, 0, 0, flags);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, Handle, IntPtr.Zero, overlayPos.X, overlayPos.Y, 0, 0, flags);
        foreach (var (window, position) in companions)
            if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, window.Handle, IntPtr.Zero, position.X, position.Y, 0, 0, flags);
        if (batch == IntPtr.Zero || !EndDeferWindowPos(batch))
        {
            SetWindowPos(_emulator, IntPtr.Zero, _dragEmulator.X + dx, _dragEmulator.Y + dy, 0, 0, flags);
            SetWindowPos(Handle, IntPtr.Zero, overlayPos.X, overlayPos.Y, 0, 0, flags);
            foreach (var (window, position) in companions) SetWindowPos(window.Handle, IntPtr.Zero, position.X, position.Y, 0, 0, flags);
        }
        _position = overlayPos;
        foreach (var (window, position) in companions) window.NotifyMoved(position);
    }

    internal void NotifyMoved(Point position) => _position = position;
}
