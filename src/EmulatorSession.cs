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
    private DuoPosePanel? _panel;  // iPhone Duo pose buttons beside the frame
    private OverlayWindow? _chrome; // iOS chrome over the picture: home indicator, or the Duo's rail / status bar
    private (ChromeState State, Size Picture)? _chromeShown;
    private System.Windows.Forms.Timer? _chromeTimer;
    private bool _chromeDark, _homeDark, _chromePolling, _duoBarHidden, _gesturesChecked;
    private readonly Backoff _duoChromeRetry = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    private string? _duoChromeTarget;
    private bool _duoChromeNotice;
    private OverlayWindow? _fade;  // the previous frame, fading out over a pose switch
    private System.Windows.Forms.Timer? _fadeTimer;
    private DateTime _fadeStart;
    private bool _fadeNext;
    private Rectangle _pictureOnScreen;
    private FoldAnimation? _animation;  // the fold animation covering a pose switch
    private DuoPose? _animationTarget;
    private bool _afterCaptured;
    private DateTime _afterNotBefore;
    private DateTime? _afterShownAt;
    private bool _hiddenUnderAnimation; // clipped to a dot while the animation stands in for it
    private readonly PoseShots _shots = new(); // each pose's last picture, the stand-in for the next switch into it
    private System.Windows.Forms.Timer? _afterTimer;
    private bool _warmed;
    private RenderedFrame? _current;
    private RenderedFrame? _shown, _glassShown; // what the overlay and glass windows currently hold
    private RenderKey? _pending;
    private (Point Offset, RenderKey Key, RECT Box)? _region;
    private System.Windows.Forms.Timer? _verifyTimer;
    private int _verifyTicks;
    private bool _disposed;
    private bool _androidBusy;
    private readonly Queue<Func<Action>> _androidTasks = new();
    private DateTime _nextDisplayQuery;
    private string? _failedCutout;
    private DuoPose? _wantedPose;
    private DateTime _nextPosePoll;
    private bool _autoPoseTried, _posturesTried;
    private int _resyncAttempts;
    private DateTime _nextResync;
    private bool _displayAnswered; // the first display query came back (with or without an answer)
    private bool _renderPortrait;
    private int _clipFailures;
    private Size _fittedRender, _letterboxSize;
    private DateTime? _letterboxSince;
    private DateTime _nextWindowFit;
    // MuMu turns and resizes its window in steps over a few frames; the layout waits until both rects hold still.
    private Size _seenWindowSize, _seenRenderSize;
    private DateTime _sizeChangedAt;
    private DateTime? _turnSince;           // the Duo picture is not the pose's way round yet (MuMu still turning)
    private (RECT Window, RECT Render)? _laidOut; // what the frame on screen was laid out for (see Watch)
    // Emulators (adb serials) whose iPhone Duo overlays are being installed and the emulator restarted: the session
    // attached to the window that comes back must not start the same again. UI thread only.
    private static readonly HashSet<string> DuoSetupRunning = [];
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TurnGrace = TimeSpan.FromSeconds(2);
    private System.Windows.Forms.Timer? _placeTimer; // MuMu's picture placed again after region changes (see PlacePictureSoon)
    private int _placeStep;
    private bool _userMoving;               // inside Windows' move / size loop on MuMu's window (its title bar or border)
    private bool _reanchor;                 // a user drag just ended: the next layout's centre is the new anchor
    private int _anchorMoves;               // window moves in a row that did not bring the device to its anchor
    private System.Windows.Forms.Timer? _anchorSave;

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
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _eventProc, pid, 0, flags));
        Log.Write($"Attached to '{title}' (pid {pid}, hwnd 0x{main:X}, render 0x{render:X})");
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || (hwnd != Main && hwnd != Render)) return;
        // The user dragging or resizing MuMu's own window (title bar, border): that moves the device on purpose.
        if (evt is EVENT_SYSTEM_MOVESIZESTART or EVENT_SYSTEM_MOVESIZEEND && hwnd == Main)
        {
            _userMoving = evt == EVENT_SYSTEM_MOVESIZESTART;
            if (!_userMoving) _reanchor = true;
            Sync();
            return;
        }
        if (evt == EVENT_SYSTEM_MINIMIZEEND)
        {
            _anchorMoves = 0; // restored: put the device back on its anchor however it came back
            StartVerifyBurst(20);
        }
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
        KeepHiddenUnderAnimation();

        var config = _app.Config;
        var instance = config.Instance(Title);

        // Minimised: the window manager hides and re-shows owned windows by itself. Keeping the clip region in place
        // avoids the title bar flashing up during the restore animation.
        if (IsIconic(Main) && config.Enabled && instance.Enabled) return;

        bool wanted = config.Enabled && instance.Enabled && Render != IntPtr.Zero
            && IsWindowVisible(Main) && !IsZoomed(Main) && !_app.IsBeingCreated(Title);
        if (!wanted || !GetWindowRect(Main, out var wr) || !GetWindowRect(Render, out var rr) || rr.Width < 64 || rr.Height < 64 || CoversMonitor(rr))
        {
            Suspend();
            return;
        }
        // Minimised, or on its way there or back: Windows parks the window at -32000. Nothing is laid out from that
        // (moving it to the anchor from there put its restored place 35000 px off screen).
        if (wr.Left <= -30000 || wr.Top <= -30000 || rr.Left <= -30000 || rr.Top <= -30000) return;

        if (FitMuMuWindow(wr, rr)) return; // MuMu's window is being resized to its picture; Sync runs again on the move

        // MuMu turns and resizes its window over several frames (a pose switch, its own rotation, a new resolution), and
        // its last size event can come before it is done. Lay out once both rects have held their size for a moment,
        // never on a size on the way, and keep looking (the burst) until then.
        var now = DateTime.UtcNow;
        var windowSize = new Size(wr.Width, wr.Height);
        var renderSize = new Size(rr.Width, rr.Height);
        if (windowSize != _seenWindowSize || renderSize != _seenRenderSize)
        {
            (_seenWindowSize, _seenRenderSize) = (windowSize, renderSize);
            _sizeChangedAt = now;
            StartVerifyBurst(10);
        }
        bool settling = now - _sizeChangedAt < SettleTime;
        RenderSize = renderSize;
        // MuMu turns its window when it turns Android for an app it remembers in portrait; on a Duo that breaks the
        // pose, so check it now rather than at the next poll.
        bool portrait = rr.Width < rr.Height;
        if (portrait != _renderPortrait)
        {
            _renderPortrait = portrait;
            if (IsDuoSurface) _nextPosePoll = DateTime.MinValue;
        }
        var chosen = _app.Library.Find(instance.Frame);
        // An emulator in an iPhone Duo pose wears the Duo frame for that pose, unless it was given another frame.
        if (Pose is { } pose && (chosen == null || IPhoneDuo.IsDuo(chosen))) chosen = _app.Library.Find(IPhoneDuo.FrameKey(pose)) ?? chosen;
        else if (chosen == null && IsDuoSurface && IsFoldable) chosen = _app.Library.Find(IPhoneDuo.FrameKey(DuoPose.Folded));
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
            if (Pose is { } shown && IPhoneDuo.PictureInRender(shown, RenderSize) is { } part)
            {
                picture = part;
                _turnSince = null;
            }
            else if (Pose != null)
            {
                // Android and MuMu's window disagree on the way round: one of them has not turned yet. Wait for the
                // other rather than put this pose's frame round a picture it does not fit; after a while (MuMu turned
                // for an app and the pose is being put back) show what there is.
                _turnSince ??= now;
                if (now - _turnSince < TurnGrace)
                {
                    settling = true;
                    StartVerifyBurst(10);
                }
            }
        }

        var key = new RenderKey(Frame.Key, picture.Width, picture.Height, Math.Clamp(config.Overlap, 0, 8));
        if (_animation != null)
        {
            FeedAnimation(key, wr, rr, picture, settling);
            return;
        }
        if (settling) return; // the frame on screen stays as it is until MuMu holds still
        if (_current == null || _current.Key != key)
        {
            // Never show a frame that doesn't match the picture; take it down until the new one is ready.
            Suspend();
            if (_pending != key) StartRender(Frame, key);
            return;
        }
        if (KeepPut(wr, rr, picture)) return; // MuMu's window moves to the anchor; Sync runs again on the move

        ApplyRegion(new Point(rr.Left - wr.Left + picture.X, rr.Top - wr.Top + picture.Y));
        _pictureOnScreen = new Rectangle(rr.Left + picture.X, rr.Top + picture.Y, picture.Width, picture.Height);
        ShowOverlay(new Point(rr.Left + picture.X - _current.RenderOffset.X, rr.Top + picture.Y - _current.RenderOffset.Y));
        EnsureStatusBar(new Point(rr.Left, rr.Top));
        EnsureChrome();
        EnsurePosePanel();
        _laidOut = (wr, rr);
    }

    /// <summary>
    /// The iOS chrome the tool owns, besides the status bar: the home indicator on iPhone frames (with the iOS chrome
    /// on and the model having one), the rail or status bar on the iPhone Duo. On the Duo, Android's own status bar is
    /// emptied too; Android reserves the rail outside app windows by default.
    /// </summary>
    private ChromeState? CurrentChrome()
    {
        if (Frame is not { } frame || Display is not { } display || !_app.Config.IosStatusBarFor(Title, frame)) return null;
        bool duo = IPhoneDuo.IsDuo(frame);
        if (duo && Pose == null) return null;
        bool home = IosChrome.HomeIndicator(frame, _app.Config.Instance(Title).DuoOverlay, _app.Config.Instance(Title).InstalledCutout);
        var kind = IosChrome.KindFor(frame, Pose);
        if (kind == ChromeKind.None && !home) return null;
        var screen = duo && Pose is { } pose ? IPhoneDuo.ScreenSize(pose) : display.Native;
        return new ChromeState(kind, home, screen, display.DensityOverride ?? display.Density, _barApplied ? _barDark : _chromeDark,
            IosStatusBar.ClockText(_app.Config.StatusBarClock), _homeDark);
    }

    private void EnsureChrome()
    {
        var state = CurrentChrome();
        bool duo = IPhoneDuo.IsDuo(Frame) && state != null;
        if (Instance is { } instance && Display is { } display && !_androidBusy && !DuoSetupRunning.Contains(instance.AdbSerial))
        {
            // The Duo's iOS chrome replaces Android's status bar content in every pose; turned off, Android gets its
            // own status bar back exactly as it was.
            var sdk = _app.Config.AndroidSdk;
            var config = _app.Config.Instance(Title);
            var areas = DuoSafeAreas.For(display.DensityOverride ?? display.Density, display.Native);
            bool protect = config.DuoProtectSafeAreas;
            string signature = AndroidCutout.DuoSignature(areas, protect);
            bool install = duo && config.DuoOverlay != signature, remove = !duo && config.DuoOverlay != null;
            string target = duo ? signature : "off";
            if (_duoChromeTarget != target)
            {
                _duoChromeTarget = target;
                _duoChromeRetry.Reset();
                _duoChromeNotice = false;
            }
            if ((duo != _duoBarHidden || install || remove) && _duoChromeRetry.TryNow(DateTime.UtcNow))
            {
                string title = Title, serial = instance.AdbSerial;
                DuoSetupRunning.Add(serial);
                RunAndroidTask(() =>
                {
                    string? error = null;
                    try
                    {
                        if (duo)
                        {
                            AndroidStatusBar.Hide(instance);
                            if (install) error = AndroidCutout.ApplyDuo(instance, sdk, areas, protect);
                        }
                        else
                        {
                            AndroidStatusBar.Restore(instance);
                            if (remove) AndroidCutout.Remove(instance);
                        }
                        // Android takes the new safe areas as the overlay changes (open apps are relaunched once, as for any
                        // framework overlay); a display off and on gives the pose it is in its own set.
                        if ((install && error == null) || remove)
                        {
                            instance.Shell(IPhoneDuo.RefreshSafeAreas);
                            if (install && protect && display.Override is { } forced) instance.Shell(IPhoneDuo.RefreshSafeBounds(forced));
                            if (install && Pose is { } pose) instance.Shell(IPhoneDuo.RefreshStatusBar(pose, display.Native));
                            Log.Write($"'{title}': iPhone Duo safe areas {(install ? $"installed ({signature})" : "removed")}");
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    finally
                    {
                        _app.Post(() => DuoSetupRunning.Remove(serial));
                    }
                    bool dark = AndroidStatusBar.WantsDarkGlyphs(instance);
                    return () =>
                    {
                        _chromeDark = dark;
                        if (error == null)
                        {
                            _duoBarHidden = duo;
                            if (install || remove)
                            {
                                config.DuoOverlay = install ? signature : null;
                                config.InstalledCutout = null; // both modes share overlay package names
                                _app.Config.Save();
                            }
                            _duoChromeRetry.Reset();
                            _nextCutoutCheck = DateTime.UtcNow.AddSeconds(1);
                        }
                        Log.Write(error == null ? $"'{Title}': Android status bar {(duo ? "emptied for the iPhone Duo's chrome" : "back")}" : $"'{Title}': Duo status bar overlay: {error}");
                        if (error != null && !_duoChromeNotice)
                        {
                            _duoChromeNotice = true;
                            _app.Notify($"{Title}: Duo safe areas could not be applied. {error}", warning: true);
                        }
                        Sync();
                    };
                });
            }
        }
        // iOS's navigation gestures on the Duo: MuMu switches them off at every boot, so look once per attach and turn
        // them back on (a framework restart, no emulator restart) where MuMu's root permission allows it.
        if (duo && !_gesturesChecked && !_androidBusy && Instance is { } gestureInstance)
        {
            _gesturesChecked = true;
            string title = Title;
            bool protect = _app.Config.Instance(title).DuoProtectSafeAreas;
            RunAndroidTask(() =>
            {
                string? error = null;
                bool rooted = MuMuControl.GetSetting(gestureInstance, "root_permission") == "true";
                bool off = !DuoSetup.GesturesOn(gestureInstance);
                if (off && rooted) error = DuoSetup.EnableGestures(gestureInstance);
                string? safeError = protect ? DuoSetup.EnsureSafeAreaSupport(gestureInstance) : null;
                return () =>
                {
                    if (off) Log.Write(!rooted ? $"'{title}': navigation gestures off (MuMu root permission is off)"
                        : error == null ? $"'{title}': navigation gestures back on" : $"'{title}': navigation gestures: {error}");
                    if (safeError != null) _app.Notify($"{title}: {safeError}", warning: true);
                    _nextPosePoll = DateTime.MinValue; // a framework restart drops the device state: put the pose back
                };
            });
        }
        if (state == null || _pictureOnScreen.Width <= 0)
        {
            _chrome?.SetVisible(false);
            _chromeTimer?.Stop();
            return;
        }
        if (_chromeTimer == null)
        {
            _chromeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _chromeTimer.Tick += (_, _) => { if (!_disposed) { PollChromeTheme(); EnsureChrome(); } };
        }
        _chromeTimer.Start();
        if (_chrome is not { IsAlive: true })
        {
            try
            {
                _chrome = new OverlayWindow(Main, interactive: false); // over the app, never in the way of a tap
                _chromeShown = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Chrome window failed for '{Title}': {ex.Message}");
                return;
            }
        }
        var picture = _pictureOnScreen.Size;
        if (_chromeShown is not { } shown || shown.State != state || shown.Picture != picture)
        {
            _chrome.SetBitmap(IosChrome.Render(picture, state, IosStatusBar.Family(_app.Config.StatusBarFont)), picture.Width, picture.Height, _pictureOnScreen.Location);
            _chromeShown = (state, picture);
        }
        else _chrome.MoveTo(_pictureOnScreen.Location);
        _chrome.SetVisible(true);
    }

    /// <summary>Light or dark glyphs, as the app in front asks (the iOS bar polls this itself when it runs).</summary>
    private void PollChromeTheme()
    {
        if (_chromePolling || CurrentChrome() is not { } state || !GetWindowRect(Main, out var wr)) return;
        // What is behind the time and ring decides, as on iOS: MuMu's own picture there (PrintWindow sees it whatever
        // covers the window), its median brightness.
        if (_pictureOnScreen.Width <= 0) return;
        float scale = (float)_pictureOnScreen.Width / state.Screen.Width;
        Rectangle InWindow(Rectangle area) => new(_pictureOnScreen.X - wr.Left + (int)(area.X * scale), _pictureOnScreen.Y - wr.Top + (int)(area.Y * scale),
            Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
        Rectangle? glyphs = IosChrome.GlyphArea(state) is { } g ? InWindow(g) : null;
        Rectangle? home = state.Home ? InWindow(IosChrome.HomeArea(state)) : null;
        if (glyphs == null && home == null) return;
        _chromePolling = true;
        var main = Main;
        Task.Run(() =>
        {
            bool? dark = null, homeDark = null;
            try
            {
                using var shot = Diagnostics.PrintEmulator(main, wr);
                if (glyphs is { } a) dark = IosChrome.IsLight(shot, a);
                if (home is { } b) homeDark = IosChrome.IsLight(shot, b);
            }
            catch { /* keep the last answer */ }
            _app.Post(() =>
            {
                _chromePolling = false;
                bool changed = dark is { } d && d != _chromeDark || homeDark is { } h && h != _homeDark;
                if (dark is { } d2) _chromeDark = d2;
                if (homeDark is { } h2) _homeDark = h2;
                if (changed) EnsureChrome();
            });
        });
    }

    /// <summary>What a framed screenshot needs to draw the same chrome.</summary>
    public ChromeState? ChromeForScreenshot() => CurrentChrome();

    /// <summary>
    /// Keeps the device where the user put it. Whenever the frame would land off the anchor (a pose switch, a turn, a
    /// new size, a restart, MuMu restoring its saved window position: MuMu moves its own window for all of those),
    /// MuMu's window is moved so the frame's centre is on the anchor again, kept fully on that monitor's work area.
    /// Only the user moves the anchor: dragging the device by its bezel, or Windows' move / size loop on MuMu's own
    /// window (its title bar or border). True when it moved the window.
    /// </summary>
    private bool KeepPut(RECT wr, RECT rr, Rectangle picture)
    {
        if (Peek || _current == null) return false;
        var frame = new Rectangle(rr.Left + picture.X - _current.RenderOffset.X, rr.Top + picture.Y - _current.RenderOffset.Y, _current.Width, _current.Height);
        var centre = DeviceAnchor.Centre(frame);
        var config = _app.Config.Instance(Title);
        if (config.Anchor == null || _userMoving || _reanchor || _overlay?.IsDragging == true)
        {
            _reanchor = false;
            _anchorMoves = 0;
            if (config.Anchor is { } old && old.X == centre.X && old.Y == centre.Y) return false;
            config.Anchor = new AnchorPoint(centre.X, centre.Y);
            SaveAnchorSoon();
            return false;
        }
        var anchor = config.Anchor.ToPoint();
        var work = Screen.FromPoint(anchor).WorkingArea;
        // A device bigger than the work area cannot be placed on it: make MuMu's window (and with it the picture and
        // the frame) smaller first, about the same centre; the next layout puts it on the anchor.
        if (DeviceAnchor.PictureToFit(new Size(rr.Width, rr.Height), frame.Size, work) is { } fits && ++_anchorMoves <= 3)
        {
            int chromeW = wr.Width - rr.Width, chromeH = wr.Height - rr.Height;
            int w = fits.Width + chromeW, h = fits.Height + chromeH;
            Log.Write($"'{Title}': the {frame.Width}x{frame.Height} device does not fit the {work.Width}x{work.Height} work area; MuMu's window {wr.Width}x{wr.Height} -> {w}x{h}");
            SetWindowPos(Main, IntPtr.Zero, wr.Left + (wr.Width - w) / 2, wr.Top + (wr.Height - h) / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            StartVerifyBurst(20);
            return true;
        }
        var target = DeviceAnchor.Place(frame.Size, anchor, work);
        int dx = target.X - frame.X, dy = target.Y - frame.Y;
        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
        {
            _anchorMoves = 0;
            return false;
        }
        // A window that will not go where it is put (MuMu clamping it, say) gets a few tries, then is left be.
        // A window off every screen (however it got there) always gets put back; one that is on screen but will not
        // stay where it is put (MuMu clamping it, say) gets a few tries, then is left be.
        if (!SystemInformation.VirtualScreen.IntersectsWith(Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom))) _anchorMoves = 0;
        if (++_anchorMoves > 3)
        {
            if (_anchorMoves == 4) Log.Write($"'{Title}': MuMu's window does not stay at the device's place ({dx}, {dy} px off); leaving it");
            return false;
        }
        Log.Write($"'{Title}': keeping the device at {anchor.X},{anchor.Y}: frame centre was {centre.X},{centre.Y}, moving MuMu's window by {dx},{dy}" +
            (target.Location != new Point(anchor.X - frame.Width / 2, anchor.Y - frame.Height / 2) ? " (kept on the work area)" : ""));
        SetWindowPos(Main, IntPtr.Zero, wr.Left + dx, wr.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        StartVerifyBurst(10);
        return true;
    }

    /// <summary>A drag sets the anchor many times a second; it goes to disk once the device has stayed put a moment.</summary>
    private void SaveAnchorSoon()
    {
        if (_anchorSave == null)
        {
            _anchorSave = new System.Windows.Forms.Timer { Interval = 800 };
            _anchorSave.Tick += (_, _) =>
            {
                _anchorSave!.Stop();
                _app.Config.Save();
            };
        }
        _anchorSave.Stop();
        _anchorSave.Start();
    }

    /// <summary>
    /// A cheap look (a few times a second, from the app) that MuMu's window and picture are still what the frame on
    /// screen was laid out for, and that the frame window is still up where it was put. Anything else gets a full
    /// <see cref="Sync"/>, so a missed or early window event can never leave the frame stale until the next drag.
    /// </summary>
    public void Watch()
    {
        // Right after a switch: MuMu's window not turned with Android by now is turned (see StartPoseSwitch).
        if (_followBy is { } follow && DateTime.UtcNow >= follow.At && !_disposed && !IsGone)
        {
            _followBy = null;
            // Also while the animation plays: it is waiting for MuMu's window to turn (the emulator is hidden under it).
            if (!Peek && GetWindowRect(Render, out var renderNow) && renderNow.Width < renderNow.Height != follow.Upright)
                TurnMuMuWindow($"MuMu did not turn its window {(follow.Upright ? "upright" : "landscape")} with Android");
        }
        // The iPhone Duo's watchdog runs on this clock too: Android's side once a second (KeepPose), even when nothing
        // on the desktop has moved (a script turning Android, MuMu keeping its window the wrong way round).
        if (!_disposed && IsDuoSurface && Instance != null && Display != null && !_androidBusy && _animation == null
            && DateTime.UtcNow >= _nextPosePoll && !IsGone && !IsIconic(Main)) PollPose();
        // Minimised or hidden: Windows hides the owned frame windows itself, and Sync leaves that alone.
        if (_disposed || _laidOut is not { } laid || _animation != null || IsGone || IsIconic(Main) || !IsWindowVisible(Main)) return;
        bool moved = !GetWindowRect(Main, out var wr) || !GetWindowRect(Render, out var rr) || !Same(wr, laid.Window) || !Same(rr, laid.Render);
        bool lost = _overlay is { IsAlive: true } overlay && !overlay.IsAsSet();
        // The clip region taken away (by MuMu, the theming layer or anyone): Sync puts it back. Peek has no region on
        // purpose, so it never counts as lost.
        bool clipLost = !Peek && _region is { } region && !EmulatorClip.IsIntact(Main, region.Box);
        if (clipLost && !_clipLostLogged) Log.Write($"'{Title}': clip region was taken away; putting it back");
        _clipLostLogged = clipLost; // once per time it goes
        if (!moved && !lost && !clipLost) return;
        if (lost)
        {
            if (!_frameLostLogged) Log.Write($"'{Title}': frame window was not where it was put; putting it back");
            _overlay!.Reassert();
        }
        _frameLostLogged = lost;
        Sync();
    }

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // ---- Fold animation ------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the animation for a pose switch, from what is on screen now (frame, live picture, glass) towards a
    /// placeholder of the new pose (its frame, screen dark) that the real picture replaces once Android has switched.
    /// False when it cannot or should not (setting off, Windows animations off, nothing shown yet).
    /// </summary>
    private bool BeginAnimation(DuoPose from, DuoPose to)
    {
        if (!_app.Config.DuoAnimations || !FoldAnimation.SystemAllowsAnimation() || _animation != null || _shown == null
            || _overlay is not { IsAlive: true, IsVisible: true } overlay || !GetWindowRect(Main, out var wr) || !GetWindowRect(Render, out var rr)) return false;
        try
        {
            var before = Composite(_shown, wr, _pictureOnScreen);
            if (before == null) return false;
            var beforeScreen = new RectangleF(overlay.Position.X, overlay.Position.Y, _shown.Width, _shown.Height);

            // Where the new pose will be: MuMu turns its window for the upright poses and tent, so predict it about the same centre.
            bool turns = IPhoneDuo.TurnsWindow(to) != IPhoneDuo.TurnsWindow(from);
            var render = turns ? new Size(RenderSize.Height, RenderSize.Width) : RenderSize;
            if (_app.Library.Find(IPhoneDuo.FrameKey(to)) is not { } frame || IPhoneDuo.PictureInRender(to, render) is not { } picture) return false;
            var rendered = FrameRenderer.Render(frame, new RenderKey(frame.Key, picture.Width, picture.Height, Math.Clamp(_app.Config.Overlap, 0, 8)));
            var origin = turns
                ? new Point((rr.Left + rr.Right) / 2 - render.Width / 2, (rr.Top + rr.Bottom) / 2 - render.Height / 2)
                : new Point(rr.Left, rr.Top);
            var afterScreen = new RectangleF(origin.X + picture.X - rendered.RenderOffset.X, origin.Y + picture.Y - rendered.RenderOffset.Y, rendered.Width, rendered.Height);
            if (!Peek && _app.Config.Instance(Title).Anchor is { } anchor)
            {
                // The device lands where it lives (KeepPut moves MuMu's window there before the new picture is taken).
                var landing = DeviceAnchor.Place(new Size(rendered.Width, rendered.Height), anchor.ToPoint(), Screen.FromPoint(anchor.ToPoint()).WorkingArea);
                afterScreen = new RectangleF(landing.X, landing.Y, landing.Width, landing.Height);
            }
            var placeholder = Composite(rendered, wr, null)!;
            // This pose as it is now is the newest picture of it; the new pose's last one stands in until its live one.
            _shots.Put(from, _shown.Key, before);
            var provisional = _shots.Get(to, rendered.Key);

            // The closed phone's hinge line in its composite: the frame's left padding and hinge barrel, at this scale.
            var closedFrame = IPhoneDuo.IsInner(from) ? frame : Frame!;
            var closedPicture = IPhoneDuo.IsInner(from) ? picture.Width : _pictureOnScreen.Width;
            float hinge = (closedFrame.Screen.X - DuoFrames.Border) * (float)closedPicture / closedFrame.Screen.Width;

            string? dump = _app.Config.DuoAnimationDump is { Length: > 0 } folder ? Path.Combine(folder, $"{DateTime.Now:HHmmss}-{from}-{to}") : null;
            var animation = new FoldAnimation(Main, from, to, new FoldAnimation.End(before, beforeScreen), new FoldAnimation.End(placeholder, afterScreen), hinge, dump,
                provisional);
            animation.Landed += () => _app.Post(() => OnAnimationLanded(animation));
            // The live device goes once the animation is over it: hiding it first left a frame or two with neither.
            animation.Shown += () => _app.Post(() =>
            {
                if (_animation == animation) Sync();
            });
            _animation = animation;
            _animationTarget = to;
            _afterCaptured = false;
            _afterNotBefore = DateTime.MaxValue;
            _afterShownAt = null;
            animation.Start();
            Log.Write($"'{Title}': animating {from} -> {to}{(provisional != null ? $" ({to} as last seen until it is live)" : "")}{(dump != null ? $" (frames to {dump})" : "")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"'{Title}': fold animation could not start: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A device image: the frame with the screen filled from the emulator window (PrintWindow sees it even while it is
    /// hidden) or dark when <paramref name="picture"/> is null, and the glass layer, premultiplied.
    /// </summary>
    private Texture? Composite(RenderedFrame frame, RECT wr, Rectangle? picture) => Composite(frame, wr, picture, out _);

    /// <param name="blank">The screen came out pure black: Android blanks it for a moment while it switches displays.</param>
    private Texture? Composite(RenderedFrame frame, RECT wr, Rectangle? picture, out bool blank)
    {
        var pixels = new byte[frame.Width * frame.Height * 4];
        long screenPixels = 0, black = 0;
        Bitmap? window = null;
        System.Drawing.Imaging.BitmapData? data = null;
        try
        {
            if (picture is { } shot)
            {
                window = Diagnostics.PrintEmulator(Main, wr);
                data = window.LockBits(new Rectangle(0, 0, window.Width, window.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            foreach (var (y, x0, x1) in frame.ScreenRuns)
            {
                int dy = frame.RenderOffset.Y + y;
                if (dy < 0 || dy >= frame.Height) continue;
                for (int x = x0; x < x1; x++)
                {
                    int dx = frame.RenderOffset.X + x;
                    if (dx < 0 || dx >= frame.Width) continue;
                    int p = (dy * frame.Width + dx) * 4, color = unchecked((int)0xFF0B0B0D);
                    if (data != null && picture is { } at)
                    {
                        int sx = at.X - wr.Left + x, sy = at.Y - wr.Top + y;
                        if (sx >= 0 && sy >= 0 && sx < data.Width && sy < data.Height) color = System.Runtime.InteropServices.Marshal.ReadInt32(data.Scan0, sy * data.Stride + sx * 4);
                        screenPixels++;
                        if ((color & 0xFFFFFF) == 0) black++;
                    }
                    pixels[p] = (byte)color; pixels[p + 1] = (byte)(color >> 8); pixels[p + 2] = (byte)(color >> 16); pixels[p + 3] = 255;
                }
            }
        }
        finally
        {
            if (data != null) window!.UnlockBits(data);
            window?.Dispose();
        }
        if (frame.Glass is { } glass)
            for (int y = 0; y < glass.Bounds.Height; y++)
                for (int x = 0; x < glass.Bounds.Width; x++)
                {
                    int s = (y * glass.Bounds.Width + x) * 4, a = glass.Pixels[s + 3];
                    if (a == 0) continue;
                    int p = ((glass.Bounds.Y + y) * frame.Width + glass.Bounds.X + x) * 4;
                    for (int c = 0; c < 4; c++) pixels[p + c] = (byte)(glass.Pixels[s + c] + pixels[p + c] * (255 - a) / 255);
                }
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            int a = frame.Pixels[i * 4 + 3];
            if (a == 0) continue;
            int p = i * 4;
            for (int c = 0; c < 4; c++) pixels[p + c] = (byte)(frame.Pixels[p + c] + pixels[p + c] * (255 - a) / 255);
        }
        blank = screenPixels > 0 && black > screenPixels * 0.97;
        return new Texture(pixels, frame.Width, frame.Height);
    }

    /// <summary>While the animation plays: keep the emulator hidden, and give it the new picture once it is there.</summary>
    private void FeedAnimation(RenderKey key, RECT wr, RECT rr, Rectangle picture, bool settling)
    {
        if (!_animation!.OnScreen) return; // the live device stays until the animation covers it (Shown syncs again)
        // The animation is the device for now: the frame and the live picture go away under it (the emulator itself
        // in KeepHiddenUnderAnimation).
        _overlay?.SetVisible(false);
        _glass?.SetVisible(false);
        _chrome?.SetVisible(false);
        _fade?.SetVisible(false);
        // Nothing for the old pose and nothing on a size on the way: the frame that matters is the new pose's, once
        // Android has switched and MuMu has finished turning its window for it.
        if (settling || Pose != _animationTarget) return;
        if (_current == null || _current.Key != key)
        {
            if (_pending != key && Frame != null) StartRender(Frame, key);
            return;
        }
        if (KeepPut(wr, rr, picture)) return; // hidden under the animation: put it where it will land first
        var screen = new RectangleF(rr.Left + picture.X - _current.RenderOffset.X, rr.Top + picture.Y - _current.RenderOffset.Y, _current.Width, _current.Height);
        bool arrived = Pose == _animationTarget && _animationTarget is { } target && IPhoneDuo.PictureInRender(target, RenderSize) != null;
        if (!arrived || DateTime.UtcNow < _afterNotBefore) return;
        // Hidden behind its one-pixel region, the emulator stops giving PrintWindow its new pictures (it keeps the one
        // from before), so the capture came out black. Once the animated device rests where it lands, show the
        // emulator's screen again under it (the animation's dark screen covers it exactly), on every pass in case
        // something took the region away, and take the picture from there.
        if (!_animation.AtRest) return; // the 150 ms tick comes back
        bool first = _afterShownAt == null;
        _afterShownAt ??= DateTime.UtcNow;
        _hiddenUnderAnimation = false;
        if (_app.Config.ClipToScreen && !Peek) ApplyRegion(new Point(rr.Left - wr.Left + picture.X, rr.Top - wr.Top + picture.Y));
        else if (first) EmulatorClip.Remove(Main); // unclipped, as the live view shows it
        if (_afterCaptured)
        {
            _animation.Retarget(screen);
            return;
        }
        if (first) return; // MuMu draws into it first
        _pictureOnScreen = new Rectangle(rr.Left + picture.X, rr.Top + picture.Y, picture.Width, picture.Height);
        // Android blanks the screen for a moment as it switches (and for longer when the safe areas are refreshed with
        // the display off and on); take the picture once there is one (the 150 ms tick keeps coming back here), or after
        // a second and a half whatever it shows.
        if (Composite(_current, wr, _pictureOnScreen, out bool blank) is { } after && (!blank || DateTime.UtcNow > _afterShownAt.Value.AddSeconds(1.5)))
        {
            _afterCaptured = true;
            _animation.SetAfter(after, screen);
            if (!blank && _animationTarget is { } landed) _shots.Put(landed, _current.Key, after);
        }
    }

    /// <summary>
    /// While the animation is the device, until the new picture is wanted, the emulator stays hidden behind a one-pixel
    /// region. Checked first thing on every pass, and kept cheap: MuMu takes the region away each time it resizes its
    /// window to turn it (two or three times per turn), and the theming layer can answer ours with its own; MuMu's whole
    /// window shows until the next pass. A layered, fully transparent window would not care, but MuMu takes the layered
    /// style away again within milliseconds.
    /// </summary>
    private void KeepHiddenUnderAnimation()
    {
        if (_animation is not { OnScreen: true } || _afterShownAt != null || EmulatorClip.IsHidden(Main) || !GetWindowRect(Main, out var wr)) return;
        var centre = GetWindowRect(Render, out var rr)
            ? new Point((rr.Left + rr.Right) / 2 - wr.Left, (rr.Top + rr.Bottom) / 2 - wr.Top)
            : new Point(wr.Width / 2, wr.Height / 2);
        _region = null;
        _hiddenUnderAnimation = true;
        EmulatorClip.Hide(Main, centre);
        StartVerifyBurst(); // look again shortly: the theming layer answers a region change
    }

    private void OnAnimationLanded(FoldAnimation animation)
    {
        if (_animation != animation) return;
        _animation = null;
        _shown = null;   // put the frame bitmap in again, at full opacity
        _fadeNext = false;
        Sync();          // the live frame and picture, under the animation's last frame...
        animation.FadeOut(); // ...which fades away over them
        StartWantedPose();
    }

    /// <summary>The Duo frames for every pose at this window size, rendered ahead so a switch never waits for one.</summary>
    private void WarmDuoFrames()
    {
        if (_warmed || RenderSize.Width <= 0) return;
        _warmed = true;
        var (library, size, overlap) = (_app.Library, RenderSize, Math.Clamp(_app.Config.Overlap, 0, 8));
        Task.Run(() =>
        {
            foreach (var pose in IPhoneDuo.Poses)
            {
                var render = IPhoneDuo.TurnsWindow(pose) == size.Width < size.Height ? size : new Size(size.Height, size.Width);
                if (library.Find(IPhoneDuo.FrameKey(pose)) is { } frame && IPhoneDuo.PictureInRender(pose, render) is { } picture)
                    FrameRenderer.Render(frame, new RenderKey(frame.Key, picture.Width, picture.Height, overlap));
            }
        });
    }

    /// <summary>The pose buttons beside a Duo frame: where the device ends on screen, the pose, what is available.</summary>
    private void EnsurePosePanel()
    {
        if (!IPhoneDuo.IsDuo(Frame) || !IsDuoSurface || !_app.Config.DuoPoseControls || Frame == null)
        {
            _panel?.Hide();
            return;
        }
        if (_panel is not { IsAlive: true })
        {
            try
            {
                _panel = new DuoPosePanel(Main, _app.PoseHotkey, pose => SetDuoPose(pose, "the pose panel"));
                UpdateCompanions();
            }
            catch (Exception ex)
            {
                Log.Write($"Pose panel failed for '{Title}': {ex.Message}");
                return;
            }
        }
        // The device's outline: the picture plus the bezel (and the hinge barrel on the closed phone's left).
        double sx = (double)_pictureOnScreen.Width / Frame.Screen.Width;
        int border = (int)Math.Round(DuoFrames.Border * sx), hinge = (int)Math.Round(DuoFrames.HingeOut * sx);
        int left = Pose == DuoPose.Folded ? hinge : 0, top = Pose == DuoPose.Tent ? hinge : 0; // the barrel: left closed, on top in tent
        var device = Rectangle.FromLTRB(_pictureOnScreen.Left - border - left, _pictureOnScreen.Top - border - top, _pictureOnScreen.Right + border, _pictureOnScreen.Bottom + border);
        uint dpi = GetDpiForWindow(Main);
        _panel.Show(device, _animationTarget is { } going && _animation != null ? going : Pose, IsFoldable, dpi == 0 ? 1f : dpi / 96f);
        WarmDuoFrames();
    }

    private void UpdateCompanions()
    {
        if (_overlay != null) _overlay.Companions = [_bar, _glass, _panel?.Window];
    }

    /// <summary>
    /// A short cross-fade from the previous frame to the new one: the old bitmap stays up in a second window and fades
    /// out while the new frame fades in, about a fifth of a second.
    /// </summary>
    private void CrossFade(RenderedFrame previous, Point previousPosition)
    {
        if (_overlay is not { IsAlive: true } overlay) return;
        try
        {
            if (_fade is not { IsAlive: true }) _fade = new OverlayWindow(Main, interactive: false);
        }
        catch
        {
            overlay.SetOpacity(255);
            return;
        }
        _fade.SetFrame(previous, previousPosition, 255);
        _fade.SetVisible(true);
        _fadeStart = DateTime.UtcNow;
        if (_fadeTimer == null)
        {
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _fadeTimer.Tick += (_, _) =>
            {
                double t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalMilliseconds / 180.0, 0, 1);
                double eased = t * t * (3 - 2 * t);
                _overlay?.SetOpacity((byte)Math.Round(255 * eased));
                _fade?.SetOpacity((byte)Math.Round(255 * (1 - eased)));
                if (t < 1) return;
                _fadeTimer!.Stop();
                _fade?.SetVisible(false);
            };
        }
        _fadeTimer.Start();
    }

    /// <summary>
    /// MuMu letterboxes its picture inside its window when the picture's shape changes without MuMu resizing the window
    /// itself: a rotation with its window auto-rotate off, or a new resolution after a restart (it restores the old
    /// window size). Until something resizes the window (a drag) the device sits small in a wrong-shaped window. This
    /// does what the drag would: the window is resized around its centre to fit the picture, keeping its longer side,
    /// so a turn swaps the window round at the same scale. True when it resized the window.
    /// </summary>
    private bool FitMuMuWindow(RECT wr, RECT rr)
    {
        int left = rr.Left - wr.Left, right = wr.Right - rr.Right, top = rr.Top - wr.Top, bottom = wr.Bottom - rr.Bottom;
        // Normal MuMu: 4 px sides, the title bar and toolbar above, a few px below.
        if (Peek || !WindowFit.IsLetterboxed(left, right, top, bottom))
        {
            _letterboxSince = null;
            return false;
        }
        var picture = new Size(rr.Width, rr.Height);
        // MuMu letterboxes for a moment in the middle of turning its own window: only a letterbox that stays is ours to fix.
        if (_letterboxSince == null || _letterboxSize != picture)
        {
            (_letterboxSince, _letterboxSize) = (DateTime.UtcNow, picture);
            StartVerifyBurst(20); // look again shortly
            return false;
        }
        if (DateTime.UtcNow - _letterboxSince < TimeSpan.FromMilliseconds(700)) return false;
        if (picture == _fittedRender && DateTime.UtcNow < _nextWindowFit) return false;
        _fittedRender = picture;
        _nextWindowFit = DateTime.UtcNow.AddSeconds(2);
        var work = Screen.FromRectangle(Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom)).WorkingArea;
        var target = WindowFit.Fit(new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height), picture, left, right, top, bottom, work);
        Log.Write($"'{Title}': MuMu letterboxes its {rr.Width}x{rr.Height} picture in a {wr.Width}x{wr.Height} window; resizing it to {target.Width}x{target.Height}");
        SetWindowPos(Main, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        StartVerifyBurst(40);
        return true;
    }

    /// <summary>Puts the current frame (and its glass layer, if any) at <paramref name="position"/> and shows it.</summary>
    private void ShowOverlay(Point position)
    {
        var overlay = EnsureOverlay();
        if (overlay == null || _current == null) return;
        if (_shown != _current)
        {
            // A pose switch fades from the old frame to the new one; anything else (first show, resize) just swaps.
            var (previous, previousPosition) = (_shown, overlay.Position);
            bool fade = _fadeNext && previous != null && IPhoneDuo.IsDuo(Frame);
            _fadeNext = false;
            overlay.SetFrame(_current, position, fade ? (byte)0 : (byte)255);
            _shown = _current;
            if (fade) CrossFade(previous!, previousPosition);
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
                UpdateCompanions();
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
        bool wanted = _app.Config.IosStatusBarFor(Title, Frame) && Instance != null && Display != null && Frame != null && !IPhoneDuo.IsDuo(Frame);
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
            var spec = AndroidCutout.Measure(Frame!, Display!, _app.Config.StatusBarPaddingFactor, ios: true);
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
                UpdateCompanions();
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
        if (StartFit(config)) return;

        if (IsDuoSurface)
        {
            // A Duo frame was picked for an emulator that is not in a pose yet: put it in that frame's pose (once).
            if (Pose == null && IPhoneDuo.PoseOf(Frame) is { } framePose && !_autoPoseTried)
            {
                _autoPoseTried = true;
                StartPoseSwitch(framePose);
                return;
            }
            // A foldable without the postures overlay reports device states but no FoldingFeature: install it (once).
            if (Display.Duo is { Foldable: true, Postures: false } && !_posturesTried)
            {
                _posturesTried = true;
                InstallPostures();
                return;
            }
            // The watchdog: Android's state, MuMu's window and the safe areas, every second (KeepPose).
            if (DateTime.UtcNow >= _nextPosePoll)
            {
                PollPose();
                return;
            }
        }

        if (!_app.Config.CutoutFor(Title, Frame) || IPhoneDuo.IsDuo(Frame))
        {
            // Switched off (here or globally), or an iPhone Duo, whose app draws its own rail: take the overlays back out.
            if (config.InstalledCutout != null) RemoveCutout();
            return;
        }

        bool iosBar = _app.Config.IosStatusBarFor(Title, Frame);
        bool wantsHome = config.IosHomeIndicator != false;
        string signature = AndroidCutout.Signature(Frame, Display, _app.Config.StatusBarPaddingFactor, iosBar, wantsHome);
        if (signature == config.InstalledCutout || signature == _failedCutout) return;
        // Changing the overlay of a running emulator relaunches its apps: an emulator that already has one gets the new
        // one when Android starts (Android has been up for under three minutes), or at "Restart to finish iOS setup".
        bool queue = config.InstalledCutout != null;
        if (queue && config.PendingIosSetup == signature && DateTime.UtcNow < _nextPendingCheck) return;
        _nextPendingCheck = DateTime.UtcNow.AddMinutes(1);

        var (frame, display2, instance2, factor, sdk) = (Frame, Display, Instance, _app.Config.StatusBarPaddingFactor, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            if (queue && UptimeSeconds(instance2) is not (< 180))
                return () =>
                {
                    if (config.PendingIosSetup == signature) return;
                    config.PendingIosSetup = signature;
                    _app.Config.Save();
                    Log.Write($"'{Title}': iOS safe areas for {frame.Model} queued for the next start ({signature})");
                    _app.Notify($"{Title}: the iOS home indicator and its safe area are ready. They go in when the emulator next starts, or now with Restart to finish iOS setup in its menu.");
                };
            var missing = AndroidTools.Missing(sdk);
            if (missing.Count > 0)
                return () =>
                {
                    _failedCutout = signature;
                    Log.Write($"'{Title}': missing Android display tools: {string.Join(", ", missing)}");
                    _app.OfferAndroidTools();
                };
            var spec = AndroidCutout.Measure(frame, display2, factor, ios: iosBar, withHome: wantsHome);
            string? error = spec == null ? "frame and display orientation differ" : AndroidCutout.Apply(instance2, spec, sdk, iosBar);
            return () =>
            {
                if (error == null)
                {
                    config.InstalledCutout = signature;
                    config.PendingIosSetup = null;
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
                AdoptPose();
                _displayAnswered = true;
                if (display != null) Log.Write($"'{Title}': MuMu instance {instance!.Index}, adb {instance.AdbSerial}, display {display.Native.Width}x{display.Native.Height} @ {display.Density} dpi" +
                    (display.Override is { } o ? $", override {o.Width}x{o.Height}" : "") + (Pose is { } p ? $", iPhone Duo {p}" : ""));
                Sync();
            };
        });
    }

    // ---- Fit emulator to frame ----------------------------------------------------------------------------------

    /// <summary>
    /// Makes the emulator the frame's device when it is not (<see cref="DeviceFit"/>): live for a density, with a restart
    /// for a resolution, the iPhone Duo setup for a Duo frame. Each target is tried once every few minutes at most, so a
    /// MuMu that will not take a setting cannot restart-loop. True when something was started.
    /// </summary>
    private bool StartFit(InstanceConfig config)
    {
        if (!config.FitToFrame || Frame is not { } frame || Display is not { } display || Instance is not { } instance) return false;
        if (IPhoneDuo.IsDuo(frame))
        {
            if (IsDuoSurface && IsFoldable || !_app.TryFit(Title, "iPhone Duo")) return false;
            Log.Write($"'{Title}': fitting to iPhone Duo (setup)");
            SetUpAsDuo();
            return true;
        }

        var spec = DeviceSpecs.For(frame);
        var step = DeviceFit.Needed(spec, display);
        if (step == DeviceFit.Step.None || !_app.TryFit(Title, $"{frame.Category}/{frame.Model}")) return false;
        string device = $"{frame.Model}: {spec.Native.Width}×{spec.Native.Height} at {spec.Density} dpi ({spec.Logical.Width}×{spec.Logical.Height} {(frame.Category.StartsWith("Apple") ? "pt" : "dp")})";
        string title = Title;
        Log.Write($"'{title}': fitting to {device}, {(step == DeviceFit.Step.Restart ? "restart" : "live density")}");
        if (step == DeviceFit.Step.Restart) _app.Notify($"{title}: becoming {device}. Restarting the emulator…");
        RunAndroidTask(() =>
        {
            string? error = step == DeviceFit.Step.Restart ? DeviceFit.Restart(instance, spec) : DeviceFit.ApplyDensity(instance, spec, display);
            Log.Write(error == null ? $"'{title}': now {device}" : $"'{title}': fit failed: {error}");
            // The restart can replace the emulator window (and this session): say it from the app.
            _app.Post(() => _app.Notify(error == null ? $"{title} is now {device}." : $"{title}: could not fit to {frame.Model}. {error}", warning: error != null));
            var now = error == null ? MuMuControl.QueryDisplay(instance) : null;
            return () =>
            {
                if (now != null) Display = now;
                Sync();
            };
        });
        return true;
    }

    // ---- iPhone Duo: fold and unfold ----------------------------------------------------------------------------

    /// <summary>Folds or unfolds the iPhone Duo. False (after telling the user why) when this emulator cannot.</summary>
    /// <param name="source">Who asked (the pose panel, the menu, a hotkey, the command line), for the log.</param>
    public bool SetDuoPose(DuoPose pose, string source)
    {
        Log.Write($"'{Title}': {IPhoneDuo.Variant(pose)} asked for by {source}");
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

    /// <summary>Fold or unfold (half open closes).</summary>
    public bool ToggleDuoPose(string source) => SetDuoPose(IPhoneDuo.Toggle(Pose), source);

    /// <summary>Half open like a book, or back to flat open.</summary>
    public bool ToggleDuoHalf(string source) => SetDuoPose(IPhoneDuo.ToggleHalf(Pose), source);

    /// <summary>Android reports device states (made a foldable), so apps get a FoldingFeature.</summary>
    public bool IsFoldable => Display?.Duo?.Foldable == true;

    private DateTime _nextPendingCheck;

    /// <summary>How long Android has been up (seconds), null if it did not say.</summary>
    private static double? UptimeSeconds(MuMuInstance instance) =>
        double.TryParse(instance.Shell("cat /proc/uptime").Output.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>"Restart to finish iOS setup": the emulator restarts and the queued overlay goes in as Android starts.</summary>
    public void RestartToFinishIosSetup()
    {
        if (Instance is not { } instance) return;
        string title = Title;
        _app.Notify($"{title}: restarting to finish the iOS setup.");
        RunAndroidTask(() =>
        {
            string? error = MuMuControl.RestartAndWait(instance, TimeSpan.FromMinutes(3));
            return () =>
            {
                Log.Write(error == null ? $"'{title}': restarted to finish the iOS setup" : $"'{title}': restart failed: {error}");
                _nextPendingCheck = DateTime.MinValue;
                Sync();
            };
        });
    }

    private void InstallPostures()
    {
        var (instance, sdk) = (Instance!, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            string? error = DuoSetup.InstallPostures(instance, sdk);
            return () =>
            {
                Log.Write(error == null ? $"'{Title}': iPhone Duo postures overlay installed" : $"'{Title}': postures overlay failed: {error}");
                if (error != null) _app.Notify($"{Title}: could not install the foldable postures overlay. {error}", warning: true);
                else _nextPosePoll = DateTime.MinValue; // read it back
            };
        });
    }

    private void NotDuo() =>
        _app.Notify($"{Title} is not set up as an iPhone Duo. Use Android display, Set up as iPhone Duo, first.", warning: true);

    private void StartWantedPose()
    {
        if (_wantedPose is not { } pose || Instance == null || Display == null || _animation != null || _androidBusy) return;
        _wantedPose = null;
        if (!IsDuoSurface)
        {
            NotDuo();
            return;
        }
        if (IPhoneDuo.IsHalf(pose) && !IsFoldable)
        {
            _app.Notify($"{Title}: {IPhoneDuo.Variant(pose)} needs the foldable setup (Android display, Set up as iPhone Duo).", warning: true);
            return;
        }
        var choice = _app.Config.Instance(Title);
        choice.DuoPose = pose; // what the app keeps it in, through restarts and MuMu turning the display
        _app.Config.Save();
        _resyncAttempts = 0;
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
    private void StartPoseSwitch(DuoPose pose, bool refreshSafeAreas = false)
    {
        var (instance, display) = (Instance!, Display!);
        var frame = _app.Library.Find(IPhoneDuo.FrameKey(pose));
        // MuMu turns its window for the upright poses and tent (same scale, swapped round): render for where it will be.
        var turned = IPhoneDuo.TurnsWindow(pose) == RenderSize.Width < RenderSize.Height ? RenderSize : new Size(RenderSize.Height, RenderSize.Width);
        RenderKey? key = frame != null && IPhoneDuo.PictureInRender(pose, turned) is { } picture
            ? new RenderKey(frame.Key, picture.Width, picture.Height, Math.Clamp(_app.Config.Overlap, 0, 8))
            : null;
        _nextPosePoll = DateTime.UtcNow.AddSeconds(1);
        // Only a switch between safe-area sets (the rail poses, the upright ones, tent) needs the display off and on
        // (apps pause and resume around it, and some take a moment to redraw), or a device turned outside the app,
        // where Android rotated without reading its safe areas again.
        bool refresh = _app.Config.Instance(Title).DuoOverlay != null
            && (refreshSafeAreas || Pose is not { } before || IPhoneDuo.SafeAreaGroup(before) != IPhoneDuo.SafeAreaGroup(pose));
        bool protect = _app.Config.Instance(Title).DuoProtectSafeAreas;
        bool animating = Pose is { } from && from != pose && BeginAnimation(from, pose);
        _fadeNext = !animating;
        RunAndroidTask(() =>
        {
            var rendered = key is { } k ? FrameRenderer.Render(frame!, k) : null;
            var commands = IPhoneDuo.SwitchCommands(pose, display.Native, display.Duo?.Foldable == true, refresh, protect);
            var (_, output) = instance.Shell(string.Join("; ", commands));
            var now = MuMuControl.WaitForPose(instance, pose);
            return () =>
            {
                if (now != null) Display = now;
                var reached = IPhoneDuo.Detect(Display);
                bool complete = reached == pose && IPhoneDuo.InStep(pose, Display);
                Log.Write($"'{Title}': iPhone Duo -> {pose}: {(complete ? "done" : $"still {reached?.ToString() ?? "no pose"} ({output.Trim()})")}");
                if (!complete)
                    _app.Notify($"{Title}: could not switch the Duo to {IPhoneDuo.Variant(pose)}. {output.Trim()}".TrimEnd(), warning: true);
                SetPose(reached);
                if (reached is { } got && IPhoneDuo.InStep(got, Display)) _resyncAttempts = 0;
                // What the watchdog compares with next: Android in this pose; MuMu's window unknown until it is seen
                // the right way round (it may not follow, for its launcher).
                _seenPose = reached;
                _seenUpright = null;
                _nextPosePoll = DateTime.UtcNow.AddSeconds(1);
                _nextCutoutCheck = DateTime.UtcNow.AddSeconds(1.5);
                // MuMu turns its window within half a second when it follows; when it does not (its home screen in
                // front), turn it then, without waiting for the next look at Android.
                if (reached is { } arrived) _followBy = (DateTime.UtcNow.AddSeconds(0.6), IPhoneDuo.TurnsWindow(arrived));
                if (rendered != null && reached == pose)
                {
                    _pending = null;
                    _current = rendered;
                }
                StartVerifyBurst(40); // MuMu turns or resizes its window a moment after Android: follow it within 50 ms
                if (_animation != null)
                {
                    // Let the app lay itself out for the new display before the picture is taken for the animation.
                    _afterNotBefore = DateTime.UtcNow.AddMilliseconds(140);
                    _afterTimer ??= new System.Windows.Forms.Timer { Interval = 150 };
                    _afterTimer.Tick -= AfterTick;
                    _afterTimer.Tick += AfterTick;
                    _afterTimer.Start();
                }
                Sync();
            };
        });
    }

    private void AfterTick(object? sender, EventArgs e)
    {
        if (_animation == null || _afterCaptured) _afterTimer?.Stop();
        Sync();
    }

    private void PollPose()
    {
        var now = DateTime.UtcNow;
        _nextPosePoll = now + PoseCheck;
        // WindowManager's safe areas: after a switch, while a repair is under way, otherwise every half minute.
        bool cutout = now >= _nextCutoutCheck || _safeAreaRefresh.Tries > 0 && !_safeAreaRefresh.GaveUp;
        if (cutout) _nextCutoutCheck = now + TimeSpan.FromSeconds(30);
        var instance = Instance!;
        RunAndroidTask(() =>
        {
            var display = MuMuControl.QueryDisplay(instance, cutout);
            return () => KeepPose(display);
        });
    }

    private DateTime _nextCutoutCheck;

    // ---- iPhone Duo: the watchdog -------------------------------------------------------------------------------

    /// <summary>How often Android's side is looked at (also straight away when MuMu's window turns).</summary>
    private static readonly TimeSpan PoseCheck = TimeSpan.FromSeconds(1);

    private DuoPose? _seenPose;      // Android's pose at the last look where it and MuMu's window agreed
    private bool? _seenUpright;      // MuMu's window then
    private string? _seenFocus;      // the app in front at the last look
    private double _seenUptime;
    private DateTime _focusChangedAt, _lastWindowTurn;

    /// <summary>
    /// Reading the safe areas again turns the display off and on, which pauses every app: three tries (at once, after
    /// 5 s, after 20 more), then given up on with one notice, until they are right again or the pose changes.
    /// </summary>
    private readonly Backoff _safeAreaRefresh = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
    private DuoPose? _safeAreaPose;
    private bool _safeAreaNoticeShown;
    private DateTime? _disagreeSince;
    private (DateTime At, bool Upright)? _followBy;
    private bool _clipLostLogged, _frameLostLogged;

    /// <summary>
    /// Compares what is with what the pose wants and repairs the difference, every second. Android's state is read in
    /// one adb call (display size and rotation, device state, fold line, safe areas, the app in front, uptime), MuMu's
    /// window from its render size.
    ///
    /// A change made outside the app is a pose change when it is coherent, Android and MuMu's window agreeing: turned
    /// by Android (a script's <c>wm user-rotation</c>, <c>wm size</c> or device state), or MuMu's window turned by
    /// itself with the same app in front (MuMu's rotate button), which turns the device a quarter. Incoherent states are
    /// repaired: MuMu's window not following Android (its home screen, an app it remembers) is turned with MuMu's own
    /// rotate; Android in no pose, or drifted from it (rotation, device state, fold line, stale safe areas), gets the
    /// pose again. Right after attaching or an Android restart there is nothing seen to compare with, so the kept pose
    /// is put back (MuMu turns the display back after a restart) unless the display is the other one altogether.
    /// </summary>
    private void KeepPose(AndroidDisplay? display)
    {
        var now = DateTime.UtcNow;
        if (display?.Duo is not { Current: not null } duo)
        {
            Forget(); // Android is starting (or adb is not answering): nothing to judge yet
            return;
        }
        Display = display;
        if (duo.Uptime is { } uptime)
        {
            if (uptime + 5 < _seenUptime)
            {
                Log.Write($"'{Title}': Android restarted; putting the iPhone Duo back");
                Forget();
                _gesturesChecked = false; // MuMu switches the gestures off at every boot
            }
            _seenUptime = uptime;
        }
        if (duo.Focus != _seenFocus)
        {
            if (_seenFocus != null) _focusChangedAt = now;
            _seenFocus = duo.Focus;
        }
        if (Peek || _animation != null) return; // hands off while the user peeks at MuMu's own window, or a switch plays

        var config = _app.Config.Instance(Title);
        var kept = config.DuoPose;
        var android = IPhoneDuo.Detect(display);
        bool upright = RenderSize.Width < RenderSize.Height;
        if (android is not { } shown)
        {
            if (kept is { } want) Reassert(want, $"Android shows no pose ({duo.Current?.Width}x{duo.Current?.Height})");
            return;
        }
        if (Pose != shown) SetPose(shown); // the frame follows what Android shows

        if (IPhoneDuo.TurnsWindow(shown) != upright)
        {
            // Android and MuMu's window disagree about the way round.
            bool androidTurned = _seenPose is { } before && before != shown;
            bool windowTurned = !androidTurned && _seenUpright is { } wasUpright && wasUpright != upright;
            if (windowTurned && now - _focusChangedAt > TimeSpan.FromSeconds(2))
            {
                Adopt(IPhoneDuo.Turned(shown), "MuMu's window was turned");
                return;
            }
            // MuMu did not follow Android (its home screen, or an app it remembers the other way round): turn it.
            _disagreeSince ??= now;
            if (now - _disagreeSince >= TimeSpan.FromSeconds(0.8))
                TurnMuMuWindow($"MuMu kept its window {(upright ? "upright" : "landscape")} for {shown} ({duo.Focus ?? "?"} in front)");
            if (androidTurned && kept is { } wanted && wanted != shown && IPhoneDuo.IsInner(shown) == IPhoneDuo.IsInner(wanted))
            {
                // Android itself was turned (a script's wm user-rotation): that is the new pose. MuMu's window is turned
                // too if it has not followed by the time the switch is under way.
                Adopt(shown, "Android was turned");
                _followBy = (DateTime.UtcNow.AddSeconds(0.6), IPhoneDuo.TurnsWindow(shown));
            }
            return;
        }
        _disagreeSince = null;

        if (kept is not { } target)
        {
            Adopt(shown, "first pose seen");
            return;
        }
        if (shown != target)
        {
            // Seen in the kept pose during this session: it was turned just now, outside the app. With nothing seen
            // yet (attached, Android restarted) only a change of display counts; a turn is MuMu's own doing.
            if (_seenPose == target || IPhoneDuo.IsInner(shown) != IPhoneDuo.IsInner(target)) Adopt(shown, "changed outside the app");
            else Reassert(target, $"came back as {shown}");
            return;
        }
        _seenPose = shown;
        _seenUpright = upright;
        if (!IPhoneDuo.InStep(target, display))
        {
            Reassert(target, $"out of step (rotation {duo.Rotation}, device state {duo.FoldState}, fold line {duo.Features})");
            return;
        }
        _resyncAttempts = 0;
        // The safe areas apps get: stale when Android turned without reading them again. The display off and on, a
        // few times at most (_safeAreaRefresh): it pauses every app, and when it does not help it must not go on.
        if (_safeAreaPose != target)
        {
            _safeAreaPose = target;
            _safeAreaRefresh.Reset();
            _safeAreaNoticeShown = false;
        }
        if (config.DuoOverlay == null || duo.Cutout is not { } cutout || DuoSafeAreas.For(display.DensityOverride ?? display.Density, display.Native) is not { } areas) return;
        bool protect = config.DuoProtectSafeAreas;
        if (areas.Matches(target, duo, protect))
        {
            _safeAreaRefresh.Reset();
            _safeAreaNoticeShown = false;
            return;
        }
        if (Instance is not { } inst) return;
        if (_safeAreaRefresh.TryNow(now))
        {
            // First the forced size again (WindowManager lost them, typically after Android's framework restarted; no
            // blink), then that and the display off and on (Android turned without reading them again).
            int attempt = _safeAreaRefresh.Tries;
            bool barOnly = cutout == areas.Insets(target) && (!protect || duo.SafeAreaProtection == true && duo.SafeContent == areas.ContentBounds(target));
            string commands = barOnly ? IPhoneDuo.RefreshStatusBar(target, display.Native) : display.Override is { } forced
                ? IPhoneDuo.ForceSizeAgain(forced) + (attempt > 1 ? "; sleep 0.3; " + IPhoneDuo.RefreshSafeAreas : "")
                    + (protect ? "; " + IPhoneDuo.RefreshSafeBounds(forced) : "")
                    + "; " + IPhoneDuo.RefreshStatusBar(target, display.Native)
                : IPhoneDuo.RefreshSafeAreas;
            Log.Write($"'{Title}': safe areas {cutout}, app bounds {duo.SafeContent}, status bar {duo.StatusBarHeight}px are not {target}'s {areas.Insets(target)} / {areas.ContentBounds(target)}; " +
                $"{(attempt > 1 ? "forcing the display size again and reading them again" : "forcing the display size again")} (try {attempt} of 3)");
            RunAndroidTask(() =>
            {
                if (protect && duo.SafeAreaProtection != true && DuoSetup.EnsureSafeAreaSupport(inst) is { } error)
                    Log.Write($"'{Title}': display-area protection: {error}");
                inst.Shell(commands);
                return () => _nextPosePoll = DateTime.MinValue;
            });
        }
        else if (_safeAreaRefresh.GaveUp && !_safeAreaNoticeShown)
        {
            _safeAreaNoticeShown = true;
            Log.Write($"'{Title}': safe areas are still {cutout}, not {target}'s {areas.Insets(target)}, after {_safeAreaRefresh.Tries} tries; leaving them until the pose changes");
            _app.Notify($"{Title}: iPhone Duo safe areas did not come back. Check iPhone Duo… repairs it.", warning: true);
        }
    }

    /// <summary>MuMu's own rotate, for its window only (Android stays as it is); at most every two seconds.</summary>
    private void TurnMuMuWindow(string why)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWindowTurn < TimeSpan.FromSeconds(2) || Instance is not { } instance) return;
        _lastWindowTurn = now;
        string title = Title;
        Log.Write($"'{title}': {why}; turning it");
        // MuMuManager, not adb: alongside a pose switch that may be running, so the window turns while Android does.
        Task.Run(() =>
        {
            string? error;
            try { error = MuMuControl.TurnWindow(instance); }
            catch (Exception ex) { error = ex.Message; }
            _app.Post(() =>
            {
                if (_disposed) return;
                if (error != null) Log.Write($"'{title}': MuMu's rotate failed: {error}");
                _disagreeSince = null;
                StartVerifyBurst(40);
            });
        });
    }

    /// <summary>Nothing seen to compare with (attached, Android restarting).</summary>
    private void Forget()
    {
        _seenPose = null;
        _seenUpright = null;
        _disagreeSince = null;
        _safeAreaRefresh.Reset();
        _safeAreaNoticeShown = false;
        _duoChromeRetry.Reset();
        // Android's framework restarting is what loses WindowManager's safe areas: look at them soon after it is back.
        _nextCutoutCheck = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>A pose reached from outside becomes the one kept, and gets everything it needs (fold line, safe areas).</summary>
    private void Adopt(DuoPose pose, string why)
    {
        var config = _app.Config.Instance(Title);
        Log.Write($"'{Title}': iPhone Duo {config.DuoPose?.ToString() ?? "no pose"} -> {pose} ({why})");
        config.DuoPose = pose;
        _app.Config.Save();
        _resyncAttempts = 0;
        _seenPose = pose;
        _seenUpright = IPhoneDuo.TurnsWindow(pose);
        StartPoseSwitch(pose, refreshSafeAreas: true);
    }

    /// <summary>Puts the kept pose back: right away, then a few more tries two seconds apart until it holds.</summary>
    private void Reassert(DuoPose pose, string why)
    {
        if (_resyncAttempts >= 5 || DateTime.UtcNow < _nextResync) return;
        _resyncAttempts++;
        _nextResync = DateTime.UtcNow.AddSeconds(2);
        Log.Write($"'{Title}': iPhone Duo {why}; applying {pose} again");
        StartPoseSwitch(pose, refreshSafeAreas: true);
    }

    /// <summary>
    /// The pose to keep: the one last chosen, unless the display itself (outer or inner, set by <c>wm size</c>) was
    /// changed from outside, which is taken as the new choice. Rotation and device state drift is healed instead.
    /// </summary>
    private void AdoptPose()
    {
        var choice = _app.Config.Instance(Title);
        if (Pose is not { } shown || choice.DuoPose is { } kept && IPhoneDuo.IsInner(kept) == IPhoneDuo.IsInner(shown)) return;
        if (choice.DuoPose != null) Log.Write($"'{Title}': iPhone Duo display changed outside the app: {choice.DuoPose} -> {shown}");
        choice.DuoPose = shown;
        _app.Config.Save();
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

    /// <summary>
    /// Makes the emulator the iPhone Duo (2670 × 2034 at 480 dpi, an Android foldable with the postures overlay), with up
    /// to two restarts the first time (see <see cref="DuoSetup"/>), and leaves it folded.
    /// </summary>
    public void SetUpAsDuo()
    {
        if (Instance is not { } instance) return;
        var (title, sdk) = (Title, _app.Config.AndroidSdk);
        _app.Notify($"{title}: setting up the iPhone Duo. The emulator may restart twice.");
        RunAndroidTask(() =>
        {
            string? error = DuoSetup.Run(instance, title, sdk, _ => { });
            return () =>
            {
                Display = null;
                Pose = null;
                _autoPoseTried = _posturesTried = false;
                _resyncAttempts = 0;
                _displayAnswered = false;
                _app.Notify(error == null ? $"{title} is an iPhone Duo now: fold, unfold and half open from its menu." : $"{title}: iPhone Duo setup failed. {error}", warning: error != null);
                Sync();
            };
        });
    }

    /// <summary>The iPhone Duo health check (and repairs), in turn with this emulator's other adb work.</summary>
    public void RunDoctor(Action<List<DuoDoctor.Finding>> done)
    {
        if (Instance is not { } instance) return;
        var settings = _app.Config.Instance(Title);
        var (title, pose, sdk) = (Title, settings.DuoPose, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            var findings = DuoDoctor.Run(instance, title, pose, sdk, repair: true, settings);
            var display = MuMuControl.QueryDisplay(instance);
            return () =>
            {
                if (display != null) { Display = display; Pose = IPhoneDuo.Detect(display); }
                _resyncAttempts = 0;
                _app.Config.Save();
                _safeAreaRefresh.Reset();
                _safeAreaNoticeShown = false;
                _duoChromeRetry.Reset();
                done(findings);
                Sync();
            };
        });
    }

    /// <summary>Retry after a failure, or rebuild on request.</summary>
    public void RetryCutout()
    {
        _failedCutout = null;
        _duoChromeRetry.Reset();
        _duoChromeNotice = false;
        _safeAreaRefresh.Reset();
        _safeAreaNoticeShown = false;
        _gesturesChecked = false;
        _nextCutoutCheck = DateTime.MinValue;
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
        if (_androidBusy) { _androidTasks.Enqueue(work); return; }
        _androidBusy = true;
        var instance = Instance;
        Task.Run(() =>
        {
            Action? done = null;
            try
            {
                using var operation = instance?.ExclusiveOperation();
                done = work();
            }
            catch (Exception ex) { Log.Write($"'{Title}': Android task failed: {ex.Message}"); }
            _app.Post(() =>
            {
                try { if (!_disposed) done?.Invoke(); }
                finally
                {
                    _androidBusy = false;
                    if (!_disposed)
                    {
                        if (_androidTasks.TryDequeue(out var next)) RunAndroidTask(next);
                        else StartWantedPose(); // the latest pose asked for while Android was busy
                    }
                }
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
            _overlay = new OverlayWindow(Main, _app.Config.BezelInteractive) { Companions = [_bar, _glass, _panel?.Window] };
            _overlay.MenuRequested += () => _app.ShowSessionMenu(this);
            _overlay.DragEnded += () => { _reanchor = true; Sync(); };
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
            // Right after attaching, the theming layer answers our DWM change with a region of its own between our
            // SetWindowRgn and the check. Try again in a moment rather than waiting for the next scan.
            if (++_clipFailures <= 3 || _clipFailures % 20 == 0) Log.Write($"Clipping failed for '{Title}' (try {_clipFailures}); retrying");
            StartVerifyBurst();
            return;
        }
        _clipFailures = 0;
        _hiddenUnderAnimation = false;
        PlacePictureSoon();
        bool first = _region == null;
        _region = (renderOffsetInWindow, _current.Key, box);

        // The theming layer answers our DWM change by installing its own region a moment later, replacing ours.
        // Re-check in quick succession right after the first application; the regular scan covers the rest.
        if (first) StartVerifyBurst();
    }

    private void StartVerifyBurst(int ticks = 12)
    {
        _verifyTicks = Math.Max(_verifyTicks, ticks);
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
        if (_region == null && !_hiddenUnderAnimation) return; // also when it goes (or the app exits) mid-animation
        _region = null;
        _hiddenUnderAnimation = false;
        _verifyTimer?.Stop();
        EmulatorClip.Remove(Main);
        PlacePictureSoon();
    }

    /// <summary>
    /// After a region change the compositor shows MuMu's picture 4 px off until the window moves
    /// (<see cref="EmulatorClip.PlacePictureAgain"/>). The clip already moves it once straight away; frame changes that
    /// come after ours (the theming layer's region, DWM) are caught by two more moves, 150 and 500 ms after the last
    /// region change.
    /// </summary>
    private void PlacePictureSoon()
    {
        _placeStep = 0;
        if (_placeTimer == null)
        {
            _placeTimer = new System.Windows.Forms.Timer();
            _placeTimer.Tick += (_, _) =>
            {
                if (_disposed || IsGone)
                {
                    _placeTimer?.Stop();
                    return;
                }
                EmulatorClip.PlacePictureAgain(Main);
                if (++_placeStep >= 2) _placeTimer!.Stop();
                else _placeTimer!.Interval = 350;
            };
        }
        _placeTimer.Stop();
        _placeTimer.Interval = 150;
        _placeTimer.Start();
    }

    /// <summary>Gives the emulator its normal look back and hides the frame (keeps the rendered bitmap for later).</summary>
    public void Suspend()
    {
        _laidOut = null;
        _overlay?.SetVisible(false);
        _glass?.SetVisible(false);
        _chrome?.SetVisible(false);
        _panel?.Hide();
        _fade?.SetVisible(false);
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
        _placeTimer?.Dispose();
        _placeTimer = null;
        if (_anchorSave is { Enabled: true }) _app.Config.Save();
        _anchorSave?.Dispose();
        _anchorSave = null;
        _barTimer?.Dispose();
        _barTimer = null;
        if (_bar is { IsAlive: true }) _bar.DestroyHandle();
        _bar = null;
        if (_glass is { IsAlive: true }) _glass.DestroyHandle();
        _glass = null;
        _panel?.Dispose();
        _panel = null;
        _chromeTimer?.Dispose();
        _chromeTimer = null;
        if (_chrome is { IsAlive: true }) _chrome.DestroyHandle();
        _chrome = null;
        if (_duoBarHidden && Instance is { } duoInstance)
        {
            _duoBarHidden = false;
            try { AndroidStatusBar.Restore(duoInstance); } catch (Exception ex) { Log.Write($"'{Title}': status bar restore failed: {ex.Message}"); }
        }
        _animation?.Dispose();
        _animation = null;
        _afterTimer?.Dispose();
        _afterTimer = null;
        _fadeTimer?.Dispose();
        _fadeTimer = null;
        if (_fade is { IsAlive: true }) _fade.DestroyHandle();
        _fade = null;
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
