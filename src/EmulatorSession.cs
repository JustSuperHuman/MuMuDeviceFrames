using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>One running emulator window together with its frame overlay.</summary>
internal sealed class EmulatorSession : IDisposable
{
    public const string RenderClass = "nemuwin";

    private readonly TrayApp _app;
    private readonly WinEventProc _eventProc; // must stay referenced while the hooks live
    private readonly List<IntPtr> _hooks = [];
    private OverlayWindow? _overlay;
    private RenderedFrame? _current;
    private RenderKey? _pending;
    private (Point Offset, RenderKey Key, RECT Box)? _region;
    private System.Windows.Forms.Timer? _verifyTimer;
    private int _verifyTicks;
    private bool _disposed;
    private bool _androidBusy;
    private DateTime _nextDisplayQuery;
    private string? _failedCutout;

    /// <summary>adb endpoint and native display, known a moment after attaching (null until then).</summary>
    public MuMuInstance? Instance { get; private set; }
    public AndroidDisplay? Display { get; private set; }

    public IntPtr Main { get; }
    public IntPtr Render { get; private set; }
    public uint Pid { get; }
    public string Title { get; private set; }
    public Size RenderSize { get; private set; }
    public FrameInfo? Frame { get; private set; }

    /// <summary>Title bar temporarily brought back (bezel double-click) without dropping the frame.</summary>
    public bool Peek { get; set; }

