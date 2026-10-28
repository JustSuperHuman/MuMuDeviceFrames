using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuMuDeviceFrames;

internal sealed class InstanceConfig
{
    /// <summary>Frame key ("Category/Model/Variant"). Null = use the default frame.</summary>
    public string? Frame { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Keep the emulator's Android status bar / display cutout in line with the frame (installs two resource overlays).</summary>
    public bool MatchAndroidCutout { get; set; }

    /// <summary>What the installed overlays were built for; they are rebuilt when frame or display change.</summary>
    public string? InstalledCutout { get; set; }

    /// <summary>
    /// An iOS safe-area overlay waiting for this emulator's next start: changing the overlay of an emulator that is
    /// running relaunches its apps, so it goes in when Android starts (or at "Restart to finish iOS setup").
    /// </summary>
    public string? PendingIosSetup { get; set; }

    /// <summary>iOS's home indicator and its 34 pt safe area on a Face ID iPhone frame; false keeps Android's bottom
    /// as it is (the opt-out). Null follows the default, on.</summary>
    public bool? IosHomeIndicator { get; set; }

    /// <summary>The iPhone Duo's overlays installed on this emulator (<see cref="AndroidCutout.DuoSignature"/>).</summary>
    public string? DuoOverlay { get; set; }

    /// <summary>Per-emulator override of <see cref="AppConfig.IosStatusBar"/>. Null = follow the global setting.</summary>
    public bool? IosStatusBar { get; set; }

    /// <summary>
    /// Make the emulator the chosen frame's device: its native resolution and real density (restarts the emulator when
    /// the resolution changes). On by default.
    /// </summary>
    public bool FitToFrame { get; set; } = true;

    /// <summary>iPhone Duo pose this emulator is kept in (healed after Android restarts). Null until it has one.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DuoPose? DuoPose { get; set; }

    /// <summary>Where the device lives on screen (its frame's centre, screen pixels): poses, turns and restarts keep it
    /// there; only dragging the device moves it (see <see cref="DeviceAnchor"/>).</summary>
    public AnchorPoint? Anchor { get; set; }
}

internal sealed class AppConfig
{
    /// <summary>
    /// The device-frames-output folder ({category}/{model}/{variant}/frame.png|mask.png|template.json). Defaults to
    /// the download location; a device-frames-output folder next to the executable is used when the default is empty.
    /// </summary>
    public string FramesRoot { get; set; } = DefaultFramesRoot;

    [JsonIgnore] public static string DefaultFramesRoot => Path.Combine(Dir, "frames");
    [JsonIgnore] public static string PortableFramesRoot => Path.Combine(AppContext.BaseDirectory, "device-frames-output");
    /// <summary>Frames this app draws itself (iPhone Duo), kept apart from the downloaded set.</summary>
    [JsonIgnore] public static string BuiltInFramesRoot => Path.Combine(Dir, "builtin-frames");

