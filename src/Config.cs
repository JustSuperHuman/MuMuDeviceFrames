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

    /// <summary>Per-emulator override of <see cref="AppConfig.IosStatusBar"/>. Null = follow the global setting.</summary>
    public bool? IosStatusBar { get; set; }
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
    public bool IosStatusBarFor(string title) => Instance(title).IosStatusBar ?? IosStatusBar;

    /// <summary>Whether an emulator gets the cutout / status bar overlays (on its own, or because the iOS bar needs them).</summary>
    public bool CutoutFor(string title) => Instance(title).MatchAndroidCutout || IosStatusBarFor(title);

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
