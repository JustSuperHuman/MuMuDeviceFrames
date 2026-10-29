using System.Runtime.InteropServices;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>Intercepts only a modifier drag that starts on a framed device. Screen taps and ordinary bezel dragging
/// keep going to their original windows. Keeping the gesture until button-up prevents a stray app tap on release.</summary>
internal sealed class DeviceMotionInput : IDisposable
{
    private readonly MouseHookProc _callback;
    private readonly Func<IntPtr, EmulatorSession?> _find;
    private IntPtr _hook;
    private EmulatorSession? _gesture;

    public DeviceMotionInput(Func<IntPtr, EmulatorSession?> find)
    {
        _find = find;
        _callback = OnMouse;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, _callback, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("Could not install the device tilt mouse hook");
    }

    public static bool ModifiersDown => Down(0x11) && Down(0x10) && Down(0x12);
    public static bool AltDown => Down(0x12);
    public static bool EscapeDown => Down(0x1b);
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        int msg = (int)message;
        if (_gesture == null && (msg != WM_LBUTTONDOWN || !ModifiersDown))
            return CallNextHookEx(_hook, code, message, data);
        try
        {
            var raw = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
            var point = new Point(raw.Point.X, raw.Point.Y);
            if (_gesture == null)
            {
                IntPtr owner = GetAncestor(WindowFromPoint(raw.Point), 3); // GA_ROOTOWNER: screen, bezel or status layer
                if (_find(owner) is not { } session || !session.BeginTilt(point))
                    return CallNextHookEx(_hook, code, message, data);
                _gesture = session;
            }
            else if (msg == WM_LBUTTONUP)
            {
                _gesture.EndTilt();
                _gesture = null;
            }
            else if (msg == WM_MOUSEMOVE)
            {
                if (ModifiersDown && !EscapeDown) _gesture.MoveTilt(point);
                else _gesture.EndTilt();
            }
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MOUSEMOVE) return (IntPtr)1;
        }
        catch (Exception ex)
        {
            _gesture?.EndTilt();
            _gesture = null;
            Log.Write($"Tilt input failed: {ex.Message}");
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _gesture?.EndTilt();
        _gesture = null;
    }
}
