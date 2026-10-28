using System.Globalization;
using System.Text;

namespace MuMuDeviceFrames;

/// <summary>
/// Teaches the emulator's Android about the frame it is shown in. Out of the box the emulator is a plain rectangle with
/// a 24dp status bar, so the clock and battery slide under the frame's rounded corners and app headers end up beneath
/// the Dynamic Island / notch / camera hole. A real phone avoids that through framework resources (display cutout,
/// status bar height, corner radius, status bar padding); this installs the same resources as runtime overlays.
///
/// No root involved: MuMu's Android images are signed with the public AOSP platform test key, and an overlay carrying
/// the target's signature may override any of its resources.
/// </summary>
internal static class AndroidCutout
{
    public const string FrameworkOverlay = "com.justsuperhuman.deviceframes.cutout";
    public const string SystemUiOverlay = "com.justsuperhuman.deviceframes.systemui";
    // v5: iOS top safe areas, and no bottom cutout (v4's, taken back off: see Measure).
    private const string Version = "v5";

    /// <summary>Cutout and corner measurements in the emulator's native pixels.</summary>
    /// <param name="Ios">The status bar height is iOS's safe area for that iPhone (written even without an island).</param>
    /// <param name="Home">iOS's home-indicator safe area (px), a waterfall on the panel's bottom edge; 0 for none.</param>
    internal sealed record Spec(string? CutoutPath, string? CutoutRect, int StatusBarHeight, int CornerRadius, int SidePadding, Rectangle? Cutout, bool Ios = false, int Home = 0)
    {
        public bool HasCutout => CutoutPath != null;
    }

    /// <param name="home">The emulator wants iOS's home indicator and its safe area (Face ID iPhones only).</param>
    public static string Signature(FrameInfo frame, AndroidDisplay display, double paddingFactor, bool hideNotificationIcons, bool home = true) =>
        string.Create(CultureInfo.InvariantCulture, $"{Version}|{frame.Key}|{display.Native.Width}x{display.Native.Height}@{display.Density}|{paddingFactor:0.###}|{(hideNotificationIcons ? "ios" : "android")}")
        + (hideNotificationIcons && home && HasHomeIndicator(frame) ? HomeMark : "");

    /// <summary>In a signature: the overlay gives apps iOS's home-indicator safe area (and the indicator is drawn).</summary>
    public const string HomeMark = "|home";

    /// <summary>iPhones with Face ID have the home indicator; Touch ID ones (SE, 8, 6s) do not.</summary>
    public static bool HasHomeIndicator(FrameInfo frame) => DeviceSpecs.IosSafeArea(frame.Category, frame.Model) is { Bottom: > 0 };