    public EmulatorSession(TrayApp app, IntPtr main, IntPtr render, uint pid, string title)
    {
        _app = app;
        Main = main; Render = render; Pid = pid; Title = title;

        _eventProc = OnWinEvent;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _eventProc, pid, 0, flags));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _eventProc, pid, 0, flags));
        Log.Write($"Attached to '{title}' (pid {pid}, hwnd 0x{main:X}, render 0x{render:X})");
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || (hwnd != Main && hwnd != Render)) return;
        if (evt is EVENT_OBJECT_LOCATIONCHANGE or EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE or EVENT_OBJECT_DESTROY
            or EVENT_SYSTEM_MINIMIZESTART or EVENT_SYSTEM_MINIMIZEEND)
            Sync();
    }

    public bool IsGone => !IsWindow(Main);

    public void RefreshTitle()
    {
        var title = GetWindowText(Main);
        if (title.Length > 0) Title = title;
    }

    /// <summary>Brings overlay, clip region and position in line with the emulator's current state. Cheap when nothing changed.</summary>
    public void Sync()
    {
        if (_disposed || IsGone) return;
        if (!IsWindow(Render)) Render = FindChildByClass(Main, RenderClass);

        var config = _app.Config;
        var instance = config.Instance(Title);

        // Minimised: the window manager hides and re-shows owned windows by itself. Keeping the clip region in place
        // avoids the title bar flashing up during the restore animation.
        if (IsIconic(Main) && config.Enabled && instance.Enabled) return;

        bool wanted = config.Enabled && instance.Enabled && Render != IntPtr.Zero
            && IsWindowVisible(Main) && !IsZoomed(Main);
        if (!wanted || !GetWindowRect(Main, out var wr) || !GetWindowRect(Render, out var rr) || rr.Width < 64 || rr.Height < 64 || CoversMonitor(rr))
        {
            Suspend();
            return;
        }

        RenderSize = new Size(rr.Width, rr.Height);
        Frame = _app.Library.Find(instance.Frame) ?? _app.Library.Find(config.DefaultFrame) ?? _app.Library.BestMatch(rr.Width, rr.Height);
        if (Frame == null)
        {
            Suspend();
            return;
        }

        var key = new RenderKey(Frame.Key, rr.Width, rr.Height, Math.Clamp(config.Overlap, 0, 8));
        if (_current == null || _current.Key != key)
        {
            // Never show a frame that doesn't match the picture; take it down until the new one is ready.
            Suspend();
            if (_pending != key) StartRender(Frame, key);
            return;
        }

        ApplyRegion(new Point(rr.Left - wr.Left, rr.Top - wr.Top));
        var position = new Point(rr.Left - _current.RenderOffset.X, rr.Top - _current.RenderOffset.Y);
        EnsureOverlay()?.MoveTo(position);
        _overlay?.SetVisible(true);
        EnsureAndroid();
    }

    // ---- Android side: display info, cutout overlays, resolution ------------------------------------------------

    /// <summary>Looks up the emulator's adb endpoint and display once, then keeps the cutout overlays matching the frame.</summary>
    private void EnsureAndroid()
    {
        if (_androidBusy || Frame == null) return;

        if (Display == null)
        {
            if (DateTime.UtcNow < _nextDisplayQuery) return;
            _nextDisplayQuery = DateTime.UtcNow.AddSeconds(10); // Android may still be booting; try again later
            RunAndroidTask(() =>
            {
                var instance = Instance ?? MuMuControl.Locate(Pid);
                var display = instance == null ? null : MuMuControl.QueryDisplay(instance);
                return () =>
                {
                    Instance = instance;
                    Display = display;
                    if (display != null) Log.Write($"'{Title}': MuMu instance {instance!.Index}, adb {instance.AdbSerial}, display {display.Native.Width}x{display.Native.Height} @ {display.Density} dpi");
                };
            });
            return;
        }

        var config = _app.Config.Instance(Title);
        if (!config.MatchAndroidCutout || Instance == null) return;
        string signature = AndroidCutout.Signature(Frame, Display, _app.Config.StatusBarPaddingFactor);
        if (signature == config.InstalledCutout || signature == _failedCutout) return;

        var (frame, display2, instance2, factor, sdk) = (Frame, Display, Instance, _app.Config.StatusBarPaddingFactor, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            var spec = AndroidCutout.Measure(frame, display2, factor);
            string? error = spec == null ? "frame and display orientation differ" : AndroidCutout.Apply(instance2, spec, sdk);
            return () =>
            {
                if (error == null)
                {
                    config.InstalledCutout = signature;
                    _app.Config.Save();
                    Log.Write($"'{Title}': Android cutout matched to {frame.Key} (status bar {spec!.StatusBarHeight}px, side padding {spec.SidePadding}px, cutout {(spec.HasCutout ? "yes" : "none")})");
                    _app.Notify($"{Title}: Android status bar now matches {frame.Model}.");
                }
                else
                {
                    _failedCutout = signature;
                    Log.Write($"'{Title}': cutout failed: {error}");
                    _app.Notify($"{Title}: could not match the Android status bar. {error}", warning: true);
                }
            };
        });
    }

    /// <summary>Retry after a failure, or rebuild on request.</summary>
    public void ForceCutout()
    {
        _failedCutout = null;
        _app.Config.Instance(Title).InstalledCutout = null;
        Sync();
    }

    public void RemoveCutout()
    {
        var config = _app.Config.Instance(Title);
        config.InstalledCutout = null;
        _app.Config.Save();
        if (Instance is not { } instance) return;
        RunAndroidTask(() =>
        {
            AndroidCutout.Remove(instance);
            return () => Log.Write($"'{Title}': Android cutout overlays removed");
        });
    }

    /// <summary>Density that keeps the app layout (dp width) the same at a different resolution.</summary>
    public int DensityFor(Size target) => Display == null ? 0
        : (int)Math.Round((double)Display.Density * Math.Min(target.Width, target.Height) / Math.Min(Display.Native.Width, Display.Native.Height));

    public void SetDisplayToFrame()
    {
        if (Instance is not { } instance || Frame is not { } frame || Display == null) return;
        var size = frame.Screen.Size;
        int dpi = DensityFor(size);
        RunAndroidTask(() =>
        {
            string result = MuMuControl.SetResolution(instance, size, dpi);
            Log.Write($"'{Title}': resolution -> {size.Width}x{size.Height} @ {dpi} dpi: {result}");
            string restart = MuMuControl.Restart(instance);
            Log.Write($"'{Title}': restart: {restart}");
            return () => { };
        });
    }

    /// <summary>adb and MuMuManager calls take from 100 ms to minutes; none of that may run on the UI thread.</summary>
    private void RunAndroidTask(Func<Action> work)
    {
        _androidBusy = true;
        Task.Run(() =>
        {
            Action? done = null;
            try { done = work(); }
            catch (Exception ex) { Log.Write($"'{Title}': Android task failed: {ex.Message}"); }
            _app.Post(() =>
            {
                _androidBusy = false;
                if (_disposed) return;
                done?.Invoke();
            });
        });
    }

    private void StartRender(FrameInfo frame, RenderKey key)
    {
        _pending = key;
        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rendered = FrameRenderer.Render(frame, key);
                Log.Write($"Rendered {key.Frame} for {key.RenderWidth}x{key.RenderHeight} -> {rendered.Width}x{rendered.Height} in {sw.ElapsedMilliseconds} ms ({(rendered.Stretched ? "exact fit" : "cropped fit")})");
                _app.Post(() => OnRendered(key, rendered));
            }
            catch (Exception ex)
            {
                Log.Write($"Render failed for {key.Frame}: {ex}");
                _app.Post(() => { if (_pending == key) _pending = null; });
            }
        });
    }

    private void OnRendered(RenderKey key, RenderedFrame rendered)
    {
        if (_disposed || _pending != key) return;
        _pending = null;
        _current = rendered;
        if (IsGone || !GetWindowRect(Render, out var rr)) return;
        EnsureOverlay()?.SetFrame(rendered, new Point(rr.Left - rendered.RenderOffset.X, rr.Top - rendered.RenderOffset.Y));
        Sync();
    }

    private OverlayWindow? EnsureOverlay()
    {
        if (_overlay is { IsAlive: true }) return _overlay;
        try
        {
            _overlay = new OverlayWindow(Main, _app.Config.BezelInteractive);
            _overlay.MenuRequested += () => _app.ShowSessionMenu(this);
            _overlay.DoubleClicked += () => { Peek = !Peek; Sync(); };
            if (_current != null && GetWindowRect(Render, out var rr))
                _overlay.SetFrame(_current, new Point(rr.Left - _current.RenderOffset.X, rr.Top - _current.RenderOffset.Y));
        }
        catch (Exception ex)
        {
            Log.Write($"Overlay creation failed for '{Title}': {ex.Message}");
            _overlay = null;
        }
        return _overlay;
    }

    private void ApplyRegion(Point renderOffsetInWindow)
    {
        if (!_app.Config.ClipToScreen || Peek || _current == null)
        {
            ClearRegion();
            return;
        }
        if (_region is { } applied && applied.Offset == renderOffsetInWindow && applied.Key == _current.Key
            && EmulatorClip.IsIntact(Main, applied.Box))
            return;

        // Window regions are relative to the window's top-left corner, so moving the emulator never invalidates this.
        var runs = _current.ScreenRuns.Select(r => (r.Y + renderOffsetInWindow.Y, r.X0 + renderOffsetInWindow.X, r.X1 + renderOffsetInWindow.X)).ToList();
        if (EmulatorClip.Apply(Main, runs) is not { } box)
        {
            Log.Write($"Clipping failed for '{Title}'");
            return;
        }
        bool first = _region == null;
        _region = (renderOffsetInWindow, _current.Key, box);

        // The theming layer answers our DWM change by installing its own region a moment later, replacing ours.
        // Re-check in quick succession right after the first application; the regular scan covers the rest.
        if (first) StartVerifyBurst();
    }

    private void StartVerifyBurst()
    {
        _verifyTicks = 12;
        if (_verifyTimer == null)
        {
            _verifyTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _verifyTimer.Tick += (_, _) =>
            {
                if (--_verifyTicks <= 0 || _disposed) _verifyTimer?.Stop();
                if (!_disposed) Sync();
            };
        }
        _verifyTimer.Start();
    }

    private void ClearRegion()
    {
        if (_region == null) return;
        _region = null;
        _verifyTimer?.Stop();
        EmulatorClip.Remove(Main);
    }

    /// <summary>Gives the emulator its normal look back and hides the frame (keeps the rendered bitmap for later).</summary>
    public void Suspend()
    {
        _overlay?.SetVisible(false);
        ClearRegion();
    }

    /// <summary>Forget the rendered bitmap, e.g. after the frame choice or overlap changed.</summary>
    public void Invalidate()
    {
        _current = null;
        _pending = null;
    }

    public void RecreateOverlay()
    {
        if (_overlay is { IsAlive: true }) _overlay.DestroyHandle();
        _overlay = null;
    }

    private static bool CoversMonitor(RECT rr)
    {
        // Full-screen emulator: no room for a bezel, and clipping would fight MuMu's own full-screen mode.
        var center = new Point((rr.Left + rr.Right) / 2, (rr.Top + rr.Bottom) / 2);
        var bounds = Screen.FromPoint(center).Bounds;
        return rr.Width >= bounds.Width || rr.Height >= bounds.Height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        _hooks.Clear();
        ClearRegion();
        _verifyTimer?.Dispose();
        _verifyTimer = null;
        if (_overlay is { IsAlive: true }) _overlay.DestroyHandle();
        _overlay = null;
        Log.Write($"Detached from '{Title}'");
    }
}
