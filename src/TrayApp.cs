using System.Diagnostics;
using System.Drawing.Drawing2D;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

/// <summary>Tray icon, emulator discovery, menus and the global hotkey.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HotkeyId = 1;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ContextMenuStrip _sessionMenu = new();
    private readonly HiddenWindow _window;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<IntPtr, EmulatorSession> _sessions = [];
    private readonly Dictionary<uint, bool> _pidIsEmulator = [];
    private Icon? _icon;

    public AppConfig Config { get; }
    public FrameLibrary Library { get; private set; }

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Config = AppConfig.Load();
        Library = FrameLibrary.Load(Config.FramesRoot);

        _window = new HiddenWindow(ToggleAll, ExitThread);
        RegisterHotkey();

        _trayMenu.Opening += (_, _) => BuildMenu(_trayMenu, null);
        _icon = LoadIcon() ?? CreateIcon();
        _tray = new NotifyIcon { Icon = _icon, Text = "MuMu Device Frames", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => ToggleAll();

        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (_, _) => Scan();
        _scanTimer.Start();
        Scan();

        if (!File.Exists(AppConfig.FilePath)) Config.Save();
        if (Library.Frames.Count == 0) Post(OfferDownload);
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
        var progress = new Progress<string>(text => _tray.Text = $"MuMu Device Frames — {text}");
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
        BuildMenu(_sessionMenu, session);
        // A menu opened from a no-activate window only closes on outside clicks if one of our windows is foreground.
        SetForegroundWindow(_window.Handle);
        _sessionMenu.Show(Cursor.Position);
    }

    private void BuildMenu(ContextMenuStrip menu, EmulatorSession? focus)
    {
        menu.Items.Clear();

        if (focus != null)
        {
            AddSessionItems(menu.Items, focus);
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add(Check($"Frames enabled  ({Config.Hotkey})", Config.Enabled, ToggleAll));
        menu.Items.Add(Check("Hide emulator title bar (clip to screen)", Config.ClipToScreen, () =>
        {
            Config.ClipToScreen = !Config.ClipToScreen;
            Config.Save();
            SyncAll();
        }));

        if (focus == null)
        {
            menu.Items.Add(new ToolStripSeparator());
            if (_sessions.Count == 0) menu.Items.Add(new ToolStripMenuItem("No running MuMu emulators") { Enabled = false });
            foreach (var session in _sessions.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem($"{session.Title}   —   {Describe(session.Frame)}");
                AddSessionItems(item.DropDownItems, session);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var defaults = new ToolStripMenuItem($"Default frame   —   {Describe(Library.Find(Config.DefaultFrame)) ?? "best match"}");
        defaults.DropDownItems.Add(Check("Automatic (best aspect-ratio match)", Config.DefaultFrame == null, () => SetDefaultFrame(null)));
        defaults.DropDownItems.Add(new ToolStripSeparator());
        AddFrameTree(defaults.DropDownItems, Config.DefaultFrame, null, f => SetDefaultFrame(f.Key));
        menu.Items.Add(defaults);

        menu.Items.Add(new ToolStripMenuItem(Library.Frames.Count == 0 ? "Download device frames (~35 MB)…" : "Update device frames from device-frames-media", null, (_, _) => DownloadFrames()) { Enabled = !_downloading });
        menu.Items.Add(new ToolStripMenuItem("Reload frames and config", null, (_, _) => Reload()));
        menu.Items.Add(new ToolStripMenuItem("Open config folder", null, (_, _) =>
        {
            Config.Save();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.Dir}\"") { UseShellExecute = true });
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit (restores emulator windows)", null, (_, _) => ExitThread()));
    }

    private void AddSessionItems(ToolStripItemCollection items, EmulatorSession session)
    {
        var instance = Config.Instance(session.Title);
        items.Add(new ToolStripMenuItem($"{session.Title}   —   {Describe(session.Frame) ?? "no frame"}") { Enabled = false });

        var frames = new ToolStripMenuItem("Frame");
        frames.DropDownItems.Add(Check("Use default frame", instance.Frame == null, () => SetInstanceFrame(session, null)));
        frames.DropDownItems.Add(new ToolStripSeparator());
        AddFrameTree(frames.DropDownItems, instance.Frame ?? session.Frame?.Key, session.RenderSize, f => SetInstanceFrame(session, f.Key));
        items.Add(frames);

        items.Add(Check("Show frame on this emulator", instance.Enabled, () =>
        {
            instance.Enabled = !instance.Enabled;
            Config.Save();
            session.Sync();
        }));
        items.Add(Check("Peek at MuMu title bar  (double-click bezel)", session.Peek, () =>
        {
            session.Peek = !session.Peek;
            session.Sync();
        }));
        items.Add(new ToolStripMenuItem("Minimize emulator", null, (_, _) => PostMessageW(session.Main, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero)));
        items.Add(new ToolStripMenuItem("Save framed screenshot (PNG)…", null, (_, _) => SaveScreenshot(session)) { Enabled = session.Instance != null && session.Frame != null });

        // Android side of the fit: status bar / cutout, and the display resolution.
        items.Add(new ToolStripSeparator());
        items.Add(Check("Match Android status bar and cutout to frame", instance.MatchAndroidCutout, () =>
        {
            instance.MatchAndroidCutout = !instance.MatchAndroidCutout;
            Config.Save();
            if (instance.MatchAndroidCutout) session.ForceCutout();
            else session.RemoveCutout();
        }));

        if (session is { Display: { } display, Frame: { } frame })
        {
            var target = frame.Screen.Size;
            if (target == display.Native)
            {
                items.Add(new ToolStripMenuItem($"Display {display.Native.Width}×{display.Native.Height} is this frame's exact screen size") { Enabled = false });
            }
            else
            {
                bool cropped = frame.Mismatch(display.Native.Width, display.Native.Height) > FrameLibrary.StretchTolerance;
                string why = cropped ? "currently cropped to fit" : "same shape, optional";
                items.Add(new ToolStripMenuItem($"Set display to {target.Width}×{target.Height} for this frame  ({why}; restarts {session.Title})", null, (_, _) =>
                {
                    int dpi = session.DensityFor(target);
                    var answer = MessageBox.Show(
                        $"Set {session.Title} to {target.Width}×{target.Height} at {dpi} dpi and restart it now?\n\n" +
                        $"Currently {display.Native.Width}×{display.Native.Height} at {display.Density} dpi. The new density keeps your app's layout the same size. " +
                        "Anything unsaved inside the emulator is lost by the restart.",
                        "MuMu Device Frames", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (answer == DialogResult.OK) session.SetDisplayToFrame();
                }));
            }
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
        Task.Run(() =>
        {
            var error = FramedScreenshot.Save(instance, frame, path);
            Post(() => Notify(error == null ? $"Saved {Path.GetFileName(path)}" : $"Screenshot failed: {error}", error != null));
        });
    }

    public void Notify(string text, bool warning = false) =>
        _tray.ShowBalloonTip(6000, "MuMu Device Frames", text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>Category ▸ Model ▸ Variant, with a fit hint per model when the emulator picture size is known.</summary>
    private void AddFrameTree(ToolStripItemCollection items, string? selectedKey, Size? renderSize, Action<FrameInfo> choose)
    {
        foreach (var category in Library.Frames.GroupBy(f => f.Category))
        {
            var categoryItem = new ToolStripMenuItem(category.Key);
            foreach (var model in category.GroupBy(f => f.Model))
            {
                string hint = "";
                if (renderSize is { Width: > 0 } size)
                {
                    double mismatch = model.First().Mismatch(size.Width, size.Height);
                    hint = mismatch < 0.002 ? "   ● exact fit" : mismatch <= FrameLibrary.StretchTolerance ? "   ○ near fit" : "";
                }
                var modelItem = new ToolStripMenuItem(model.Key + hint);
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

    private static ToolStripMenuItem Check(string text, bool isChecked, Action onClick) =>
        new(text, null, (_, _) => onClick()) { Checked = isChecked };

    private static string? Describe(FrameInfo? frame) => frame == null ? null : $"{frame.Model} / {frame.Variant}";

    private void SetInstanceFrame(EmulatorSession session, string? key)
    {
        Config.Instance(session.Title).Frame = key;
        Config.Save();
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
        bool interactiveChanged = Config.BezelInteractive != fresh.BezelInteractive;
        Config.BezelInteractive = fresh.BezelInteractive;
        if (Config.Hotkey != fresh.Hotkey)
        {
            Config.Hotkey = fresh.Hotkey;
            RegisterHotkey();
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
    }

    // ---- hotkey / icon / shutdown -------------------------------------------------------------------------------

    private void RegisterHotkey()
    {
        UnregisterHotKey(_window.Handle, HotkeyId);
        if (string.IsNullOrWhiteSpace(Config.Hotkey)) return;

        uint mods = MOD_NOREPEAT;
        Keys key = Keys.None;
        foreach (var part in Config.Hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
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
        if (key == Keys.None || !RegisterHotKey(_window.Handle, HotkeyId, mods, (uint)key))
            Log.Write($"Could not register hotkey '{Config.Hotkey}'");
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
        _scanTimer.Stop();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    protected override void ExitThreadCore()
    {
        Shutdown();
        UnregisterHotKey(_window.Handle, HotkeyId);
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

    /// <summary>Receives the hotkey and lends foreground status to the bezel context menu.</summary>
    private sealed class HiddenWindow : NativeWindow
    {
        public const string WindowCaption = "MuMuDeviceFramesHost";
        public const int WM_EXIT_REQUEST = 0x8000 + 1; // WM_APP + 1, posted by "MuMuDeviceFrames.exe --exit"

        private readonly Action _onHotkey, _onExit;

        public HiddenWindow(Action onHotkey, Action onExit)
        {
            _onHotkey = onHotkey;
            _onExit = onExit;
            CreateHandle(new CreateParams { Caption = WindowCaption, Style = unchecked((int)WS_POPUP), ExStyle = (int)WS_EX_TOOLWINDOW });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey();
            else if (m.Msg == WM_EXIT_REQUEST) _onExit();
            base.WndProc(ref m);
        }
    }
}