    /// <summary>Frame used by emulators without their own choice. Null = best aspect-ratio match.</summary>
    public string? DefaultFrame { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Clip the emulator window to the rounded screen shape so its title bar and borders vanish under the frame.</summary>
    public bool ClipToScreen { get; set; } = true;

    /// <summary>Pixels by which the bezel overlaps the emulator picture, so the anti-aliased inner edge always blends with app content.</summary>
    public int Overlap { get; set; } = 1;

    /// <summary>Bezel can be dragged / right-clicked. When false the overlay is fully click-through.</summary>
    public bool BezelInteractive { get; set; } = true;

    /// <summary>Toggles all frames (gives the MuMu title bar and toolbar back while off).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F";

    /// <summary>Reloads the Expo app in the last active emulator. Empty disables the hotkey.</summary>
    public string ExpoReloadHotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>Shows or hides Expo's floating tools button in the last active emulator. Empty disables the hotkey.</summary>
    public string ExpoGearHotkey { get; set; } = "Ctrl+Alt+G";

    /// <summary>Folds or unfolds the iPhone Duo in the active or last active emulator. Empty disables the hotkey.</summary>
    public string DuoHotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>Half opens the iPhone Duo like a book, or opens it flat again. Empty disables the hotkey.</summary>
    public string DuoHalfHotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>Modifiers for the five pose hotkeys, followed by 1 to 5 (closed, open, book, open portrait, tabletop).</summary>
    public string DuoPoseHotkeys { get; set; } = "Ctrl+Alt";

    /// <summary>The pose control beside an iPhone Duo frame.</summary>
    public bool DuoPoseControls { get; set; } = true;

    /// <summary>Animate folding and unfolding (also off when Windows' "Animation effects" are off).</summary>
    public bool DuoAnimations { get; set; } = true;

    /// <summary>A folder to write every animation frame to as PNG (at exactly 60 fps), for making videos. Null = off.</summary>
    public string? DuoAnimationDump { get; set; }

    /// <summary>Last emulator brought to the front, used when an Expo hotkey runs from another app.</summary>
    public string? LastActiveEmulator { get; set; }

    /// <summary>Extra desktop pixels around the entire rendered device frame in ShareX recordings.</summary>
    public int RecordingPadding { get; set; } = 15;

    /// <summary>Optional path to a compatible ShareX.exe when it is installed outside the usual locations.</summary>
    public string? ShareXExecutable { get; set; }

    /// <summary>Android SDK folder (aapt2, zipalign, apksigner, android.jar) for building the cutout overlays. Null = ANDROID_HOME.</summary>
    public string? AndroidSdk { get; set; }

    /// <summary>Status bar side padding as a fraction of the frame's corner size. Larger pushes clock and icons further in.</summary>
    public double StatusBarPaddingFactor { get; set; } = 0.5;

    /// <summary>
    /// iOS-style status bar on every emulator: Android's own clock and icons are hidden and an iPhone time / cellular /
    /// Wi-Fi / battery is drawn instead. Implies matching the Android status bar and cutout to the frame.
    /// </summary>
    public bool IosStatusBar { get; set; }

    /// <summary>Whether an emulator shows the iOS-style bar: its own override, else the global setting.</summary>
    /// <remarks>An iPhone frame (the iPhone Duo too) brings the iOS chrome by default, as fit-to-frame brings the size;
    /// the emulator's own choice still wins. Android frames keep Android's status bar unless switched on.</remarks>
    public bool IosStatusBarFor(string title, FrameInfo? frame = null) =>
        Instance(title).IosStatusBar ?? (IosStatusBar || string.Equals(frame?.Category, IPhoneDuo.Category, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an emulator gets the cutout / status bar overlays (on its own, or because the iOS bar needs them).</summary>
    public bool CutoutFor(string title, FrameInfo? frame = null) =>
        Instance(title).MatchAndroidCutout || (IosStatusBarFor(title, frame) && !IPhoneDuo.IsDuo(frame));

    /// <summary>Time shown by the iOS-style status bar: "auto" (this PC's time, 12-hour), "24h", or fixed text such as "9:41".</summary>
    public string StatusBarClock { get; set; } = "auto";

    /// <summary>Font family for the iOS-style time. Null = SF Pro if installed, otherwise Segoe UI Semibold.</summary>
    public string? StatusBarFont { get; set; }

    /// <summary>Executable names (without .exe) whose windows are checked for a MuMu render surface.</summary>
    public List<string> ProcessNames { get; set; } = ["MuMuNxDevice", "MuMuPlayer", "NemuPlayer"];

    /// <summary>Per-emulator settings, keyed by the emulator window title (e.g. "JG-1").</summary>
    public Dictionary<string, InstanceConfig> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuMuDeviceFrames");
    [JsonIgnore] public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                if (cfg != null)
                {
                    cfg.Instances = new Dictionary<string, InstanceConfig>(cfg.Instances, StringComparer.OrdinalIgnoreCase);
                    return cfg.WithPortableFallback();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Config load failed, using defaults: {ex.Message}");
        }
        return new AppConfig().WithPortableFallback();
    }

    private AppConfig WithPortableFallback()
    {
        if (FramesRoot == DefaultFramesRoot && !Directory.Exists(FramesRoot) && Directory.Exists(PortableFramesRoot))
            FramesRoot = PortableFramesRoot;
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Write($"Config save failed: {ex.Message}");
        }
    }

    public InstanceConfig Instance(string title)
    {
        if (!Instances.TryGetValue(title, out var inst))
            Instances[title] = inst = new InstanceConfig();
        return inst;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppConfig.Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