    /// <summary>
    /// Reads the geometry straight from the frame: whatever the frame paints *inside* the screen outline near the top is
    /// the cutout, and the rows it takes the screen to reach full width give the corner size.
    /// </summary>
    /// <param name="ios">With the iOS-style chrome on an iPhone frame: the model's iOS safe areas (status bar, home indicator).</param>
    public static Spec? Measure(FrameInfo frame, AndroidDisplay display, double paddingFactor, bool ios = false, bool withHome = true)
    {
        var source = FrameSource.Load(frame);
        var screen = source.Screen;
        if (screen.Width > screen.Height != display.Native.Width > display.Native.Height)
        {
            Log.Write($"Cutout: {frame.Key} and the emulator display have different orientations; skipping");
            return null;
        }
        double sx = (double)display.Native.Width / screen.Width, sy = (double)display.Native.Height / screen.Height;

        var spans = new List<(int Y, int Left, int Right)>(); // cutout extent per row, screen coordinates
        int cornerRows = 0, cornerInset = 0;
        bool fullWidthSeen = false;
        for (int y = 0; y < screen.Height / 3; y++)
        {
            int row = (screen.Y + y) * source.Width + screen.X;
            int first = -1, last = -1;
            for (int x = 0; x < screen.Width; x++)
            {
                if (source.Mask[row + x] < 128) continue;
                if (first < 0) first = x;
                last = x;
            }
            if (first < 0) continue;
            if (y == 0) cornerInset = first;
            if (!fullWidthSeen)
            {
                if (first == 0 && last == screen.Width - 1) fullWidthSeen = true;
                else cornerRows = y + 1;
            }

            int left = -1, right = -1, count = 0;
            for (int x = first; x <= last; x++)
            {
                if (source.Mask[row + x] >= 128) continue;
                if (left < 0) left = x;
                right = x;
                count++;
            }
            if (count >= 3) spans.Add((y, left, right + 1));
        }

        int cornerExtent = (int)Math.Round(Math.Max(cornerRows * sy, cornerInset * sx));
        int minStatusBar = (int)Math.Round(24 * display.Density / 160.0);

        string? path = null, rect = null;
        Rectangle? cutout = null;
        int statusBar = minStatusBar;
        if (spans.Count >= 4)
        {
            int top = spans[0].Y, bottom = spans[^1].Y + 1;
            int minLeft = spans.Min(s => s.Left), maxRight = spans.Max(s => s.Right);
            double cx = screen.Width / 2.0;
            cutout = new Rectangle((int)Math.Round(minLeft * sx), (int)Math.Round(top * sy), (int)Math.Round((maxRight - minLeft) * sx), (int)Math.Round((bottom - top) * sy));

            // Outline as a polygon: down the left side, back up the right. X is relative to the display centre, which is
            // what the framework's cutout spec expects when no @left/@right marker is given.
            int step = Math.Max(1, spans.Count / 48);
            var rows = spans.Where((_, i) => i % step == 0).ToList();
            if (rows[^1] != spans[^1]) rows.Add(spans[^1]);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                double y = i == rows.Count - 1 ? bottom : rows[i].Y;
                sb.Append(i == 0 ? "M " : " L ").Append(Num((rows[i].Left - cx) * sx)).Append(',').Append(Num(y * sy));
            }
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                double y = i == rows.Count - 1 ? bottom : rows[i].Y;
                sb.Append(" L ").Append(Num((rows[i].Right - cx) * sx)).Append(',').Append(Num(y * sy));
            }
            path = sb.Append(" Z").ToString();
            rect = $"M {Num((minLeft - cx) * sx)},{Num(top * sy)} h {Num((maxRight - minLeft) * sx)} v {Num((bottom - top) * sy)} h {Num(-(maxRight - minLeft) * sx)} Z";

