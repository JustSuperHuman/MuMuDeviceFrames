using System.Diagnostics;
using System.Drawing.Drawing2D;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>Tray icon, emulator discovery, menus and global hotkeys.</summary>
internal sealed partial class TrayApp : ApplicationContext
{
    private const int FramesHotkeyId = 1, ExpoReloadHotkeyId = 2, ExpoGearHotkeyId = 3, DuoHotkeyId = 4, DuoHalfHotkeyId = 5;
    /// <summary>Ctrl+Alt+1 to 5 (by default): one iPhone Duo pose each, in <see cref="IPhoneDuo.Poses"/> order.</summary>
    private const int DuoPoseHotkeyBase = 11;
    private static readonly int[] HotkeyIds = [FramesHotkeyId, ExpoReloadHotkeyId, ExpoGearHotkeyId, DuoHotkeyId, DuoHalfHotkeyId,
        .. Enumerable.Range(DuoPoseHotkeyBase, IPhoneDuo.Poses.Length)];

    /// <summary>The hotkey shown next to a pose, e.g. Ctrl+Alt+3 for the book.</summary>
    public string? PoseHotkey(DuoPose pose) =>
        string.IsNullOrWhiteSpace(Config.DuoPoseHotkeys) ? null : $"{Config.DuoPoseHotkeys}+{Array.IndexOf(IPhoneDuo.Poses, pose) + 1}";

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ContextMenuStrip _sessionMenu = new();
    private readonly HiddenWindow _window;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly System.Windows.Forms.Timer _watchTimer;
    private readonly System.Windows.Forms.Timer _controlsTimer;
    private bool _controlsAltDown;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<IntPtr, EmulatorSession> _sessions = [];
    private readonly Dictionary<uint, bool> _pidIsEmulator = [];
    private readonly HashSet<uint> _expoBusy = [];
    private readonly WinEventProc _foregroundChanged;
    private readonly IntPtr _foregroundHook;
    private readonly DeviceMotionInput _motionInput;
    private IntPtr _lastActiveEmulator;
    private Icon? _icon;

    public AppConfig Config { get; }
    public FrameLibrary Library { get; private set; }

    private readonly Dictionary<string, DateTime> _fitAttempts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DateTime> _duoCreating = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An iPhone Duo being created (the menu or <c>--duo-create</c>): its frame is the Duo's, and the app leaves the
    /// emulator alone until the setup is done (no fitting it to the frame its picture looks like, no overlays during
    /// the setup's restarts). Expires on its own in case the creating process died.
    /// </summary>
    public bool IsBeingCreated(string emulator) => _duoCreating.TryGetValue(emulator, out var until) && DateTime.UtcNow < until;

    private bool MarkDuoCreating(string name, bool creating)
    {
        if (creating)
        {
            _duoCreating[name] = DateTime.UtcNow.AddMinutes(15);
            Config.Instance(name).Frame = IPhoneDuo.FrameKey(DuoPose.Folded);
            Config.Save();
            Log.Write($"'{name}': iPhone Duo being created; left alone until it is set up");
        }
        else if (_duoCreating.Remove(name))
        {
            Log.Write($"'{name}': iPhone Duo creation over");
            foreach (var session in _sessions.Values.ToArray()) session.Sync();
        }
        return true;
    }

    /// <summary>True (and remembered) unless this emulator was fitted to this target in the last few minutes.</summary>
    public bool TryFit(string emulator, string target)
    {
        string key = $"{emulator}|{target}";
        if (_fitAttempts.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(3)) return false;
        _fitAttempts[key] = DateTime.UtcNow;
        return true;
    }

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Config = AppConfig.Load();
        Library = FrameLibrary.Load(Config.FramesRoot);

        _window = new HiddenWindow(OnHotkey, RunExpoAction, RecordFrameByName, SetDuoPoseByName, SetFrameByName, MarkDuoCreating, MenuShot, ExitThread);
        RegisterHotkeys();

