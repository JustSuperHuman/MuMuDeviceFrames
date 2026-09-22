using System.Text.Json;

namespace MuMuDeviceFrames;

internal sealed record FrameInfo(string Category, string Model, string Variant, string Dir, Rectangle Screen, Size FrameSize)
{
    public string Key => $"{Category}/{Model}/{Variant}";
    public string FramePath => Path.Combine(Dir, "frame.png");
    public string MaskPath => Path.Combine(Dir, "mask.png");
    public bool IsLandscape => Screen.Width > Screen.Height;

    /// <summary>Short side / long side, so portrait and landscape compare equal.</summary>
    public double Aspect => (double)Math.Min(Screen.Width, Screen.Height) / Math.Max(Screen.Width, Screen.Height);

    /// <summary>Relative aspect-ratio error against an emulator picture of the given size (0 = exact fit).</summary>
    public double Mismatch(int width, int height)
    {
        double target = (double)Math.Min(width, height) / Math.Max(width, height);
        return Math.Abs(target / Aspect - 1.0);
    }
}

internal sealed class FrameLibrary
{
    /// <summary>Below this the frame is stretched (imperceptibly) to fit exactly; above it the screen is cropped instead.</summary>
    public const double StretchTolerance = 0.015;

    public IReadOnlyList<FrameInfo> Frames { get; }
    public string Root { get; }

    private readonly Dictionary<string, FrameInfo> _byKey;

    private FrameLibrary(string root, List<FrameInfo> frames)
    {
        Root = root;
        Frames = frames;
        _byKey = frames.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static FrameLibrary Load(string root)
    {
        var frames = new List<FrameInfo>();
        if (Directory.Exists(root))
        {
            foreach (var templatePath in Directory.EnumerateFiles(root, "template.json", SearchOption.AllDirectories))
            {
                try
                {
                    var dir = Path.GetDirectoryName(templatePath)!;
                    var parts = Path.GetRelativePath(root, dir).Split(Path.DirectorySeparatorChar);
                    if (parts.Length != 3) continue;
                    if (!File.Exists(Path.Combine(dir, "frame.png")) || !File.Exists(Path.Combine(dir, "mask.png"))) continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(templatePath));
                    var screen = doc.RootElement.GetProperty("screen");
                    var size = doc.RootElement.GetProperty("frameSize");
                    frames.Add(new FrameInfo(parts[0], parts[1], parts[2], dir,
                        new Rectangle(screen.GetProperty("x").GetInt32(), screen.GetProperty("y").GetInt32(),
                            screen.GetProperty("width").GetInt32(), screen.GetProperty("height").GetInt32()),
                        new Size(size.GetProperty("width").GetInt32(), size.GetProperty("height").GetInt32())));
                }
                catch (Exception ex)
                {
                    Log.Write($"Skipping {templatePath}: {ex.Message}");
                }
            }
        }

        frames.Sort((a, b) =>
        {
            int c = Native.StrCmpLogicalW(a.Category, b.Category);
            if (c == 0) c = Native.StrCmpLogicalW(a.Model, b.Model);
            if (c == 0) c = Native.StrCmpLogicalW(a.Variant, b.Variant);
            return c;
        });
        Log.Write($"Frame library: {frames.Count} frames from {root}");
        return new FrameLibrary(root, frames);
    }

    public FrameInfo? Find(string? key) => key != null && _byKey.TryGetValue(key.Replace('\\', '/'), out var f) ? f : null;

    /// <summary>The frame whose screen aspect is closest to the emulator picture; newest model wins ties.</summary>
    public FrameInfo? BestMatch(int width, int height)
    {
        FrameInfo? best = null;
        double bestScore = double.MaxValue;
        foreach (var f in Frames)
        {
            double score = Math.Round(f.Mismatch(width, height), 4);
            if (f.Variant.Contains("Shadow", StringComparison.OrdinalIgnoreCase)) score += 1e-5;
            // On ties a different model replaces the current pick: the list is naturally sorted, so later means newer.
            if (best == null || score < bestScore || (score == bestScore && f.Model != best.Model))
            {
                best = f;
                bestScore = score;
            }
        }
        return best;
    }
}
