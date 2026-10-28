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
    private OverlayWindow? _glass; // click-through layer over the picture (the open iPhone Duo's crease)
    private RenderedFrame? _current;
    private RenderedFrame? _shown, _glassShown; // what the overlay and glass windows currently hold
    private RenderKey? _pending;
    private (Point Offset, RenderKey Key, RECT Box)? _region;
    private System.Windows.Forms.Timer? _verifyTimer;
    private int _verifyTicks;
    private bool _disposed;
    private bool _androidBusy;
    private DateTime _nextDisplayQuery;
    private string? _failedCutout;
    private DuoPose? _wantedPose;
    private DateTime _nextPosePoll;
    private bool _autoPoseTried;
    private bool _displayAnswered; // the first display query came back (with or without an answer)

    /// <summary>adb endpoint and native display, known a moment after attaching (null until then).</summary>
    public MuMuInstance? Instance { get; private set; }
    public AndroidDisplay? Display { get; private set; }

    /// <summary>iPhone Duo pose Android is in (from <c>wm size</c>); null when the emulator is not in one.</summary>
    public DuoPose? Pose { get; private set; }

    /// <summary>The emulator runs at the iPhone Duo's 2670 × 2034 surface, so it can fold and unfold.</summary>
    public bool IsDuoSurface => Display != null && IPhoneDuo.IsSurface(Display.Native);

    public IntPtr Main { get; }
    public IntPtr Render { get; private set; }
    public uint Pid { get; }
    public string Title { get; private set; }
    public Size RenderSize { get; private set; }
    public FrameInfo? Frame { get; private set; }

    /// <summary>Title bar temporarily brought back (bezel double-click) without dropping the frame.</summary>
    public bool Peek { get; set; }

    public Rectangle? RecordingBounds(int padding)
    {
        Sync();
        if (_disposed || !_app.Config.Enabled || !_app.Config.Instance(Title).Enabled ||
            _current == null || _overlay is not { IsAlive: true, IsVisible: true } || IsIconic(Main)) return null;

        int inset = Math.Clamp(padding, 0, 500);
        Point position = _overlay.Position;
        return new Rectangle(position.X - inset, position.Y - inset,
            _current.Width + inset * 2, _current.Height + inset * 2);
    }

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
        var chosen = _app.Library.Find(instance.Frame);
        // An emulator in an iPhone Duo pose wears the Duo frame for that pose, unless it was given another frame.
        if (Pose is { } pose && (chosen == null || IPhoneDuo.IsDuo(chosen))) chosen = _app.Library.Find(IPhoneDuo.FrameKey(pose)) ?? chosen;
        Frame = chosen ?? _app.Library.Find(config.DefaultFrame) ?? _app.Library.BestMatch(rr.Width, rr.Height);
        if (Frame == null)
        {
            Suspend();
            return;
        }
        EnsureAndroid();

        // A Duo frame, or an automatic one on a picture shaped like the Duo surface: until Android has first said which
        // display is on, any frame or placement could be the wrong one (and would flash up for a moment).
        if (!_displayAnswered && (IPhoneDuo.IsDuo(Frame) || (chosen == null && IPhoneDuo.LooksLikeSurface(RenderSize))))
        {
            Suspend();
            return;
        }

        // The picture: all of MuMu's render surface, or for the iPhone Duo the centred part the current display uses.
        var picture = new Rectangle(0, 0, rr.Width, rr.Height);
        if (IPhoneDuo.IsDuo(Frame))
        {
            if (IsDuoSurface && Pose == null)
            {
                Suspend(); // a pose is on its way (see EnsureAndroid)
                return;
            }
            if (Pose is { } shown && IPhoneDuo.PictureInRender(shown, RenderSize) is { } part) picture = part;
        }

        var key = new RenderKey(Frame.Key, picture.Width, picture.Height, Math.Clamp(config.Overlap, 0, 8));
        if (_current == null || _current.Key != key)
        {
            // Never show a frame that doesn't match the picture; take it down until the new one is ready.
            Suspend();
            if (_pending != key) StartRender(Frame, key);
            return;
        }

        ApplyRegion(new Point(rr.Left - wr.Left + picture.X, rr.Top - wr.Top + picture.Y));
        ShowOverlay(new Point(rr.Left + picture.X - _current.RenderOffset.X, rr.Top + picture.Y - _current.RenderOffset.Y));
        EnsureStatusBar(new Point(rr.Left, rr.Top));
    }

    /// <summary>Puts the current frame (and its glass layer, if any) at <paramref name="position"/> and shows it.</summary>
    private void ShowOverlay(Point position)
    {
        var overlay = EnsureOverlay();
        if (overlay == null || _current == null) return;
        if (_shown != _current)
        {
            overlay.SetFrame(_current, position);
            _shown = _current;
        }
        else overlay.MoveTo(position);
        overlay.SetVisible(true);

        if (_current.Glass is not { } glass)
        {
            _glass?.SetVisible(false);
            return;
        }
        if (_glass is not { IsAlive: true })
        {
            try
            {
                _glass = new OverlayWindow(Main, interactive: false); // never takes a tap meant for the app
                _glassShown = null;
                overlay.Companions = [_bar, _glass];
            }
            catch (Exception ex)
            {
                Log.Write($"Glass window failed for '{Title}': {ex.Message}");
                return;
            }
        }
        var at = new Point(position.X + glass.Bounds.X, position.Y + glass.Bounds.Y);
        if (_glassShown != _current)
        {
            _glass.SetBitmap(glass.Pixels, glass.Bounds.Width, glass.Bounds.Height, at);
            _glassShown = _current;
        }
        else _glass.MoveTo(at);
        _glass.SetVisible(true);
    }

    // ---- iOS-style status bar ------------------------------------------------------------------------------------

    private OverlayWindow? _bar;
    private System.Windows.Forms.Timer? _barTimer;
    private StatusBarLayout? _barLayout;
    private string? _barLayoutFrame;
    private bool _barApplied, _barPolling, _barDark;
    private (string Time, bool Dark, int Width, int Height)? _barShown;

    public bool StatusBarActive => _barApplied;

    /// <summary>Keeps the band window in step with the setting, the picture and Android's light/dark request.</summary>
    private void EnsureStatusBar(Point picture)
    {
        // Never on the iPhone Duo: the app draws the time and status in its own rail there.
        bool wanted = _app.Config.IosStatusBarFor(Title) && Instance != null && Display != null && Frame != null && !IPhoneDuo.IsDuo(Frame);
        // Drawing the bar alone does not reserve space in apps. Wait for Android's matching insets before covering it.
        wanted = wanted && _app.Config.Instance(Title).InstalledCutout ==
            AndroidCutout.Signature(Frame!, Display!, _app.Config.StatusBarPaddingFactor, hideNotificationIcons: true);
        if (!wanted)
        {
            if (_barApplied && !_androidBusy)
            {
                _barApplied = false;
                var instance = Instance!;
                RunAndroidTask(() => { AndroidStatusBar.Restore(instance); return () => Log.Write($"'{Title}': Android status bar restored"); });
            }
            _barTimer?.Stop();
            _bar?.SetVisible(false);
            return;
        }

        if (!_barApplied && !_androidBusy)
        {
            _barApplied = true;
            var instance = Instance!;
            RunAndroidTask(() =>
            {
                AndroidStatusBar.Hide(instance);
                bool dark = AndroidStatusBar.WantsDarkGlyphs(instance);
                return () => { _barDark = dark; Log.Write($"'{Title}': Android status bar hidden, iOS-style bar on"); RefreshStatusBar(); };
            });
        }

        if (_barLayout == null || _barLayoutFrame != Frame!.Key)
        {
            var spec = AndroidCutout.Measure(Frame!, Display!, _app.Config.StatusBarPaddingFactor);
            if (spec == null) return;
            _barLayout = StatusBarLayout.From(spec, Display!);
            _barLayoutFrame = Frame!.Key;
            _barShown = null;
        }

        if (_barTimer == null)
        {
            _barTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _barTimer.Tick += (_, _) => { if (!_disposed) { PollStatusBarTheme(); RefreshStatusBar(); } };
        }
        _barTimer.Start();

        if (_bar is not { IsAlive: true })
        {
            try
            {
                _bar = new OverlayWindow(Main, interactive: false); // WS_EX_TRANSPARENT: the notification shade stays reachable
                _barShown = null;
                if (_overlay != null) _overlay.Companions = [_bar, _glass];
            }
            catch (Exception ex)
            {
                Log.Write($"Status bar window failed for '{Title}': {ex.Message}");
                return;
            }
        }
        RefreshStatusBar();
        _bar.MoveTo(picture);
        _bar.SetVisible(true);
    }

    /// <summary>Asks Android (in the background) whether the foreground app wants dark glyphs; ~50 ms over adb.</summary>
    private void PollStatusBarTheme()
    {
        if (_barPolling || Instance is not { } instance || !_barApplied) return;
        _barPolling = true;
        Task.Run(() =>
        {
            bool dark = false;
            try { dark = AndroidStatusBar.WantsDarkGlyphs(instance); }
            catch (Exception ex) { Log.Write($"'{Title}': theme poll failed: {ex.Message}"); }
            _app.Post(() =>
            {
                _barPolling = false;
                if (_disposed || dark == _barDark) return;
                _barDark = dark;
                RefreshStatusBar();
            });
        });
    }

    /// <summary>Redraws the band when the time, the theme or the picture size changed.</summary>
    private void RefreshStatusBar()
    {
        if (_bar is not { IsAlive: true } || _barLayout == null || _current == null) return;
        int rw = _current.Key.RenderWidth, rh = _current.Key.RenderHeight;
        string time = IosStatusBar.ClockText(_app.Config.StatusBarClock);
        if (_barShown is { } shown && shown.Time == time && shown.Dark == _barDark && shown.Width == rw && shown.Height == rh) return;

        var (pixels, width, height) = IosStatusBar.RenderBand(rw, rh, _barLayout, _barDark, time, IosStatusBar.Family(_app.Config.StatusBarFont));
        if (!GetWindowRect(Render, out var rr)) return;
        _bar.SetBitmap(pixels, width, height, new Point(rr.Left, rr.Top));
        _barShown = (time, _barDark, rw, rh);
    }

    /// <summary>What a framed screenshot needs to draw the same bar: layout, current theme and time.</summary>
    public (StatusBarLayout Layout, bool Dark, string Time)? StatusBarForScreenshot()
    {
        if (!_barApplied || _barLayout == null) return null;
        return (_barLayout, _barDark, IosStatusBar.ClockText(_app.Config.StatusBarClock));
    }

    // ---- Android side: display info, cutout overlays, resolution ------------------------------------------------

    /// <summary>Looks up the emulator's adb endpoint and display once, then keeps the cutout overlays matching the frame.</summary>
    private void EnsureAndroid()
    {
        if (_androidBusy || Frame == null) return;

        if (Display == null)
        {
            if (DateTime.UtcNow < _nextDisplayQuery) return;
            StartDisplayQuery();
            return;
        }

        var config = _app.Config.Instance(Title);
        if (Instance == null) return;

        if (IsDuoSurface)
        {
            // A Duo frame was picked for an emulator that is not in a pose yet: put it in that frame's pose (once).
            if (Pose == null && IPhoneDuo.PoseOf(Frame) is { } framePose && !_autoPoseTried)
            {
                _autoPoseTried = true;
                StartPoseSwitch(framePose);
                return;
            }
            // Keep up with poses switched from outside this app (adb, scripts): a cheap wm size every few seconds.
            if (DateTime.UtcNow >= _nextPosePoll)
            {
                PollPose();
                return;
            }
        }

        if (!_app.Config.CutoutFor(Title) || IPhoneDuo.IsDuo(Frame))
        {
            // Switched off (here or globally), or an iPhone Duo, whose app draws its own rail: take the overlays back out.
            if (config.InstalledCutout != null) RemoveCutout();
            return;
        }

        bool iosBar = _app.Config.IosStatusBarFor(Title);
        string signature = AndroidCutout.Signature(Frame, Display, _app.Config.StatusBarPaddingFactor, iosBar);
        if (signature == config.InstalledCutout || signature == _failedCutout) return;

        var (frame, display2, instance2, factor, sdk) = (Frame, Display, Instance, _app.Config.StatusBarPaddingFactor, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            var missing = AndroidTools.Missing(sdk);
            if (missing.Count > 0)
                return () =>
                {
                    _failedCutout = signature;
                    Log.Write($"'{Title}': missing Android display tools: {string.Join(", ", missing)}");
                    _app.OfferAndroidTools();
                };
            var spec = AndroidCutout.Measure(frame, display2, factor);
            string? error = spec == null ? "frame and display orientation differ" : AndroidCutout.Apply(instance2, spec, sdk, iosBar);
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

    /// <summary>Finds the adb endpoint (once) and asks Android for its display and iPhone Duo pose.</summary>
    private void StartDisplayQuery()
    {
        _nextDisplayQuery = DateTime.UtcNow.AddSeconds(10); // Android may still be booting; try again later
        RunAndroidTask(() =>
        {
            MuMuInstance? instance = Instance;
            AndroidDisplay? display = null;
            try
            {
                instance ??= MuMuControl.Locate(Pid);
                display = instance == null ? null : MuMuControl.QueryDisplay(instance);
            }
            catch (Exception ex)
            {
                Log.Write($"'{Title}': display query failed: {ex.Message}"); // still an answer: frames must not wait forever
            }
            return () =>
            {
                Instance = instance;
                Display = display;
                Pose = IPhoneDuo.Detect(display);
                _displayAnswered = true;
                if (display != null) Log.Write($"'{Title}': MuMu instance {instance!.Index}, adb {instance.AdbSerial}, display {display.Native.Width}x{display.Native.Height} @ {display.Density} dpi" +
                    (display.Override is { } o ? $", override {o.Width}x{o.Height}" : "") + (Pose is { } p ? $", iPhone Duo {p}" : ""));
                Sync();
            };
        });
    }

    // ---- iPhone Duo: fold and unfold ----------------------------------------------------------------------------

    /// <summary>Folds or unfolds the iPhone Duo. False (after telling the user why) when this emulator cannot.</summary>
    public bool SetDuoPose(DuoPose pose)
    {
        if (Display != null && !IsDuoSurface)
        {
            NotDuo();
            return false;
        }
        _wantedPose = pose;
        if (_androidBusy) return true; // started when the running adb task ends
        if (Display == null) StartDisplayQuery(); // works with frames switched off too; the pose follows the answer
        else StartWantedPose();
        return true;
    }

    public bool ToggleDuoPose() => SetDuoPose(Pose is { } pose ? IPhoneDuo.Other(pose) : DuoPose.Folded);

    private void NotDuo() =>
        _app.Notify($"{Title} is not set up as an iPhone Duo. Use Android display, Set up as iPhone Duo, first.", warning: true);

    private void StartWantedPose()
    {
        if (_wantedPose is not { } pose || Instance == null || Display == null) return;
        _wantedPose = null;
        if (!IsDuoSurface)
        {
            NotDuo();
            return;
        }
        // Folding means showing the Duo: an explicit choice of another frame gives way to the Duo frame for that pose.
        var instance = _app.Config.Instance(Title);
        if (instance.Frame != null && instance.Frame != IPhoneDuo.FrameKey(pose))
        {
            instance.Frame = IPhoneDuo.FrameKey(pose);
            _app.Config.Save();
        }
        StartPoseSwitch(pose);
    }

    /// <summary>
    /// Runs the <c>wm</c> commands for a pose. The new frame is rendered first, so it goes up as soon as Android has
    /// switched (the picture changes size at that moment and the old clip would show the wrong part of it).
    /// </summary>
    private void StartPoseSwitch(DuoPose pose)
    {
        var (instance, display) = (Instance!, Display!);
        var frame = _app.Library.Find(IPhoneDuo.FrameKey(pose));
        RenderKey? key = frame != null && IPhoneDuo.PictureInRender(pose, RenderSize) is { } picture
            ? new RenderKey(frame.Key, picture.Width, picture.Height, Math.Clamp(_app.Config.Overlap, 0, 8))
            : null;
        _nextPosePoll = DateTime.UtcNow.AddSeconds(4);
        RunAndroidTask(() =>
        {
            var rendered = key is { } k ? FrameRenderer.Render(frame!, k) : null;
            var (_, output) = instance.Shell(string.Join("; ", IPhoneDuo.Commands(pose, display.Native)));
            var now = MuMuControl.QueryDisplay(instance);
            return () =>
            {
                if (now != null) Display = now;
                var reached = IPhoneDuo.Detect(Display);
                Log.Write($"'{Title}': iPhone Duo -> {pose}: {(reached == pose ? "done" : $"still {reached?.ToString() ?? "no pose"} ({output.Trim()})")}");
                if (reached != pose)
                    _app.Notify($"{Title}: could not {(pose == DuoPose.Folded ? "fold" : "unfold")} the iPhone Duo. {output.Trim()}".TrimEnd(), warning: true);
                SetPose(reached);
                if (rendered != null && reached == pose)
                {
                    _pending = null;
                    _current = rendered;
                }
                Sync();
            };
        });
    }

    private void PollPose()
    {
        _nextPosePoll = DateTime.UtcNow.AddSeconds(4);
        var instance = Instance!;
        RunAndroidTask(() =>
        {
            var display = MuMuControl.QueryDisplay(instance);
            return () =>
            {
                if (display == null) return;
                Display = display;
                var pose = IPhoneDuo.Detect(display);
                if (pose == Pose) return;
                Log.Write($"'{Title}': iPhone Duo pose changed outside the app: {pose?.ToString() ?? "none"}");
                SetPose(pose);
                Sync();
            };
        });
    }

    /// <summary>Records the pose and keeps a stored Duo frame choice on the matching variant, so menus and restarts agree.</summary>
    private void SetPose(DuoPose? pose)
    {
        Pose = pose;
        var instance = _app.Config.Instance(Title);
        if (pose is not { } p || !IPhoneDuo.IsDuo(_app.Library.Find(instance.Frame)) || instance.Frame == IPhoneDuo.FrameKey(p)) return;
        instance.Frame = IPhoneDuo.FrameKey(p);
        _app.Config.Save();
    }

    /// <summary>Gives the emulator the Duo's 2670 × 2034 surface at 480 dpi and restarts it (MuMu applies resolutions on start).</summary>
    public void SetUpAsDuo()
    {
        if (Instance is not { } instance) return;
        RunAndroidTask(() =>
        {
            string result = MuMuControl.SetResolution(instance, IPhoneDuo.Surface, IPhoneDuo.Density);
            Log.Write($"'{Title}': resolution -> iPhone Duo {IPhoneDuo.Surface.Width}x{IPhoneDuo.Surface.Height} @ {IPhoneDuo.Density} dpi: {result}");
            string restart = MuMuControl.Restart(instance);
            Log.Write($"'{Title}': restart: {restart}");
            return () =>
            {
                Display = null;
                Pose = null;
                _autoPoseTried = false;
                _displayAnswered = false;
            };
        });
    }

    /// <summary>Retry after a failure, or rebuild on request.</summary>
    public void RetryCutout()
    {
        _failedCutout = null;
        Sync();
    }

    /// <summary>Rebuild even when the stored frame signature has not changed.</summary>
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
                if (!_androidBusy) StartWantedPose(); // a fold or unfold asked for while this task ran
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
        Sync();
    }

    private OverlayWindow? EnsureOverlay()
    {
        if (_overlay is { IsAlive: true }) return _overlay;
        try
        {
            _overlay = new OverlayWindow(Main, _app.Config.BezelInteractive) { Companions = [_bar, _glass] };
            _overlay.MenuRequested += () => _app.ShowSessionMenu(this);
            _overlay.DoubleClicked += () => { Peek = !Peek; Sync(); };
            _shown = null; // the caller puts the bitmap in
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
        _glass?.SetVisible(false);
        _bar?.SetVisible(false);
        _barTimer?.Stop();
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
        _barTimer?.Dispose();
        _barTimer = null;
        if (_bar is { IsAlive: true }) _bar.DestroyHandle();
        _bar = null;
        if (_glass is { IsAlive: true }) _glass.DestroyHandle();
        _glass = null;
        if (_overlay is { IsAlive: true }) _overlay.DestroyHandle();
        _overlay = null;
        if (_barApplied && Instance is { } instance)
        {
            // Synchronous on purpose: this runs while the app exits, and Android must not be left without a status bar.
            _barApplied = false;
            try { AndroidStatusBar.Restore(instance); } catch (Exception ex) { Log.Write($"'{Title}': status bar restore failed: {ex.Message}"); }
        }
        Log.Write($"Detached from '{Title}'");
    }
}