        _trayMenu.Opening += (_, _) => BuildMenu(_trayMenu, null);
        _icon = LoadIcon() ?? CreateIcon();
        _tray = new NotifyIcon { Icon = _icon, Text = "MuMu Device Frames", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => ToggleAll();

        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (_, _) => Scan();
        _scanTimer.Start();
        // Between scans: a cheap check that every frame still matches its emulator (see EmulatorSession.Watch).
        _watchTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _watchTimer.Tick += (_, _) => { foreach (var session in _sessions.Values.ToArray()) session.Watch(); };
        _watchTimer.Start();
        Scan();
        _motionInput = new DeviceMotionInput(owner => _sessions.GetValueOrDefault(owner));
        // Alt also reveals controls when the pointer is stationary. Only key transitions touch the panels.
        _controlsTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _controlsTimer.Tick += (_, _) =>
        {
            bool held = DeviceMotionInput.AltDown;
            if (held == _controlsAltDown) return;
            _controlsAltDown = held;
            foreach (var session in _sessions.Values.ToArray()) session.RefreshDeviceControls();
        };
        _controlsTimer.Start();
        _foregroundChanged = (_, _, hwnd, _, _, _, _) => TrackActiveEmulator(hwnd);
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundChanged, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == IntPtr.Zero) Log.Write("Could not monitor the active emulator window");
        // A monitor added, removed, rearranged or rescaled: lay every frame out again (anchors are clamped to the new
        // work areas, frames rendered for the new size).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Post(() =>
        {
            Log.Write("Display settings changed; laying the frames out again");
            SyncAll(invalidate: true);
        });

        if (!File.Exists(AppConfig.FilePath)) Config.Save();
        Startup.Refresh();
        InitializeUpdates();
        if (Library.DownloadedCount == 0) Post(OfferDownload);
    }

    // ---- frame set ----------------------------------------------------------------------------------------------

    private bool _downloading;

    private void OfferDownload()
    {
        var answer = MessageBox.Show(
            $"No device frames were found in\n{Config.FramesRoot}\n\n" +
            "Download the full set (151 iPhone, Pixel, Galaxy, iPad and Apple Watch frames, about 35 MB) from the " +
            "device-frames-media project now?\n\nYou can also do this later from the tray menu.",
            "MuMu Device Frames", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) DownloadFrames();
    }

    private void DownloadFrames()
    {
        if (_downloading) return;
        _downloading = true;
        string root = Config.FramesRoot == AppConfig.PortableFramesRoot ? Config.FramesRoot : AppConfig.DefaultFramesRoot;
        if (Config.FramesRoot != root)
        {
            Config.FramesRoot = root;
            Config.Save();
        }
        var progress = new Progress<string>(text => _tray.Text = $"MuMu Device Frames: {text}");
        Task.Run(async () =>
        {
            try
            {
                var result = await FrameDownloader.DownloadAsync(root, progress, CancellationToken.None);
                Post(() =>
                {
                    Log.Write($"Frame download: {result.Downloaded} new, {result.UpToDate} up to date, {result.Failed} failed of {result.Total}");
                    Notify(result.Failed == 0
                        ? $"{result.Total} device frames ready."
                        : $"{result.Failed} of {result.Total} frames failed to download; try again later.", result.Failed != 0);
                    Reload();
                });
            }
            catch (Exception ex)
            {
                Log.Write($"Frame download failed: {ex.Message}");
                Post(() => Notify($"Frame download failed: {ex.Message}", warning: true));
            }
            finally
            {
                Post(() => { _downloading = false; _tray.Text = "MuMu Device Frames"; });
            }
        });
    }

    public void Post(Action action) => _ui.Post(_ =>
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"UI callback failed: {ex}"); }
    }, null);

    private bool _toolsPrompted, _toolsBusy;

    public void OfferAndroidTools()
    {
        if (_toolsPrompted || _toolsBusy) return;
        _toolsPrompted = true;
        Post(() => SetupAndroidTools(automatic: true));
    }

    private async void SetupAndroidTools(bool automatic = false)
    {
        if (_toolsBusy) return;
        _toolsBusy = true;
        try
        {
            AndroidTools.Refresh();
            var missing = await Task.Run(() => AndroidTools.Missing(Config.AndroidSdk));
            if (missing.Count == 0)
            {
                foreach (var session in _sessions.Values.ToArray()) session.RetryCutout();
                if (!automatic) Notify("Android display tools are ready. Retrying any failed display overlays.");
                return;
            }
            _toolsPrompted = true;
            var answer = MessageBox.Show(
                "Safe areas and the iOS-style status bar need additional Android tools.\n\n" +
                $"Missing: {string.Join(", ", missing)}.\n\n" +
                "Install the missing tools using WinGet? Setup reuses existing tools and downloads Java and Google's Android CLI/SDK as needed. " +
                "A setup window will show progress and package/license prompts. Java may request administrator approval.\n\n" +
                "Choose No to skip for this session. You can retry from Install / repair Android display tools in the tray menu.",
                "Set up Android display tools", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            string root = AndroidSdk.Root(Config.AndroidSdk) ?? AndroidSdk.DefaultRoot;
            int exit = await AndroidTools.InstallAsync(root);
            var remaining = await Task.Run(() => AndroidTools.Missing(root));
            if (exit != 0 || remaining.Count > 0)
            {
                Log.Write($"Android tools setup exited with {exit}; missing: {string.Join(", ", remaining)}");
                MessageBox.Show("Android tools setup did not complete. You can retry from the tray menu.\n\n" +
                    (remaining.Count > 0 ? $"Still missing: {string.Join(", ", remaining)}.\n\n" : "") +
                    $"Details: {Path.Combine(AppConfig.Dir, "android-tools-install.log")}\n\n" +
                    "If WinGet is unavailable, install or update App Installer at https://aka.ms/getwinget, or install the SDK build-tools/platform and Java 17+ manually.",
                    "Android tools setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Config.AndroidSdk = root;
            Config.Save();
            foreach (var session in _sessions.Values.ToArray()) session.RetryCutout();
            Notify("Android display tools installed. Applying safe areas to your emulators.");
        }
        catch (Exception ex)
        {
            Log.Write($"Android tools setup failed: {ex}");
            Notify($"Android tools setup failed: {ex.Message}. Retry from the tray menu.", warning: true);
        }
        finally { _toolsBusy = false; }
    }

    // ---- emulator discovery -------------------------------------------------------------------------------------

    private void Scan()
    {
        foreach (var (hwnd, session) in _sessions.ToArray())
        {
            if (!session.IsGone) continue;
            session.Dispose();
            _sessions.Remove(hwnd);
        }

        EnumWindows((hwnd, _) =>
        {
            if (_sessions.ContainsKey(hwnd) || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsEmulatorProcess(pid)) return true;
            IntPtr render = FindChildByClass(hwnd, EmulatorSession.RenderClass);
            if (render == IntPtr.Zero) return true;
            _sessions[hwnd] = new EmulatorSession(this, hwnd, render, pid, GetWindowText(hwnd));
            return true;
        }, IntPtr.Zero);

        foreach (var session in _sessions.Values)
        {
            session.RefreshTitle();
            session.Sync();
        }

        TrackActiveEmulator(GetForegroundWindow());

        if (_pidIsEmulator.Count > 512) _pidIsEmulator.Clear();
    }

    private bool IsEmulatorProcess(uint pid)
    {
        if (_pidIsEmulator.TryGetValue(pid, out bool known)) return known;
        bool result = false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            result = Config.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Process ended or is inaccessible: not ours.
        }
        return _pidIsEmulator[pid] = result;
    }

    private void SyncAll(bool invalidate = false)
    {
        foreach (var session in _sessions.Values)
        {
            if (invalidate) session.Invalidate();
            session.Sync();
        }
    }

    private void ToggleAll()
    {
        Config.Enabled = !Config.Enabled;
        Config.Save();
        SyncAll();
    }

    // ---- menus --------------------------------------------------------------------------------------------------

    public void ShowSessionMenu(EmulatorSession session)
    {
        RememberEmulator(session);
        BuildMenu(_sessionMenu, session);
        // A menu opened from a no-activate window only closes on outside clicks if one of our windows is foreground.
        SetForegroundWindow(_window.Handle);
        _sessionMenu.Show(Cursor.Position);
    }

    // Segoe Fluent Icons / MDL2 Assets code points.
    private const string PhoneGlyph = "\uE8EA", FrameGlyph = "\uEB9F", CopyGlyph = "\uE8C8", SaveGlyph = "\uE74E",
        RefreshGlyph = "\uE72C", GearGlyph = "\uE713", DownloadGlyph = "\uE896", FolderGlyph = "\uE838",
        PowerGlyph = "\uE7E8", CloseAppsGlyph = "\uE8BB", MinimizeGlyph = "\uE921", DisplayGlyph = "\uE7F4", DevGlyph = "\uEC7A", VideoGlyph = "\uE714";

    private void BuildMenu(ContextMenuStrip menu, EmulatorSession? focus)
    {
        menu.Items.Clear();
        var items = menu.Items;

        if (focus != null)
        {
            // Bezel menu: this emulator's actions first, app-wide settings tucked into the last submenu.
            items.Add(new MenuTitle(focus.Title, Describe(focus.Frame) ?? "No frame", PhoneGlyph));
            items.Add(new MenuSeparator());
            AddSessionItems(items, focus);
            items.Add(new MenuSeparator());
            items.Add(FramesEnabledItem());
            var app = new MenuItem("MuMu Device Frames", GearGlyph);
            AddGlobalItems(app.DropDownItems);
            app.DropDownItems.Add(new MenuSeparator());
            AddAppItems(app.DropDownItems);
            app.DropDownItems.Add(new MenuSeparator());
            app.DropDownItems.Add(ExitItem());
            items.Add(app);
        }
        else
        {
            string running = _sessions.Count switch { 0 => "No emulators running", 1 => "1 emulator running", var n => $"{n} emulators running" };
            items.Add(new MenuTitle("MuMu Device Frames", running, PhoneGlyph));
            items.Add(new MenuSeparator());
            items.Add(FramesEnabledItem());
            items.Add(new MenuSeparator());
            if (_sessions.Count == 0) items.Add(new MenuItem("Start a MuMu emulator to frame it") { Enabled = false });
            foreach (var session in _sessions.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuItem(session.Title, PhoneGlyph) { Hint = session.Frame?.Model ?? "No frame" };
                AddSessionItems(item.DropDownItems, session);
                items.Add(item);
            }
            items.Add(new MenuItem(_creatingDuo ? "Creating an iPhone Duo emulator…" : "Create iPhone Duo emulator…", PhoneGlyph, CreateDuo) { Enabled = !_creatingDuo });
            items.Add(new MenuSeparator());
            AddGlobalItems(items);
            items.Add(new MenuSeparator());
            var settings = new MenuItem("Settings", GearGlyph);
            AddAppItems(settings.DropDownItems);
            items.Add(AppUpdateItem());
            items.Add(settings);
            items.Add(ExitItem());
        }

        MenuStyle.Apply(menu);
    }

    private MenuItem FramesEnabledItem() => Check("Show device frames", Config.Enabled, ToggleAll, hint: Config.Hotkey);

    private MenuItem ExitItem() => new("Exit", PowerGlyph, ExitThread) { Hint = "Restores emulators" };

    /// <summary>Settings that apply to every emulator.</summary>
    private void AddGlobalItems(ToolStripItemCollection items)
    {
        var defaults = new MenuItem("Default frame", FrameGlyph) { Hint = Library.Find(Config.DefaultFrame)?.Model ?? "Automatic" };
        defaults.DropDownItems.Add(Check("Automatic", Config.DefaultFrame == null, () => SetDefaultFrame(null), hint: "Best aspect-ratio match"));
        defaults.DropDownItems.Add(new MenuSeparator());
        AddFrameTree(defaults.DropDownItems, Config.DefaultFrame, null, f => SetDefaultFrame(f.Key));
        items.Add(defaults);
        items.Add(Check("iOS-style status bar", Config.IosStatusBar, () =>
        {
            Config.IosStatusBar = !Config.IosStatusBar;
            foreach (var inst in Config.Instances.Values) inst.IosStatusBar = null; // global switch wins everywhere
            Config.Save();
            SyncAll();
        }));
        items.Add(Check("Hide emulator title bar", Config.ClipToScreen, () =>
        {
            Config.ClipToScreen = !Config.ClipToScreen;
            Config.Save();
            SyncAll();
        }));
        if (ShareXIntegration.FindCompatible(Config.ShareXExecutable) != null)
        {
            var padding = new MenuItem("Recording padding", VideoGlyph) { Hint = $"{Math.Clamp(Config.RecordingPadding, 0, 500)} px" };
            foreach (int amount in new[] { 0, 5, 10, 15, 20, 30, 50, 75, 100 })
                padding.DropDownItems.Add(Check($"{amount} px", Config.RecordingPadding == amount, () => SetRecordingPadding(amount)));
            padding.DropDownItems.Add(new MenuSeparator());
            padding.DropDownItems.Add(new MenuItem("Custom…", null, PromptRecordingPadding));
            items.Add(padding);
        }
    }

    private void CheckDuo(EmulatorSession session)
    {
        Notify($"{session.Title}: checking the iPhone Duo…");
        session.RunDoctor(findings => MessageBox.Show(
            string.Join("\n", findings.Select(f => f.ToString())),
            $"iPhone Duo check: {session.Title}", MessageBoxButtons.OK,
            findings.Any(f => f.Outcome == DuoDoctor.Outcome.Failed) ? MessageBoxIcon.Warning : MessageBoxIcon.Information));
    }

    private bool _creatingDuo;

    /// <summary>A new iPhone Duo emulator from nothing, in the background, with progress as notifications.</summary>
    private void CreateDuo()
    {
        if (_creatingDuo) return;
        var answer = MessageBox.Show(
            "Create a new MuMu emulator set up as an iPhone Duo?\n\n" +
            "It runs Android 15 at 2670×2034 at 480 dpi and is an Android foldable (closed, book, open, open portrait, tabletop and tent), " +
            "with the Duo frame and its pose buttons. It takes a minute or two and restarts the new emulator once.",
            "MuMu Device Frames", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        _creatingDuo = true;
        string? sdk = Config.AndroidSdk;
        Task.Run(() =>
        {
            string? error;
            string created;
            try { error = DuoSetup.Create(null, sdk, text => Post(() => Notify($"iPhone Duo: {text}")), out created, name => Post(() => MarkDuoCreating(name, true))); }
            catch (Exception ex) { (error, created) = (ex.Message, ""); }
            Post(() =>
            {
                _creatingDuo = false;
                if (created.Length > 0) MarkDuoCreating(created, false);
                Notify(error == null ? $"{created} is ready: an iPhone Duo, folded." : $"iPhone Duo not created: {error}", warning: error != null);
            });
        });
    }

    /// <summary>Housekeeping: frame downloads, config and startup.</summary>
    private void AddAppItems(ToolStripItemCollection items)
    {
        items.Add(Library.DownloadedCount == 0
            ? new MenuItem("Download device frames…", DownloadGlyph, DownloadFrames) { Hint = "~35 MB", Enabled = !_downloading }
            : new MenuItem("Update device frames", DownloadGlyph, DownloadFrames) { Hint = $"{Library.DownloadedCount} installed", Enabled = !_downloading });
        items.Add(new MenuItem("Reload frames and config", RefreshGlyph, Reload));
        items.Add(new MenuItem(_toolsBusy ? "Android display tools setup in progress…" : "Install / repair Android display tools…",
            DownloadGlyph, () => SetupAndroidTools())
        { Enabled = !_toolsBusy });
        items.Add(new MenuItem("Open config folder", FolderGlyph, () =>
        {
            Config.Save();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.Dir}\"") { UseShellExecute = true });
        }));
        items.Add(Check("Start with Windows", Startup.IsEnabled, () =>
        {
            try { Startup.Set(!Startup.IsEnabled); }
            catch (Exception ex) { Notify($"Could not change Start with Windows: {ex.Message}", warning: true); }
        }));
        items.Add(new MenuSeparator());
        items.Add(AppUpdateItem());
        items.Add(Check("Check for app updates automatically", Config.AutomaticUpdateChecks, () =>
        {
            Config.AutomaticUpdateChecks = !Config.AutomaticUpdateChecks;
            Config.Save();
            if (Config.AutomaticUpdateChecks) CheckForAppUpdates(manual: false);
        }, hint: "Daily"));
        if (_updateDownload != null && !_installingUpdate)
            items.Add(new MenuItem("Cancel update download", null, () => _updateDownload?.Cancel()));
    }

    private void AddSessionItems(ToolStripItemCollection items, EmulatorSession session)
    {
        var instance = Config.Instance(session.Title);
        bool connected = session.Instance != null;

        // Everyday actions sit at the top, one click away.
        bool canShoot = connected && session.Frame != null;
        items.Add(new MenuTiles(
            new Tile("Copy", CopyGlyph, () => CopyScreenshot(session), canShoot, "Copy framed screenshot"),
            new Tile("Save", SaveGlyph, () => SaveScreenshot(session), canShoot, "Save framed screenshot as PNG"),
            new Tile("Reload", RefreshGlyph, () => RunExpoAction(ExpoAction.Reload, session), connected,
                $"Reload Expo app  {Config.ExpoReloadHotkey}".TrimEnd()),
            new Tile("Dev gear", DevGlyph, () => RunExpoAction(ExpoAction.ToggleGear, session), connected,
                $"Show/hide Expo dev menu gear  {Config.ExpoGearHotkey}".TrimEnd()),
            new Tile("Close apps", CloseAppsGlyph, () => CloseApps(session), connected,
                "Close every app running in this emulator", Destructive: true)));

        items.Add(new MenuItem("Shake device", PhoneGlyph, session.Shake)
        { Enabled = session.CanMoveDevice, Hint = "Tap the side button to build strength" });

        bool duo = session.IsDuoSurface || IPhoneDuo.IsDuo(session.Frame);
        if (duo)
        {
            // iPhone Duo: the six poses as one selector. Each switch is live, no restart. Only a foldable can tell apps
            // it is half open; elsewhere book and tabletop would just be open.
            bool canFold = connected && session.IsDuoSurface;
            items.Add(new MenuPoseBar(IPhoneDuo.Poses.Select(pose =>
            {
                bool half = IPhoneDuo.IsHalf(pose);
                string tip = half && !session.IsFoldable ? "Needs Set up as iPhone Duo" : $"{IPhoneDuo.Describe(pose)}   {PoseHotkey(pose)}".TrimEnd();
                return new PoseChoice(pose, IPhoneDuo.ShortName(pose), canFold && (!half || session.IsFoldable), tip);
            }).ToList(), session.Pose, pose => session.SetDuoPose(pose, "the menu"))
            { Enabled = canFold });
        }

        if (ShareXIntegration.FindCompatible(Config.ShareXExecutable) != null)
        {
            // Stop only while ShareX is recording; the time so far shows on the button.
            var since = _menuShotRecording ?? ShareXIntegration.RecordingSince();
            items.Add(new MenuRecordButton(since, $"{Math.Clamp(Config.RecordingPadding, 0, 500)} px padding", () => RecordFrame(session), StopFrameRecording)
            {
                Enabled = since != null || session.Frame != null && Config.Enabled && instance.Enabled,
            });
        }

        items.Add(new MenuSeparator());
        var frames = new MenuItem("Frame", FrameGlyph) { Hint = session.Frame?.Model ?? "None" };
        frames.DropDownItems.Add(Check("Use default frame", instance.Frame == null, () => SetInstanceFrame(session, null)));
        frames.DropDownItems.Add(new MenuSeparator());
        AddFrameTree(frames.DropDownItems, instance.Frame ?? session.Frame?.Key, session.RenderSize, f => SetInstanceFrame(session, f.Key),
            duoFits: session.IsDuoSurface);
        items.Add(frames);
        items.Add(Check("Show frame", instance.Enabled, () =>
        {
            instance.Enabled = !instance.Enabled;
            Config.Save();
            session.Sync();
        }));
        items.Add(Check("Peek at title bar", session.Peek, () =>
        {
            session.Peek = !session.Peek;
            session.Sync();
        }, hint: "Double-click bezel"));

        // Android side of the fit: status bar / cutout, and the display resolution.
        var android = new MenuItem("Android display", DisplayGlyph);
        AddAndroidItems(android.DropDownItems, session, instance, connected);
        items.Add(android);
        if (duo)
        {
            // The Duo's less frequent settings, out of the way of the pose bar.
            var duoItems = new MenuItem("iPhone Duo", PhoneGlyph);
            duoItems.DropDownItems.Add(new MenuItem("Check iPhone Duo…", null, () => CheckDuo(session)) { Enabled = connected, Hint = "Repairs" });
            duoItems.DropDownItems.Add(Check("Keep apps clear of system areas", instance.DuoProtectSafeAreas, () =>
            {
                instance.DuoProtectSafeAreas = !instance.DuoProtectSafeAreas;
                Config.Save();
                session.RetryCutout();
            }, hint: instance.DuoProtectSafeAreas ? "All apps" : "App-managed insets"));
            duoItems.DropDownItems.Add(Check("Animate folding", Config.DuoAnimations, () =>
            {
                Config.DuoAnimations = !Config.DuoAnimations;
                Config.Save();
            }, hint: FoldAnimation.SystemAllowsAnimation() ? null : "Windows animations are off"));
            duoItems.DropDownItems.Add(Check("Show pose controls", Config.DuoPoseControls, () =>
            {
                Config.DuoPoseControls = !Config.DuoPoseControls;
                Config.Save();
                SyncAll();
            }, hint: "While holding Alt"));
            items.Add(duoItems);
        }
        items.Add(new MenuItem("Minimize", MinimizeGlyph, () => PostMessageW(session.Main, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero)));
    }

    private void AddAndroidItems(ToolStripItemCollection items, EmulatorSession session, InstanceConfig instance, bool connected)
    {
        items.Add(FitItem(session, instance));
        items.Add(new MenuSeparator());
        if (IPhoneDuo.IsDuo(session.Frame))
        {
            AddDuoAndroidItems(items, session, connected);
            return;
        }
        items.Add(Check("Match status bar and cutout to frame", instance.MatchAndroidCutout, () =>
        {
            instance.MatchAndroidCutout = !instance.MatchAndroidCutout;
            Config.Save();
            if (instance.MatchAndroidCutout) session.ForceCutout();
            else session.RemoveCutout();
        }));

        bool iosOn = Config.IosStatusBarFor(session.Title, session.Frame);
        bool iosDefault = Config.IosStatusBar || string.Equals(session.Frame?.Category, IPhoneDuo.Category, StringComparison.OrdinalIgnoreCase);
        var iosBar = Check("iOS-style status bar", iosOn, () =>
        {
            // An explicit choice here overrides the default (on for iPhone frames, else the global switch); choosing the
            // default again clears the override.
            bool next = !iosOn;
            instance.IosStatusBar = next == iosDefault ? null : next;
            Config.Save();
            session.Sync();
        }, hint: instance.IosStatusBar != null ? "This emulator only" : iosDefault && !Config.IosStatusBar ? "On for iPhone frames" : "Following global");
        iosBar.Enabled = connected;
        items.Add(iosBar);
        if (instance.PendingIosSetup != null)
        {
            var finish = new MenuItem("Restart to finish iOS setup", null, session.RestartToFinishIosSetup);
            finish.Enabled = connected;
            items.Add(finish);
        }

        if (session is not { Display: { } display, Frame: { } frame } || instance.FitToFrame) return;
        items.Add(new MenuSeparator());
        var target = frame.Screen.Size;
        if (target == display.Native)
        {
            items.Add(new MenuItem("Resolution matches frame") { Hint = $"{target.Width}×{target.Height}", Enabled = false });
            return;
        }
        bool cropped = frame.Mismatch(display.Native.Width, display.Native.Height) > FrameLibrary.StretchTolerance;
        items.Add(new MenuItem($"Set resolution to {target.Width}×{target.Height}…", null, () =>
        {
            int dpi = session.DensityFor(target);
            var answer = MessageBox.Show(
                $"Set {session.Title} to {target.Width}×{target.Height} at {dpi} dpi and restart it now?\n\n" +
                $"Currently {display.Native.Width}×{display.Native.Height} at {display.Density} dpi. The new density keeps your app's layout the same size. " +
                "Anything unsaved inside the emulator is lost by the restart.",
                "MuMu Device Frames", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.OK) session.SetDisplayToFrame();
        })
        {
            Hint = cropped ? "Cropped now · restarts" : "Optional · restarts",
            HintTone = cropped ? HintTone.Caution : HintTone.Secondary,
        });
    }

    /// <summary>"Fit emulator to frame": the device's resolution and density for whatever frame is chosen.</summary>
    private MenuItem FitItem(EmulatorSession session, InstanceConfig instance)
    {
        string? hint = null;
        if (session.Frame is { } frame)
        {
            var spec = DeviceSpecs.For(frame);
            bool fits = session.Display is { } display && (IPhoneDuo.IsDuo(frame) ? session.IsDuoSurface && session.IsFoldable : DeviceFit.Needed(spec, display) == DeviceFit.Step.None);
            hint = IPhoneDuo.IsDuo(frame) ? (fits ? "iPhone Duo" : "Sets up iPhone Duo")
                : $"{spec.Native.Width}×{spec.Native.Height} · {spec.Density} dpi{(fits ? "" : " · restarts")}";
        }
        return Check("Fit emulator to frame", instance.FitToFrame, () =>
        {
            instance.FitToFrame = !instance.FitToFrame;
            Config.Save();
            session.Sync();
        }, hint: hint);
    }

    /// <summary>
    /// The iPhone Duo has its own chrome and pose-specific safe areas, so there is no ordinary iPhone status bar to
    /// match; what matters is the 2670 × 2034 surface both poses are cut from.
    /// </summary>
    private void AddDuoAndroidItems(ToolStripItemCollection items, EmulatorSession session, bool connected)
    {
        items.Add(new MenuItem("Status and safe areas") { Hint = "Managed by iPhone Duo", Enabled = false });
        if (session.Display is not { } display) return;
        items.Add(new MenuSeparator());
        var surface = IPhoneDuo.Surface;
        if (session.IsDuoSurface && session.IsFoldable)
        {
            items.Add(new MenuItem("Set up as iPhone Duo") { Checked = true, Enabled = false, Hint = "Foldable" });
            return;
        }
        items.Add(new MenuItem("Set up as iPhone Duo…", null, () =>
        {
            var answer = MessageBox.Show(
                $"Make {session.Title} an iPhone Duo now?\n\n" +
                $"It runs at {surface.Width}×{surface.Height} at {IPhoneDuo.Density} dpi (now {display.Native.Width}×{display.Native.Height} at {display.Density} dpi), " +
                "where both displays fit at 1:1, and becomes an Android foldable (closed, half open and open device states), so apps get the fold " +
                "through Jetpack WindowManager. Adding the device states needs root once, so the emulator restarts up to twice. " +
                "Anything unsaved inside the emulator is lost.",
                "MuMu Device Frames", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.OK) session.SetUpAsDuo();
        })
        { Hint = "Restarts", HintTone = HintTone.Caution, Enabled = connected });
    }

    private void SetRecordingPadding(int padding)
    {
        Config.RecordingPadding = Math.Clamp(padding, 0, 500);
        Config.Save();
    }

    private void PromptRecordingPadding()
    {
        using var dialog = new Form
        {
            Text = "Recording padding",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(290, 130)
        };
        dialog.Controls.Add(new Label { Text = "Pixels around the entire frame", Location = new Point(16, 15), AutoSize = true });
        var value = new NumericUpDown { Minimum = 0, Maximum = 500, Value = Math.Clamp(Config.RecordingPadding, 0, 500), Location = new Point(16, 44), Width = 100 };
        dialog.Controls.Add(value);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(103, 89), Width = 80 };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(194, 89), Width = 80 };
        dialog.Controls.Add(cancel);
        dialog.Controls.Add(save);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog() == DialogResult.OK) SetRecordingPadding((int)value.Value);
    }

    /// <summary>The named emulator, or else the active, last used, remembered or only one.</summary>
    private EmulatorSession? FindTarget(string? name)
    {
        EmulatorSession? target = name == null ? SessionForWindow(GetForegroundWindow()) :
            _sessions.Values.FirstOrDefault(session => string.Equals(session.Title, name, StringComparison.OrdinalIgnoreCase));
        if (name == null && target == null && _sessions.TryGetValue(_lastActiveEmulator, out var last)) target = last;
        if (name == null && target == null && Config.LastActiveEmulator is { } saved)
            target = _sessions.Values.FirstOrDefault(session => string.Equals(session.Title, saved, StringComparison.OrdinalIgnoreCase));
        if (name == null && target == null && _sessions.Count == 1) target = _sessions.Values.First();
        return target;
    }

    private bool RecordFrameByName(string? name)
    {
        var target = FindTarget(name);
        if (target == null)
        {
            Log.Write($"Frame recording: emulator '{name ?? "active"}' not found");
            return false;
        }
        RememberEmulator(target);
        return RecordFrame(target);
    }

    /// <summary>--set-frame: a frame (or null for automatic) for a named emulator, as if picked from its menu.</summary>
    private bool SetFrameByName(string name, string? key)
    {
        var target = FindTarget(name);
        if (key != null && Library.Find(key) == null)
        {
            Log.Write($"--set-frame: unknown frame '{key}'");
            return false;
        }
        if (target == null)
        {
            Config.Instance(name).Frame = key; // used when that emulator starts
            Config.Save();
            return true;
        }
        Log.Write($"Frame for '{target.Title}' -> {key ?? "automatic"}");
        SetInstanceFrame(target, key);
        return true;
    }

    /// <summary>Half open like a book, or back to flat (the book hotkey).</summary>
    private bool ToggleDuoHalf()
    {
        var target = FindTarget(null);
        if (target == null)
        {
            Notify("Select a MuMu emulator before folding or unfolding.", warning: true);
            return false;
        }
        RememberEmulator(target);
        Log.Write($"iPhone Duo half-open toggle on '{target.Title}'");
        return target.ToggleDuoHalf($"the {Config.DuoHalfHotkey} hotkey");
    }

    /// <summary>Fold (outer display), unfold (inner display), half open, or with no pose fold / unfold.</summary>
    private bool SetDuoPoseByName(string? name, DuoPose? pose) => SetDuoPoseByName(name, pose, name == null ? "a hotkey" : "the command line");

    private bool SetDuoPoseByName(string? name, DuoPose? pose, string source)
    {
        var target = FindTarget(name);
        if (target == null)
        {
            Log.Write($"iPhone Duo: emulator '{name ?? "active"}' not found");
            Notify(name == null ? "Select a MuMu emulator before folding or unfolding." : $"No running emulator named {name}.", warning: true);
            return false;
        }
        RememberEmulator(target);
        Log.Write($"iPhone Duo {(pose?.ToString() ?? "toggle")} on '{target.Title}'");
        return pose is { } p ? target.SetDuoPose(p, source) : target.ToggleDuoPose(source);
    }

    private bool RecordFrame(EmulatorSession session)
    {
        string? shareX = ShareXIntegration.FindCompatible(Config.ShareXExecutable);
        if (shareX == null)
        {
            Log.Write("Frame recording unavailable: compatible ShareX is not running or installed");
            return false;
        }
        Rectangle? bounds = session.RecordingBounds(Config.RecordingPadding);
        if (bounds == null)
        {
            Log.Write($"Frame recording unavailable: '{session.Title}' has no visible frame");
            Notify("Show the full device frame before recording.", warning: true);
            return false;
        }
        if (!Screen.AllScreens.Any(screen => screen.Bounds.Contains(bounds.Value)))
        {
            Log.Write($"Frame recording unavailable: '{session.Title}' bounds {bounds.Value} do not fit on one display");
            Notify("Move the full frame and its padding onto one display before recording.", warning: true);
            return false;
        }
        try
        {
            ShareXIntegration.Record(shareX, bounds.Value);
            Log.Write($"Recording '{session.Title}' with ShareX at {bounds.Value}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"ShareX recording failed: {ex}");
            Notify($"Could not start recording: {ex.Message}", warning: true);
            return false;
        }
    }

    /// <summary>Set only while <c>--menu-shot</c> draws the menu as it looks during a recording that began then.</summary>
    private DateTime? _menuShotRecording;

    /// <summary>
    /// <c>--menu-shot</c>: an emulator's bezel menu drawn into a PNG, in light or dark and at a scale (1 is 100 %), as
    /// it would show on a monitor at that scale, with the rounded corners and border Windows adds. The same building
    /// and drawing code as the real menu; only the theme and the scale are given instead of read (at another scale the
    /// rows' own WinForms offsets are scaled by the renderer, as WinForms scales them on such a monitor).
    /// </summary>
    private bool MenuShot(string name, string path, bool dark, double scale, int? recordingSeconds)
    {
        var target = FindTarget(name);
        if (target == null) return false;
        MenuStyle.PreviewDark = dark;
        MenuStyle.PreviewScale = scale;
        _menuShotRecording = recordingSeconds is { } seconds ? DateTime.Now - TimeSpan.FromSeconds(seconds) : null;
        try
        {
            using var menu = new ContextMenuStrip();
            _ = menu.Handle;
            BuildMenu(menu, target);
            // Laid out the way it is on screen: shown, but fully transparent, in the corner of the screen.
            menu.Opacity = 0;
            menu.Show(Screen.PrimaryScreen!.WorkingArea.Location);
            var size = menu.Size;
            using var plain = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            menu.DrawToBitmap(plain, new Rectangle(Point.Empty, size));
            var palette = (menu.Renderer as MenuRenderer)!.Palette;
            int radius = (int)Math.Round(8 * scale);
            using var shot = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                using var outline = MenuRenderer.RoundedRect(new Rectangle(0, 0, size.Width, size.Height), radius);
                using (var brush = new TextureBrush(plain)) g.FillPath(brush, outline);
                using var inner = MenuRenderer.RoundedRect(new Rectangle(0, 0, size.Width - 1, size.Height - 1), radius);
                using var pen = new Pen(palette.Border);
                g.DrawPath(pen, inner);
            }
            menu.Close();
            shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Write($"Menu shot of '{target.Title}' ({(dark ? "dark" : "light")}, {scale:0.##}x{(recordingSeconds != null ? ", recording" : "")}) -> {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Menu shot failed: {ex}");
            return false;
        }
        finally
        {
            MenuStyle.PreviewDark = null;
            MenuStyle.PreviewScale = null;
            _menuShotRecording = null;
        }
    }

    private void StopFrameRecording()
    {
        string? shareX = ShareXIntegration.FindCompatible(Config.ShareXExecutable);
        if (shareX == null) return;
        try { ShareXIntegration.Stop(shareX); }
        catch (Exception ex)
        {
            Log.Write($"Could not stop ShareX recording: {ex}");
            Notify($"Could not stop recording: {ex.Message}", warning: true);
        }
    }

    private void SaveScreenshot(EmulatorSession session)
    {
        if (session.Instance is not { } instance || session.Frame is not { } frame) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save framed screenshot",
            Filter = "PNG image|*.png",
            FileName = $"{session.Title} {frame.Model} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        SetForegroundWindow(_window.Handle);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string path = dialog.FileName;
        var bar = session.StatusBarForScreenshot() is { } s ? new FramedScreenshot.StatusBar(s.Layout, s.Dark, s.Time, IosStatusBar.Family(Config.StatusBarFont)) : null;
        var chrome = session.ChromeForScreenshot();
        Task.Run(() =>
        {
            var error = FramedScreenshot.Save(instance, frame, path, null, bar, chrome);
            Post(() => Notify(error == null ? $"Saved {Path.GetFileName(path)}" : $"Screenshot failed: {error}", error != null));
        });
    }

    private void CopyScreenshot(EmulatorSession session)
    {
        if (session.Instance is not { } instance || session.Frame is not { } frame) return;
        string title = session.Title;
        var bar = session.StatusBarForScreenshot() is { } s ? new FramedScreenshot.StatusBar(s.Layout, s.Dark, s.Time, IosStatusBar.Family(Config.StatusBarFont)) : null;
        var chrome = session.ChromeForScreenshot();
        Task.Run(() =>
        {
            string? error;
            byte[]? png = null;
            try { png = FramedScreenshot.Png(instance, frame, bar, out error, chrome); }
            catch (Exception ex) { error = ex.Message; }
            // The clipboard is only reachable from the UI (STA) thread.
            Post(() =>
            {
                if (png != null)
                {
                    try { FramedScreenshot.CopyToClipboard(png); }
                    catch (Exception ex) { error = $"clipboard is busy ({ex.Message})"; }
                }
                Notify(error == null ? $"Copied {title} in {frame.Model}. Paste it anywhere." : $"Screenshot failed: {error}", error != null);
            });
        });
    }

    private void CloseApps(EmulatorSession session)
    {
        if (session.Instance is not { } instance) return;
        string title = session.Title;
        Task.Run(() =>
        {
            string message;
            bool ok = true;
            try
            {
                var closed = MuMuControl.CloseApps(instance);
                Log.Write($"Closed apps on '{title}': {(closed.Count == 0 ? "none running" : string.Join(", ", closed))}");
                message = closed.Count switch
                {
                    0 => "No apps were running.",
                    1 => $"Closed {closed[0]}.",
                    var n => $"Closed {n} apps.",
                };
            }
            catch (Exception ex)
            {
                Log.Write($"Close apps on '{title}' failed: {ex}");
                (message, ok) = ($"Could not close apps: {ex.Message}", false);
            }
            Post(() => Notify($"{title}: {message}", warning: !ok));
        });
    }

    public void Notify(string text, bool warning = false)
    {
        _updateNotification = false;
        _tray.ShowBalloonTip(6000, "MuMu Device Frames", text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>Category ▸ Model ▸ Variant, with a fit hint per model when the emulator picture size is known.</summary>
    private void AddFrameTree(ToolStripItemCollection items, string? selectedKey, Size? renderSize, Action<FrameInfo> choose, bool duoFits = false)
    {
        if (Library.Frames.Count == 0) items.Add(new MenuItem("No frames installed") { Enabled = false });
        foreach (var category in Library.Frames.GroupBy(f => f.Category))
        {
            var categoryItem = new MenuItem(category.Key);
            foreach (var model in category.GroupBy(f => f.Model))
            {
                string? hint = null;
                var tone = HintTone.Secondary;
                if (IPhoneDuo.IsDuo(model.First()))
                {
                    if (duoFits) (hint, tone) = ("Exact fit", HintTone.Good); // both poses are 1:1 on the Duo surface
                }
                else if (renderSize is { Width: > 0 } size)
                {
                    double mismatch = model.First().Mismatch(size.Width, size.Height);
                    if (mismatch < 0.002) (hint, tone) = ("Exact fit", HintTone.Good);
                    else if (mismatch <= FrameLibrary.StretchTolerance) hint = "Near fit";
                }
                var modelItem = new MenuItem(model.Key) { Hint = hint, HintTone = tone };
                foreach (var frame in model)
                {
                    bool selected = string.Equals(frame.Key, selectedKey, StringComparison.OrdinalIgnoreCase);
                    modelItem.DropDownItems.Add(Check(frame.Variant, selected, () => choose(frame)));
                    if (selected) { modelItem.Checked = true; categoryItem.Checked = true; }
                }
                categoryItem.DropDownItems.Add(modelItem);
            }
            items.Add(categoryItem);
        }
    }

    private static MenuItem Check(string text, bool isChecked, Action onClick, string? hint = null) =>
        new(text, null, onClick) { Checked = isChecked, Hint = hint };

    private static string? Describe(FrameInfo? frame) => frame == null ? null : $"{frame.Model} / {frame.Variant}";

    private void SetInstanceFrame(EmulatorSession session, string? key)
    {
        Config.Instance(session.Title).Frame = key;
        Config.Save();
        // Picking "Closed" or "Open" on an emulator set up as a Duo means folding or unfolding it.
        if (session.IsDuoSurface && IPhoneDuo.PoseOf(Library.Find(key)) is { } pose && pose != session.Pose) session.SetDuoPose(pose, "the Frame menu");
        session.Sync();
    }

    private void SetDefaultFrame(string? key)
    {
        Config.DefaultFrame = key;
        Config.Save();
        SyncAll();
    }

    private void Reload()
    {
        var fresh = AppConfig.Load();
        Config.FramesRoot = fresh.FramesRoot; Config.DefaultFrame = fresh.DefaultFrame; Config.Enabled = fresh.Enabled;
        Config.ClipToScreen = fresh.ClipToScreen; Config.Overlap = fresh.Overlap; Config.ProcessNames = fresh.ProcessNames;
        Config.Instances = fresh.Instances;
        Config.AndroidSdk = fresh.AndroidSdk; Config.StatusBarPaddingFactor = fresh.StatusBarPaddingFactor;
        Config.StatusBarClock = fresh.StatusBarClock; Config.StatusBarFont = fresh.StatusBarFont; Config.IosStatusBar = fresh.IosStatusBar;
        Config.LastActiveEmulator = fresh.LastActiveEmulator;
        Config.RecordingPadding = fresh.RecordingPadding;
        Config.AutomaticUpdateChecks = fresh.AutomaticUpdateChecks;
        Config.LastUpdateCheckUtc = fresh.LastUpdateCheckUtc;
        Config.LastNotifiedUpdate = fresh.LastNotifiedUpdate;
        Config.DuoPoseControls = fresh.DuoPoseControls; Config.DuoAnimations = fresh.DuoAnimations; Config.DuoAnimationDump = fresh.DuoAnimationDump;
        Config.ShareXExecutable = fresh.ShareXExecutable;
        bool interactiveChanged = Config.BezelInteractive != fresh.BezelInteractive;
        Config.BezelInteractive = fresh.BezelInteractive;
        if (Config.Hotkey != fresh.Hotkey || Config.ExpoReloadHotkey != fresh.ExpoReloadHotkey || Config.ExpoGearHotkey != fresh.ExpoGearHotkey
            || Config.DuoHotkey != fresh.DuoHotkey || Config.DuoHalfHotkey != fresh.DuoHalfHotkey || Config.DuoPoseHotkeys != fresh.DuoPoseHotkeys)
        {
            Config.Hotkey = fresh.Hotkey;
            Config.ExpoReloadHotkey = fresh.ExpoReloadHotkey;
            Config.ExpoGearHotkey = fresh.ExpoGearHotkey;
            Config.DuoHotkey = fresh.DuoHotkey;
            Config.DuoHalfHotkey = fresh.DuoHalfHotkey;
            Config.DuoPoseHotkeys = fresh.DuoPoseHotkeys;
            RegisterHotkeys();
        }

        Library = FrameLibrary.Load(Config.FramesRoot);
        FrameRenderer.ClearCaches();
        _pidIsEmulator.Clear();
        foreach (var session in _sessions.Values)
        {
            session.Suspend();
            if (interactiveChanged) session.RecreateOverlay();
        }
        SyncAll(invalidate: true);
        AndroidTools.Refresh();
        foreach (var session in _sessions.Values.ToArray()) session.RetryCutout();
    }

    // ---- hotkey / icon / shutdown -------------------------------------------------------------------------------

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case FramesHotkeyId: ToggleAll(); break;
            case ExpoReloadHotkeyId: RunExpoAction(ExpoAction.Reload); break;
            case ExpoGearHotkeyId: RunExpoAction(ExpoAction.ToggleGear); break;
            case DuoHotkeyId: SetDuoPoseByName(null, null, $"the {Config.DuoHotkey} hotkey"); break;
            case DuoHalfHotkeyId: ToggleDuoHalf(); break;
            case >= DuoPoseHotkeyBase when id < DuoPoseHotkeyBase + IPhoneDuo.Poses.Length:
                SetDuoPoseByName(null, IPhoneDuo.Poses[id - DuoPoseHotkeyBase], $"the {PoseHotkey(IPhoneDuo.Poses[id - DuoPoseHotkeyBase])} hotkey");
                break;
        }
    }

    private bool RunExpoAction(ExpoAction action) => RunExpoAction(action, null);

    private bool RunExpoAction(ExpoAction action, EmulatorSession? target)
    {
        string selection = target == null ? "active" : "menu";
        if (target == null)
        {
            target = SessionForWindow(GetForegroundWindow());
            if (target == null && _sessions.TryGetValue(_lastActiveEmulator, out var last))
            {
                target = last;
                selection = "last used";
            }
            if (target == null && Config.LastActiveEmulator is { } saved)
            {
                target = _sessions.Values.FirstOrDefault(session => string.Equals(session.Title, saved, StringComparison.OrdinalIgnoreCase));
                if (target != null) selection = "remembered";
            }
            if (target == null && _sessions.Count == 1)
            {
                target = _sessions.Values.First();
                selection = "only running";
            }
        }
        if (target == null)
        {
            Log.Write($"Expo {action}: no emulator selected");
            Notify("Select a MuMu emulator before using an Expo command.", warning: true);
            return false;
        }
        if (!_expoBusy.Add(target.Pid)) return false;

        RememberEmulator(target);
        uint pid = target.Pid;
        string title = target.Title;
        var knownInstance = target.Instance;
        Log.Write($"Expo {action} selected '{title}' ({selection})");
        Task.Run(() =>
        {
            ExpoResult result;
            try
            {
                var instance = knownInstance ?? MuMuControl.Locate(pid);
                result = instance == null
                    ? new(false, "Could not connect to this emulator's Android guest.")
                    : ExpoControl.Run(instance, action);
            }
            catch (Exception ex)
            {
                Log.Write($"Expo action on '{title}' failed: {ex}");
                result = new(false, $"Expo command failed: {ex.Message}");
            }
            Log.Write($"Expo {action} on '{title}': {result.Message}");
            Post(() =>
            {
                _expoBusy.Remove(pid);
                Notify($"{title}: {result.Message}", warning: !result.Success);
            });
        });
        return true;
    }

    private EmulatorSession? SessionForWindow(IntPtr hwnd)
    {
        if (_sessions.TryGetValue(hwnd, out var session)) return session;
        // The frame is an owned popup; Android's render surface is a child window.
        return _sessions.TryGetValue(GetAncestor(hwnd, 3), out session) ? session : null; // GA_ROOTOWNER
    }

    private void TrackActiveEmulator(IntPtr hwnd)
    {
        if (SessionForWindow(hwnd) is { } session) RememberEmulator(session);
        UpdatePoseHotkeys(hwnd);
    }

    private bool _poseHotkeysOn;

    /// <summary>
    /// The iPhone Duo's hotkeys (the six poses, fold and half open) are only registered while an emulator is the
    /// foreground window. Registered all the time they fired while typing in other apps, and took Ctrl+Alt+digit from
    /// keyboards whose AltGr sends Ctrl+Alt. Unregistered, the keys reach whatever app is in front. Ctrl+Alt+F and the
    /// Expo hotkeys stay global.
    /// </summary>
    private void UpdatePoseHotkeys(IntPtr foreground)
    {
        bool want = SessionForWindow(foreground) != null;
        if (want == _poseHotkeysOn) return;
        _poseHotkeysOn = want;
        foreach (int id in PoseHotkeyIds) UnregisterHotKey(_window.Handle, id);
        if (!want) return;
        RegisterHotkey(DuoHotkeyId, Config.DuoHotkey);
        RegisterHotkey(DuoHalfHotkeyId, Config.DuoHalfHotkey);
        if (!string.IsNullOrWhiteSpace(Config.DuoPoseHotkeys))
            for (int i = 0; i < IPhoneDuo.Poses.Length; i++) RegisterHotkey(DuoPoseHotkeyBase + i, $"{Config.DuoPoseHotkeys}+D{i + 1}");
    }

    private static IEnumerable<int> PoseHotkeyIds => new[] { DuoHotkeyId, DuoHalfHotkeyId }.Concat(Enumerable.Range(DuoPoseHotkeyBase, IPhoneDuo.Poses.Length));

    private void RememberEmulator(EmulatorSession session)
    {
        _lastActiveEmulator = session.Main;
        if (Config.LastActiveEmulator == session.Title) return;
        Config.LastActiveEmulator = session.Title;
        Config.Save();
    }

    private void RegisterHotkeys()
    {
        foreach (int id in HotkeyIds) UnregisterHotKey(_window.Handle, id);
        RegisterHotkey(FramesHotkeyId, Config.Hotkey);
        RegisterHotkey(ExpoReloadHotkeyId, Config.ExpoReloadHotkey);
        RegisterHotkey(ExpoGearHotkeyId, Config.ExpoGearHotkey);
        // The Duo's hotkeys: only while an emulator is in front (UpdatePoseHotkeys).
        _poseHotkeysOn = false;
        UpdatePoseHotkeys(GetForegroundWindow());
    }

    private void RegisterHotkey(int id, string binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return;

        uint mods = MOD_NOREPEAT;
        Keys key = Keys.None;
        foreach (var part in binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default: Enum.TryParse(part, ignoreCase: true, out key); break;
            }
        }
        if (key == Keys.None || !RegisterHotKey(_window.Handle, id, mods, (uint)key))
            Log.Write($"Could not register hotkey '{binding}'");
    }

    /// <summary>The executable's own icon, so tray and taskbar match; falls back to a drawn one.</summary>
    private static Icon? LoadIcon()
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
            return icon == null ? null : (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = RoundedRect(new RectangleF(8, 2, 16, 28), 4.5f);
            using var screen = RoundedRect(new RectangleF(10, 4, 12, 24), 3f);
            g.FillPath(Brushes.White, body);
            using var fill = new SolidBrush(Color.FromArgb(40, 110, 255));
            g.FillPath(fill, screen);
        }
        IntPtr handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Puts every emulator window back the way MuMu made it. Safe to call more than once.</summary>
    public void Shutdown()
    {
        StopUpdates();
        _scanTimer.Stop();
        _watchTimer.Stop();
        _controlsTimer.Stop();
        _motionInput.Dispose();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    protected override void ExitThreadCore()
    {
        Shutdown();
        _controlsTimer.Dispose();
        foreach (int id in HotkeyIds) UnregisterHotKey(_window.Handle, id);
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        _icon = null;
        _window.DestroyHandle();
        base.ExitThreadCore();
    }

    /// <summary>Asks an already running instance to exit cleanly (it restores the emulator windows on the way out).</summary>
    public static bool RequestExitOfRunningInstance()
    {
        IntPtr hwnd = FindWindowW(null, HiddenWindow.WindowCaption);
        return hwnd != IntPtr.Zero && PostMessageW(hwnd, HiddenWindow.WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Posts an Expo action to the tray instance for command-line hotkey tools.</summary>
    public static bool RequestExpoActionOfRunningInstance(ExpoAction action)
    {
        IntPtr hwnd = FindWindowW(null, HiddenWindow.WindowCaption);
        int message = action == ExpoAction.Reload ? HiddenWindow.WM_EXPO_RELOAD : HiddenWindow.WM_EXPO_GEAR;
        return hwnd != IntPtr.Zero &&
            SendMessageTimeoutW(hwnd, message, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out var result) != IntPtr.Zero &&
            result != IntPtr.Zero;
    }

    public static bool RequestFrameRecordingOfRunningInstance(string? name) => SendToRunningInstance(HiddenWindow.RecordFrameData, name);

    /// <summary>Folds or unfolds the iPhone Duo in the named (or active) emulator of the running tray app.</summary>
    public static bool RequestDuoPoseOfRunningInstance(DuoPose pose, string? name) => SendToRunningInstance(HiddenWindow.DuoPoseData + (int)pose, name);

    /// <summary>Draws an emulator's bezel menu into a PNG through the running tray app (<c>--menu-shot</c>).</summary>
    public static bool RequestMenuShotOfRunningInstance(string name, string path, bool dark, double scale, int? recordingSeconds) =>
        SendToRunningInstance(HiddenWindow.MenuShotData, string.Join('\u0001', name, path, dark ? "dark" : "light",
            scale.ToString(System.Globalization.CultureInfo.InvariantCulture), recordingSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""));

    /// <summary>Tells the running tray app an iPhone Duo is being created under this name, or that it is over.</summary>
    public static bool RequestDuoCreatingOfRunningInstance(string name, bool creating) =>
        SendToRunningInstance(HiddenWindow.DuoCreatingData, (creating ? "1" : "0") + name);

    /// <summary>Gives an emulator a frame through the running tray app (<c>--set-frame</c>).</summary>
    public static bool RequestSetFrameOfRunningInstance(string name, string? key) =>
        SendToRunningInstance(HiddenWindow.SetFrameData, name + "" + (key ?? ""));

    /// <summary>True when the tray app is running (so commands go through it and its frames follow).</summary>
    public static bool IsRunning => FindWindowW(null, HiddenWindow.WindowCaption) != IntPtr.Zero;

    private static bool SendToRunningInstance(int dataId, string? name)
    {
        IntPtr hwnd = FindWindowW(null, HiddenWindow.WindowCaption);
        if (hwnd == IntPtr.Zero || name is { Length: > 500 }) return false;
        IntPtr data = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(name ?? "");
        IntPtr message = IntPtr.Zero;
        try
        {
            var payload = new COPYDATASTRUCT { DataId = dataId, ByteCount = ((name?.Length ?? 0) + 1) * sizeof(char), Data = data };
            message = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<COPYDATASTRUCT>());
            System.Runtime.InteropServices.Marshal.StructureToPtr(payload, message, false);
            return SendMessageTimeoutW(hwnd, WM_COPYDATA, IntPtr.Zero, message, SMTO_ABORTIFHUNG, 5000, out var result) != IntPtr.Zero && result != IntPtr.Zero;
        }
        finally
        {
            if (message != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(message);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Receives the hotkey and lends foreground status to the bezel context menu.</summary>
    private sealed class HiddenWindow : NativeWindow
    {
        public const string WindowCaption = "MuMuDeviceFramesHost";
        public const int WM_EXIT_REQUEST = 0x8000 + 1; // WM_APP + 1, posted by "MuMuDeviceFrames.exe --exit"
        public const int WM_EXPO_RELOAD = 0x8000 + 2, WM_EXPO_GEAR = 0x8000 + 3;
        /// <summary>WM_COPYDATA kinds; the payload is an emulator name (empty for the active one).</summary>
        public const int RecordFrameData = 1, SetFrameData = 2, DuoCreatingData = 3, MenuShotData = 4, DuoPoseData = 10; // DuoPoseData + (int)pose

        private readonly Action<int> _onHotkey;
        private readonly Func<ExpoAction, bool> _onExpoAction;
        private readonly Func<string?, bool> _onRecordFrame;
        private readonly Func<string?, DuoPose?, bool> _onDuoPose;
        private readonly Func<string, string?, bool> _onSetFrame;
        private readonly Func<string, bool, bool> _onDuoCreating;
        private readonly Func<string, string, bool, double, int?, bool> _onMenuShot;
        private readonly Action _onExit;

        public HiddenWindow(Action<int> onHotkey, Func<ExpoAction, bool> onExpoAction, Func<string?, bool> onRecordFrame,
            Func<string?, DuoPose?, bool> onDuoPose, Func<string, string?, bool> onSetFrame, Func<string, bool, bool> onDuoCreating,
            Func<string, string, bool, double, int?, bool> onMenuShot, Action onExit)
        {
            _onSetFrame = onSetFrame;
            _onDuoCreating = onDuoCreating;
            _onMenuShot = onMenuShot;
            _onHotkey = onHotkey;
            _onExpoAction = onExpoAction;
            _onRecordFrame = onRecordFrame;
            _onDuoPose = onDuoPose;
            _onExit = onExit;
            CreateHandle(new CreateParams { Caption = WindowCaption, Style = unchecked((int)WS_POPUP), ExStyle = (int)WS_EX_TOOLWINDOW });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) { _onHotkey(m.WParam.ToInt32()); return; }
            if (m.Msg == WM_EXIT_REQUEST) { _onExit(); return; }
            if (m.Msg == WM_EXPO_RELOAD) { m.Result = _onExpoAction(ExpoAction.Reload) ? (IntPtr)1 : IntPtr.Zero; return; }
            if (m.Msg == WM_EXPO_GEAR) { m.Result = _onExpoAction(ExpoAction.ToggleGear) ? (IntPtr)1 : IntPtr.Zero; return; }
            if (m.Msg == WM_COPYDATA && m.LParam != IntPtr.Zero)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<COPYDATASTRUCT>(m.LParam);
                bool pose = data.DataId >= DuoPoseData && data.DataId < DuoPoseData + IPhoneDuo.Poses.Length;
                if ((data.DataId == RecordFrameData || data.DataId == SetFrameData || data.DataId == DuoCreatingData || data.DataId == MenuShotData || pose)
                    && data.Data != IntPtr.Zero && data.ByteCount is >= 2 and <= 1002 && data.ByteCount % 2 == 0)
                {
                    string? name = System.Runtime.InteropServices.Marshal.PtrToStringUni(data.Data, data.ByteCount / 2 - 1);
                    if (data.DataId == MenuShotData)
                    {
                        var p = name.Split('\u0001');
                        m.Result = p.Length == 5 && double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale)
                            && scale is >= 0.5 and <= 4
                            && _onMenuShot(p[0], p[1], p[2] == "dark", scale, int.TryParse(p[4], out int seconds) ? seconds : null) ? (IntPtr)1 : IntPtr.Zero;
                        return;
                    }
                    if (data.DataId == DuoCreatingData)
                    {
                        m.Result = name.Length > 1 && _onDuoCreating(name[1..], name[0] == '1') ? (IntPtr)1 : IntPtr.Zero;
                        return;
                    }
                    if (data.DataId == SetFrameData)
                    {
                        var parts = name.Split('', 2);
                        m.Result = parts.Length == 2 && _onSetFrame(parts[0], parts[1].Length == 0 ? null : parts[1]) ? (IntPtr)1 : IntPtr.Zero;
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = null;
                    bool ok = data.DataId == RecordFrameData ? _onRecordFrame(name)
                        : _onDuoPose(name, (DuoPose)(int)(data.DataId - DuoPoseData));
                    m.Result = ok ? (IntPtr)1 : IntPtr.Zero;
                }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
