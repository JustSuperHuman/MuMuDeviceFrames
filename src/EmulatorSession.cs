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
    private bool _chromeDark, _chromePolling, _duoBarHidden;
    private OverlayWindow? _fade;  // the previous frame, fading out over a pose switch
    private System.Windows.Forms.Timer? _fadeTimer;
    private DateTime _fadeStart;
    private bool _fadeNext;
    private Rectangle _pictureOnScreen;
    private FoldAnimation? _animation;  // the fold animation covering a pose switch
    private DuoPose? _animationTarget;
    private bool _afterCaptured;
    private DateTime _afterNotBefore;
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
    /// emptied too; insets stay as they are (the app reserves the rail).
    /// </summary>
    private ChromeState? CurrentChrome()
    {
        if (Frame is not { } frame || Display is not { } display || !_app.Config.IosStatusBarFor(Title, frame)) return null;
        bool duo = IPhoneDuo.IsDuo(frame);
        if (duo && Pose == null) return null;
        bool home = IosChrome.HomeIndicator(frame);
        var kind = IosChrome.KindFor(frame, Pose);
        if (kind == ChromeKind.None && !home) return null;
        var screen = duo && Pose is { } pose ? IPhoneDuo.ScreenSize(pose) : display.Native;
        return new ChromeState(kind, home, screen, display.DensityOverride ?? display.Density, _barApplied ? _barDark : _chromeDark,
            IosStatusBar.ClockText(_app.Config.StatusBarClock));
    }

    private void EnsureChrome()
    {
        var state = CurrentChrome();
        bool duo = IPhoneDuo.IsDuo(Frame) && state != null;
        if (duo != _duoBarHidden && Instance is { } instance && !_androidBusy)
        {
            // The Duo's iOS chrome replaces Android's status bar content in every pose.
            _duoBarHidden = duo;
            var sdk = _app.Config.AndroidSdk;
            RunAndroidTask(() =>
            {
                string? error = null;
                if (duo)
                {
                    AndroidStatusBar.Hide(instance);
                    error = AndroidCutout.ApplyDuo(instance, sdk);
                }
                else
                {
                    AndroidStatusBar.Restore(instance);
                }
                bool dark = AndroidStatusBar.WantsDarkGlyphs(instance);
                return () =>
                {
                    _chromeDark = dark;
                    Log.Write(error == null ? $"'{Title}': Android status bar {(duo ? "emptied for the iPhone Duo's chrome" : "back")}" : $"'{Title}': Duo status bar overlay: {error}");
                    Sync();
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
        if (_chromePolling || _barApplied || Instance is not { } instance) return;
        _chromePolling = true;
        Task.Run(() =>
        {
            bool dark = false;
            try { dark = AndroidStatusBar.WantsDarkGlyphs(instance); } catch { /* keep the last answer */ }
            _app.Post(() => { _chromePolling = false; _chromeDark = dark; });
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
        var target = DeviceAnchor.Place(frame.Size, anchor, Screen.FromPoint(anchor).WorkingArea);
        int dx = target.X - frame.X, dy = target.Y - frame.Y;
        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
        {
            _anchorMoves = 0;
            return false;
        }
        // A window that will not go where it is put (MuMu clamping it, say) gets a few tries, then is left be.
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
        // Minimised or hidden: Windows hides the owned frame windows itself, and Sync leaves that alone.
        if (_disposed || _laidOut is not { } laid || _animation != null || IsGone || IsIconic(Main) || !IsWindowVisible(Main)) return;
        bool moved = !GetWindowRect(Main, out var wr) || !GetWindowRect(Render, out var rr) || !Same(wr, laid.Window) || !Same(rr, laid.Render);
        bool lost = _overlay is { IsAlive: true } overlay && !overlay.IsAsSet();
        if (!moved && !lost) return;
        if (lost)
        {
            Log.Write($"'{Title}': frame window was not where it was put; putting it back");
            _overlay!.Reassert();
        }
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

            // Where the new pose will be: MuMu turns its window for the upright poses, so predict it about the same centre.
            bool turns = IPhoneDuo.IsUpright(to) != IPhoneDuo.IsUpright(from);
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

            // The closed phone's hinge line in its composite: the frame's left padding and hinge barrel, at this scale.
            var closedFrame = IPhoneDuo.IsInner(from) ? frame : Frame!;
            var closedPicture = IPhoneDuo.IsInner(from) ? picture.Width : _pictureOnScreen.Width;
            float hinge = (closedFrame.Screen.X - DuoFrames.Border) * (float)closedPicture / closedFrame.Screen.Width;

            string? dump = _app.Config.DuoAnimationDump is { Length: > 0 } folder ? Path.Combine(folder, $"{DateTime.Now:HHmmss}-{from}-{to}") : null;
            var animation = new FoldAnimation(Main, from, to, new FoldAnimation.End(before, beforeScreen), new FoldAnimation.End(placeholder, afterScreen), hinge, dump);
            animation.Landed += () => _app.Post(() => OnAnimationLanded(animation));
            _animation = animation;
            _animationTarget = to;
            _afterCaptured = false;
            _afterNotBefore = DateTime.MaxValue;
            animation.Start();
            // The animation is the device for now: the frame and the live picture go away under it.
            _overlay?.SetVisible(false);
            _glass?.SetVisible(false);
            _chrome?.SetVisible(false);
            _fade?.SetVisible(false);
            _region = null;
            EmulatorClip.Hide(Main, new Point((rr.Left + rr.Right) / 2 - wr.Left, (rr.Top + rr.Bottom) / 2 - wr.Top));
            PlacePictureSoon();
            Log.Write($"'{Title}': animating {from} -> {to}{(dump != null ? $" (frames to {dump})" : "")}");
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
        _overlay?.SetVisible(false);
        _glass?.SetVisible(false);
        _chrome?.SetVisible(false);
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
        if (_afterCaptured)
        {
            _animation!.Retarget(screen);
            return;
        }
        _pictureOnScreen = new Rectangle(rr.Left + picture.X, rr.Top + picture.Y, picture.Width, picture.Height);
        // Android blanks the screen for a moment as it switches; take the picture once there is one (the 150 ms tick
        // keeps coming back here), or after a second whatever it shows.
        if (Composite(_current, wr, _pictureOnScreen, out bool blank) is { } after && (!blank || DateTime.UtcNow > _afterNotBefore.AddSeconds(1)))
        {
            _afterCaptured = true;
            _animation!.SetAfter(after, screen);
        }
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
                var render = IPhoneDuo.IsUpright(pose) == size.Width < size.Height ? size : new Size(size.Height, size.Width);
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
                _panel = new DuoPosePanel(Main, _app.PoseHotkey, pose => SetDuoPose(pose));
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
        int border = (int)Math.Round(DuoFrames.Border * sx), hinge = Pose == DuoPose.Folded ? (int)Math.Round(DuoFrames.HingeOut * sx) : 0;
        var device = Rectangle.FromLTRB(_pictureOnScreen.Left - border - hinge, _pictureOnScreen.Top - border, _pictureOnScreen.Right + border, _pictureOnScreen.Bottom + border);
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
            // After an Android restart MuMu turns the display back and the device state override is gone; MuMu can turn
            // the display when an app it remembers comes to the front; and two writers (this app and a script, say) can
            // interleave. Put the pose back: a few tries, a few seconds apart, until it holds.
            if (Pose != null && config.DuoPose is { } target && (Pose != target || !IPhoneDuo.InStep(target, Display))
                && _resyncAttempts < 5 && DateTime.UtcNow >= _nextResync)
            {
                _resyncAttempts++;
                _nextResync = DateTime.UtcNow.AddSeconds(3);
                Log.Write($"'{Title}': iPhone Duo out of step with {target} (shows {Pose}, sees {Display.Duo?.Current}, state {Display.Duo?.FoldState}); applying {target} again");
                StartPoseSwitch(target);
                return;
            }
            // A foldable without the postures overlay reports device states but no FoldingFeature: install it (once).
            if (Display.Duo is { Foldable: true, Postures: false } && !_posturesTried)
            {
                _posturesTried = true;
                InstallPostures();
                return;
            }
            // Keep up with poses switched from outside this app (adb, scripts): a cheap wm size every few seconds.
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
            var spec = AndroidCutout.Measure(frame, display2, factor, ios: iosBar);
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

    /// <summary>Fold or unfold (half open closes).</summary>
    public bool ToggleDuoPose() => SetDuoPose(IPhoneDuo.Toggle(Pose));

    /// <summary>Half open like a book, or back to flat open.</summary>
    public bool ToggleDuoHalf() => SetDuoPose(IPhoneDuo.ToggleHalf(Pose));

    /// <summary>Android reports device states (made a foldable), so apps get a FoldingFeature.</summary>
    public bool IsFoldable => Display?.Duo?.Foldable == true;

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
        if (_wantedPose is not { } pose || Instance == null || Display == null || _animation != null) return;
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
    private void StartPoseSwitch(DuoPose pose)
    {
        var (instance, display) = (Instance!, Display!);
        var frame = _app.Library.Find(IPhoneDuo.FrameKey(pose));
        // MuMu turns its window for the upright poses (same scale, swapped round): render for where it will be.
        var turned = IPhoneDuo.IsUpright(pose) == RenderSize.Width < RenderSize.Height ? RenderSize : new Size(RenderSize.Height, RenderSize.Width);
        RenderKey? key = frame != null && IPhoneDuo.PictureInRender(pose, turned) is { } picture
            ? new RenderKey(frame.Key, picture.Width, picture.Height, Math.Clamp(_app.Config.Overlap, 0, 8))
            : null;
        _nextPosePoll = DateTime.UtcNow.AddSeconds(4);
        bool animating = Pose is { } from && from != pose && BeginAnimation(from, pose);
        _fadeNext = !animating;
        RunAndroidTask(() =>
        {
            var rendered = key is { } k ? FrameRenderer.Render(frame!, k) : null;
            var (_, output) = instance.Shell(string.Join("; ", IPhoneDuo.Commands(pose, display.Native, display.Duo?.Foldable == true)));
            var now = MuMuControl.QueryDisplay(instance);
            return () =>
            {
                if (now != null) Display = now;
                var reached = IPhoneDuo.Detect(Display);
                Log.Write($"'{Title}': iPhone Duo -> {pose}: {(reached == pose ? "done" : $"still {reached?.ToString() ?? "no pose"} ({output.Trim()})")}");
                if (reached != pose)
                    _app.Notify($"{Title}: could not {(pose == DuoPose.Folded ? "fold" : "unfold")} the iPhone Duo. {output.Trim()}".TrimEnd(), warning: true);
                SetPose(reached);
                if (reached is { } got && IPhoneDuo.InStep(got, Display)) _resyncAttempts = 0;
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
                if (pose is { } now && now == _app.Config.Instance(Title).DuoPose && IPhoneDuo.InStep(now, display)) _resyncAttempts = 0;
                if (pose == Pose) return;
                SetPose(pose);
                AdoptPose();
                Sync();
            };
        });
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
        var (title, pose, sdk) = (Title, _app.Config.Instance(Title).DuoPose, _app.Config.AndroidSdk);
        RunAndroidTask(() =>
        {
            var findings = DuoDoctor.Run(instance, title, pose, sdk, repair: true);
            var display = MuMuControl.QueryDisplay(instance);
            return () =>
            {
                if (display != null) { Display = display; Pose = IPhoneDuo.Detect(display); }
                _resyncAttempts = 0;
                done(findings);
                Sync();
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
        if (_region == null) return;
        _region = null;
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
