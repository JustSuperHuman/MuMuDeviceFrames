using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

internal sealed partial class EmulatorSession
{
    private DeviceMotionPanel? _motionPanel;
    private DeviceMotionState? _motionState;
    private DeviceMotionPreview? _motionPreview;
    private MotionSensorDriver? _motionDriver;
    private System.Windows.Forms.Timer? _motionTimer;
    private Texture? _motionImage;
    private Texture? _motionCaptured;
    private MotionCapture? _motionCapture;
    private RECT _motionWindow, _motionRender;
    private RenderedFrame? _motionFrame;
    private Point _motionPosition;
    private (string? Frame, string? Default) _motionChoice;
    private bool _motionTick;
    private bool _motionBar, _motionChrome;

    private static double MotionTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    [MemberNotNullWhen(true, nameof(_current), nameof(_overlay))]
    private bool ShowDeviceControls => DeviceMotionInput.AltDown && !_disposed && !IsGone && !IsIconic(Main)
        && IsWindowVisible(Main) && _app.Config.Enabled && _app.Config.Instance(Title).Enabled
        && _current != null && _overlay is { IsAlive: true }
        && (_overlay.IsVisible || _motionPreview?.Visible == true || _animation?.OnScreen == true);

    /// <summary>Refreshes just the floating controls when Alt changes, without relaying out or changing Android.</summary>
    public void RefreshDeviceControls()
    {
        EnsurePosePanel();
        EnsureMotionPanel();
    }

    public bool CanMoveDevice => !_disposed && !IsGone && !IsIconic(Main) && Instance != null && _current != null
        && _app.Config.Enabled && _app.Config.Instance(Title).Enabled
        && _animation == null && _fadeTimer?.Enabled != true && _overlay is { IsAlive: true }
        && (_overlay.IsVisible || _motionState != null);

    public void Shake()
    {
        if (!CanMoveDevice) return;
        EnsureMotion();
        _motionState!.Shake(MotionTime);
        _motionTimer!.Start();
        EnsureMotionPanel();
    }

    public bool BeginTilt(Point point)
    {
        if (!CanMoveDevice || _overlay?.IsDragging == true) return false;
        if (!new Rectangle(_overlay!.Position, new Size(_current!.Width, _current.Height)).Contains(point)) return false;
        EnsureMotion();
        SetForegroundWindow(Main);
        _motionState!.BeginTilt(point, new Size(_current!.Width, _current.Height), MotionTime);
        _motionTimer!.Start();
        return true;
    }

    public void MoveTilt(Point point) => _motionState?.MoveTilt(point);
    public void EndTilt() => _motionState?.EndTilt();

    private void EnsureMotion()
    {
        if (_motionState != null) return;
        _motionState = new DeviceMotionState();
        _motionChoice = (_app.Config.Instance(Title).Frame, _app.Config.DefaultFrame);
        _motionFrame = _current;
        _motionPosition = _overlay!.Position;
        GetWindowRect(Main, out _motionWindow);
        GetWindowRect(Render, out _motionRender);
        _motionImage = null;
        _motionCaptured = null;
        _motionBar = _bar?.IsVisible == true;
        _motionChrome = _chrome?.IsVisible == true;
        var instance = Instance!;
        MotionCapture? capture = null;
        capture = new MotionCapture(instance, Pid, _motionFrame!, new Size(_motionRender.Width, _motionRender.Height),
            new Rectangle(_pictureOnScreen.Left - _motionRender.Left, _pictureOnScreen.Top - _motionRender.Top,
                _pictureOnScreen.Width, _pictureOnScreen.Height), ex => _app.Post(() =>
            {
                if (_disposed || _motionCapture != capture) return;
                Log.Write($"'{Title}': live motion capture failed: {ex}");
                StopMotion();
                _app.Notify($"{Title}: could not capture the live screen for motion. {ex.Message}", warning: true);
            }));
        _motionCapture = capture;
        _motionDriver ??= new MotionSensorDriver(() => new MotionSensors(instance, Pid), ex => _app.Post(() =>
        {
            if (_disposed) return;
            Log.Write($"'{Title}': motion controls failed: {ex}");
            StopMotion();
            _motionDriver?.Dispose();
            _motionDriver = null;
            _app.Notify($"{Title}: motion controls could not connect to MuMu. {ex.Message}", warning: true);
        }));
        if (_motionTimer == null)
        {
            _motionTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _motionTimer.Tick += (_, _) => MotionTick();
        }
    }