            // A floating island/hole gets as much room below as it has above, which centres the clock and icons on it;
            // a notch hanging from the top edge just needs a little air underneath.
            int margin = Math.Max(top, (bottom - top) / 12);
            statusBar = Math.Max(minStatusBar, (int)Math.Round((bottom + margin) * sy));
        }

        // The bottom: iOS's 34 pt home-indicator safe area as a waterfall on the panel's bottom edge, the same kind of
        // cutout the iPhone Duo's safe areas are. Apps that are not edge to edge are kept clear of it (letterboxed), as on
        // iOS for apps built for older SDKs.
        bool iosSafe = false;
        int home = 0;
        if (ios && DeviceSpecs.IosSafeArea(frame.Category, frame.Model) is { } safe)
        {
            iosSafe = true;
            // iOS's own numbers: the status bar safe area of that iPhone, and 34 pt for the home indicator.
            int density = display.DensityOverride ?? display.Density;
            statusBar = (int)Math.Round(safe.Top * density / 160.0);
            if (withHome) home = (int)Math.Round(safe.Bottom * density / 160.0);
        }
        return new Spec(path, rect, statusBar, (int)Math.Round(cornerExtent * 0.8), (int)Math.Round(cornerExtent * paddingFactor), cutout, iosSafe, home);
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Builds both overlays, installs and enables them. Returns null on success, otherwise what went wrong.</summary>
    /// <param name="hideNotificationIcons">For the iOS-style bar: no notification icons next to where its time is drawn.</param>
    public static string? Apply(MuMuInstance instance, Spec spec, string? sdkRoot, bool hideNotificationIcons = false)
    {
        var framework = new StringBuilder();
        if (spec.HasCutout)
        {
            framework.AppendLine($"    <string name=\"config_mainBuiltInDisplayCutout\" translatable=\"false\">{spec.CutoutPath}</string>");
            framework.AppendLine($"    <string name=\"config_mainBuiltInDisplayCutoutRectApproximation\" translatable=\"false\">{spec.CutoutRect}</string>");
            // The frame already paints the island; Android must not paint a second one underneath.
            framework.AppendLine("    <bool name=\"config_fillMainBuiltInDisplayCutout\">false</bool>");
        }
        if (spec.HasCutout || spec.Ios)
            foreach (var name in new[] { "status_bar_height_default", "status_bar_height_portrait", "quick_qs_offset_height" })
                framework.AppendLine($"    <dimen name=\"{name}\">{spec.StatusBarHeight}px</dimen>");
        if (spec.Home > 0) framework.AppendLine($"    <dimen name=\"waterfall_display_bottom_edge_size\">{spec.Home}px</dimen>");
        foreach (var name in new[] { "rounded_corner_radius", "rounded_corner_radius_top", "rounded_corner_radius_bottom" })
            framework.AppendLine($"    <dimen name=\"{name}\">{spec.CornerRadius}px</dimen>");

        var systemUi = new StringBuilder();
        systemUi.AppendLine($"    <dimen name=\"status_bar_padding_start\">{spec.SidePadding}px</dimen>");
        systemUi.AppendLine($"    <dimen name=\"status_bar_padding_end\">{spec.SidePadding}px</dimen>");
        if (hideNotificationIcons)
        {
            systemUi.AppendLine("    <integer name=\"max_notif_static_icons\">0</integer>");
            systemUi.AppendLine("    <dimen name=\"overflow_dot_radius\">0px</dimen>"); // the "more notifications" dot
            // Android 15's clock ignores the icon blocklist; a zero text size is the one switch it still honours.
            systemUi.AppendLine("    <dimen name=\"status_bar_clock_size\">0px</dimen>");
        }

        return InstallOverlay(instance, FrameworkOverlay, "android", "Device frame cutout", framework.ToString(), sdkRoot)
            ?? InstallOverlay(instance, SystemUiOverlay, "com.android.systemui", "Device frame status bar", systemUi.ToString(), sdkRoot);
    }

    /// <summary>What <see cref="ApplyDuo"/> installs, recorded so a restart of this app does not install it again.</summary>
    public static string DuoSignature(DuoSafeAreas areas) => $"duo-v6|{areas}";

    /// <summary>
    /// The iPhone Duo's part. Android's status bar is emptied (no clock, no notification icons) and 0 px tall, so the
    /// tool's iOS chrome is all there is. The safe areas are display cutouts of the "waterfall" kind: every app gets
    /// them as real display-cutout insets, and they are per pose. Android picks the waterfall from resources for the
    /// display's configuration whenever the display turns on (<see cref="IPhoneDuo.RefreshSafeAreas"/>), so the rail
    /// poses (the default values) and the open display upright (values-w600dp-port, 626 dp wide; closed is 466 dp)
    /// each get their own edges, without any app relaunched. Waterfalls are given on the panel's own edges, so each set
    /// is turned back by its poses' display rotation (<see cref="DuoSafeAreas"/>). Apps that are not edge to edge are
    /// kept clear of them, as iOS keeps apps built for older SDKs clear of the Duo's status area.
    /// </summary>
    public static string? ApplyDuo(MuMuInstance instance, string? sdkRoot, DuoSafeAreas areas)
    {
        static string Waterfall((int Left, int Top, int Right, int Bottom) natural) =>
            $"    <dimen name=\"waterfall_display_left_edge_size\">{natural.Left}px</dimen>\n" +
            $"    <dimen name=\"waterfall_display_top_edge_size\">{natural.Top}px</dimen>\n" +
            $"    <dimen name=\"waterfall_display_right_edge_size\">{natural.Right}px</dimen>\n" +
            $"    <dimen name=\"waterfall_display_bottom_edge_size\">{natural.Bottom}px</dimen>\n";
        // Which set a display gets: tent is the only one under 500 dp tall (678 x 466), the open display upright the only
        // portrait one 600 dp wide; closed, open and book are the rest (h500dp).
        var folders = new List<(string?, string)>
        {
            (null, "    <dimen name=\"status_bar_height_default\">0px</dimen>\n" + Waterfall(areas.TentNatural)),
            ("h500dp", Waterfall(areas.RailNatural)),
            ("w600dp-port", $"    <dimen name=\"status_bar_height_default\">{areas.UprightTop}px</dimen>\n" + Waterfall(areas.UprightNatural)),
        };
        if (InstallOverlay(instance, FrameworkOverlay, "android", "Device frame cutout", folders, sdkRoot) is { } error) return error;
        const string values = "    <integer name=\"max_notif_static_icons\">0</integer>\n" +
                              "    <dimen name=\"overflow_dot_radius\">0px</dimen>\n" +
                              "    <dimen name=\"status_bar_clock_size\">0px</dimen>\n";
        return InstallOverlay(instance, SystemUiOverlay, "com.android.systemui", "Device frame status bar", values, sdkRoot);
    }

    /// <summary>
    /// Builds a resource overlay for <paramref name="target"/> from <c>values.xml</c> entries, signs it with the AOSP
    /// platform test key, installs and enables it, and checks Android really activated it. Null on success.
    /// </summary>
    public static string? InstallOverlay(MuMuInstance instance, string package, string target, string label, string values, string? sdkRoot) =>
        InstallOverlay(instance, package, target, label, [(null, values)], sdkRoot);

    /// <param name="folders">Values per resource qualifier (e.g. <c>w600dp-port</c>; null for every configuration).</param>
    public static string? InstallOverlay(MuMuInstance instance, string package, string target, string label, IReadOnlyList<(string? Qualifier, string Values)> folders, string? sdkRoot)
    {
        var missing = AndroidTools.Missing(sdkRoot);
        if (missing.Count > 0) return $"Missing Android display tools: {string.Join(", ", missing)}. Use Install / repair Android display tools in the tray menu.";
        string? aapt2 = AndroidSdk.Newest("build-tools", "aapt2.exe", sdkRoot), zipalign = AndroidSdk.Newest("build-tools", "zipalign.exe", sdkRoot),
                apksigner = AndroidSdk.Newest("build-tools", "apksigner.bat", sdkRoot), androidJar = AndroidSdk.Newest("platforms", "android.jar", sdkRoot);
        if (aapt2 == null || zipalign == null || apksigner == null || androidJar == null)
            return "Android SDK build-tools/platform not found (set AndroidSdk in config.json, or ANDROID_HOME).";

        string key = Path.Combine(AppContext.BaseDirectory, "keys", "platform.pk8"), cert = Path.Combine(AppContext.BaseDirectory, "keys", "platform.x509.pem");
        if (!File.Exists(key) || !File.Exists(cert)) return $"AOSP platform test key missing ({key}).";

        string work = Path.Combine(Path.GetTempPath(), "MuMuDeviceFrames", Guid.NewGuid().ToString("N"));
        try
        {
            string dir = Path.Combine(work, package);
            foreach (var (qualifier, values) in folders)
            {
                string folder = qualifier == null ? "values" : $"values-{qualifier}";
                Directory.CreateDirectory(Path.Combine(dir, "res", folder));
                File.WriteAllText(Path.Combine(dir, "res", folder, "values.xml"), $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<resources>\n{values}</resources>\n");
            }
            File.WriteAllText(Path.Combine(dir, "AndroidManifest.xml"),
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{package}" android:versionCode="1" android:versionName="1.0">
                     <overlay android:targetPackage="{target}" android:priority="200" android:isStatic="false" />
                     <application android:hasCode="false" android:label="{label}" />
                 </manifest>
                 """);

            string compiled = Path.Combine(dir, "compiled.zip"), unsigned = Path.Combine(dir, "unsigned.apk"), aligned = Path.Combine(dir, "aligned.apk"), apk = Path.Combine(dir, "overlay.apk");
            var steps = new (string Name, Func<(int ExitCode, string Output)> Run)[]
            {
                ("aapt2 compile", () => ProcessRunner.Run(aapt2, ["compile", "--dir", Path.Combine(dir, "res"), "-o", compiled])),
                ("aapt2 link", () => ProcessRunner.Run(aapt2, ["link", "-o", unsigned, "-I", androidJar, "--manifest", Path.Combine(dir, "AndroidManifest.xml"), "--min-sdk-version", "30", "--target-sdk-version", "35", compiled])),
                ("zipalign", () => ProcessRunner.Run(zipalign, ["-f", "4", unsigned, aligned])),
                ("apksigner", () => ProcessRunner.RunBatch(apksigner, ["sign", "--key", key, "--cert", cert, "--out", apk, aligned])),
                ("adb install", () => instance.Adb("install", "-r", apk)),
                ("overlay enable", () => instance.Shell($"cmd overlay enable --user 0 {package}")),
            };
            foreach (var (name, run) in steps)
            {
                var (exit, output) = run();
                bool failed = exit != 0 || (name == "adb install" && !output.Contains("Success"));
                if (failed) return $"{name} failed for {package}: {output.Trim()}";
            }

            // Installing is not proof: Android silently ignores an overlay whose signature doesn't satisfy the target.
            if (!instance.Shell("cmd overlay list").Output.Contains($"[x] {package}"))
                return $"Android did not activate {package} (this image is probably not signed with the AOSP platform test key).";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp folder, best effort */ }
        }
    }

    public static void Remove(MuMuInstance instance)
    {
        foreach (var package in new[] { SystemUiOverlay, FrameworkOverlay })
        {
            instance.Shell($"cmd overlay disable --user 0 {package}");
            instance.Adb("uninstall", package);
        }
    }
}