    private void MotionTick()
    {
        if (_motionTick || _motionState == null) return;
        _motionTick = true;
        try
        {
            if (!CanMoveDevice || !IsWindowVisible(Main) || !GetWindowRect(Main, out var wr) || !Same(wr, _motionWindow)
                || !GetWindowRect(Render, out var rr) || !Same(rr, _motionRender)) { StopMotion(); return; }
            if (_motionState.Tilting && (!DeviceMotionInput.ModifiersDown || DeviceMotionInput.EscapeDown
                || GetAncestor(GetForegroundWindow(), 3) != Main)) _motionState.EndTilt();
            double now = MotionTime;
            MotionSample sample = _motionState.Sample(now);
            _motionDriver!.Submit(sample);
            EnsureMotionPanel();
            if (!_motionState.Active) { StopMotion(); return; }
            // Windows' animation preference disables the decorative shake, while an explicit tilt still shows its pose.
            if (!_motionState.Tilting && sample.Pitch == 0 && sample.Roll == 0 && !FoldAnimation.SystemAllowsAnimation()) return;
            if (_motionCapture?.Latest is not { } captured) return; // keep the real screen up until its live replacement is ready
            if (_motionCaptured != captured)
            {
                _motionCaptured = captured;
                _motionImage = new Texture((byte[])captured.Pixels.Clone(), captured.Width, captured.Height);
                if (_motionBar) CompositeMotionLayer(_motionImage, _bar);
                if (_motionChrome) CompositeMotionLayer(_motionImage, _chrome);
            }
            _motionPreview ??= new DeviceMotionPreview(Main, new Size(_motionFrame!.Width, _motionFrame.Height), _motionPosition);
            _motionPreview.Show(_motionImage!, sample);
            HideUnderMotion();
        }
        catch (Exception ex)
        {
            Log.Write($"'{Title}': motion preview failed: {ex}");
            StopMotion();
        }
        finally { _motionTick = false; }
    }

    private void CompositeMotionLayer(Texture image, OverlayWindow? layer)
    {
        if (layer?.Bitmap is not { } bitmap) return;
        var offset = new Point(layer.Position.X - _motionPosition.X, layer.Position.Y - _motionPosition.Y);
        for (int y = 0; y < bitmap.Size.Height; y++)
            for (int x = 0; x < bitmap.Size.Width; x++)
            {
                int dx = offset.X + x, dy = offset.Y + y;
                if (dx < 0 || dy < 0 || dx >= image.Width || dy >= image.Height) continue;
                int s = (y * bitmap.Size.Width + x) * 4, d = (dy * image.Width + dx) * 4, alpha = bitmap.Pixels[s + 3];
                for (int c = 0; c < 4; c++) image.Pixels[d + c] = (byte)(bitmap.Pixels[s + c] + image.Pixels[d + c] * (255 - alpha) / 255);
            }
    }

    private void HideUnderMotion()
    {
        _placeTimer?.Stop();
        _overlay?.SetVisible(false);
        _glass?.SetVisible(false);
        _bar?.SetVisible(false);
        _chrome?.SetVisible(false);
        _fade?.SetVisible(false);
        if (EmulatorClip.IsHidden(Main)) return;
        _region = null;
        _hiddenUnderAnimation = true;
        EmulatorClip.Hide(Main, new Point(_pictureOnScreen.Left + _pictureOnScreen.Width / 2 - _motionWindow.Left,
            _pictureOnScreen.Top + _pictureOnScreen.Height / 2 - _motionWindow.Top));
    }

    private bool KeepMotion(RECT window, RECT render)
    {
        if (_motionPreview?.Visible != true) return false;
        if (!Same(window, _motionWindow) || !Same(render, _motionRender) || _motionFrame != _current
            || _motionChoice != (_app.Config.Instance(Title).Frame, _app.Config.DefaultFrame))
        {
            StopMotion(restore: false);
            return false;
        }
        HideUnderMotion();
        EnsureMotionPanel();
        return true;
    }

    private void StopMotion(bool restore = true)
    {
        if (_motionState == null && _motionPreview == null) return;
        _motionState = null;
        _motionTimer?.Stop();
        _motionCapture?.Dispose();
        _motionCapture = null;
        _motionCaptured = null;
        _motionDriver?.Submit(MotionSample.Neutral);
        var preview = _motionPreview;
        _motionPreview = null;
        _motionImage = null;
        _motionFrame = null;
        // Re-expose the real screen under the last preview frame before taking the stand-in down.
        if (restore && !_disposed && !IsGone) Sync();
        preview?.Dispose();
    }

    private void EnsureMotionPanel()
    {
        if (!ShowDeviceControls)
        {
            _motionPanel?.Hide();
            return;
        }
        if (_motionPanel == null)
        {
            _motionPanel = new DeviceMotionPanel(Main, Shake);
            UpdateCompanions();
        }
        var device = new Rectangle(_overlay.Position, new Size(_current.Width, _current.Height));
        bool poses = _app.Config.DuoPoseControls && _panel?.Window.IsVisible == true;
        if (Frame != null && IPhoneDuo.IsDuo(Frame))
        {
            double sx = (double)_pictureOnScreen.Width / Frame.Screen.Width;
            int border = (int)Math.Round(DuoFrames.Border * sx), hinge = (int)Math.Round(DuoFrames.HingeOut * sx);
            device = Rectangle.FromLTRB(_pictureOnScreen.Left - border - (Pose == DuoPose.Folded ? hinge : 0),
                _pictureOnScreen.Top - border - (Pose == DuoPose.Tent ? hinge : 0), _pictureOnScreen.Right + border, _pictureOnScreen.Bottom + border);
        }
        uint dpi = GetDpiForWindow(Main);
        _motionPanel.Show(device, poses, dpi == 0 ? 1 : dpi / 96f, CanMoveDevice, _motionState?.Strength ?? 0);
    }
}
