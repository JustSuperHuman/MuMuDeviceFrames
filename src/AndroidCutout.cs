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
    private const string Version = "v3";

    /// <summary>Cutout and corner measurements in the emulator's native pixels.</summary>
    internal sealed record Spec(string? CutoutPath, string? CutoutRect, int StatusBarHeight, int CornerRadius, int SidePadding, Rectangle? Cutout)
    {
        public bool HasCutout => CutoutPath != null;
    }

    public static string Signature(FrameInfo frame, AndroidDisplay display, double paddingFactor, bool hideNotificationIcons) =>
        string.Create(CultureInfo.InvariantCulture, $"{Version}|{frame.Key}|{display.Native.Width}x{display.Native.Height}@{display.Density}|{paddingFactor:0.###}|{(hideNotificationIcons ? "ios" : "android")}");

    /// <summary>
    /// Reads the geometry straight from the frame: whatever the frame paints *inside* the screen outline near the top is
    /// the cutout, and the rows it takes the screen to reach full width give the corner size.
    /// </summary>
    public static Spec? Measure(FrameInfo frame, AndroidDisplay display, double paddingFactor)
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

        return new Spec(path, rect, statusBar, (int)Math.Round(cornerExtent * 0.8), (int)Math.Round(cornerExtent * paddingFactor), cutout);
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Builds both overlays, installs and enables them. Returns null on success, otherwise what went wrong.</summary>
    /// <param name="hideNotificationIcons">For the iOS-style bar: no notification icons next to where its time is drawn.</param>
    public static string? Apply(MuMuInstance instance, Spec spec, string? sdkRoot, bool hideNotificationIcons = false)
    {
        string? aapt2 = AndroidSdk.Newest("build-tools", "aapt2.exe", sdkRoot), zipalign = AndroidSdk.Newest("build-tools", "zipalign.exe", sdkRoot),
                apksigner = AndroidSdk.Newest("build-tools", "apksigner.bat", sdkRoot), androidJar = AndroidSdk.Newest("platforms", "android.jar", sdkRoot);
        if (aapt2 == null || zipalign == null || apksigner == null || androidJar == null)
            return "Android SDK build-tools/platform not found (set AndroidSdk in config.json, or ANDROID_HOME).";

        string key = Path.Combine(AppContext.BaseDirectory, "keys", "platform.pk8"), cert = Path.Combine(AppContext.BaseDirectory, "keys", "platform.x509.pem");
        if (!File.Exists(key) || !File.Exists(cert)) return $"AOSP platform test key missing ({key}).";

        var framework = new StringBuilder();
        if (spec.HasCutout)
        {
            framework.AppendLine($"    <string name=\"config_mainBuiltInDisplayCutout\" translatable=\"false\">{spec.CutoutPath}</string>");
            framework.AppendLine($"    <string name=\"config_mainBuiltInDisplayCutoutRectApproximation\" translatable=\"false\">{spec.CutoutRect}</string>");
            // The frame already paints the island; Android must not paint a second one underneath.
            framework.AppendLine("    <bool name=\"config_fillMainBuiltInDisplayCutout\">false</bool>");
            foreach (var name in new[] { "status_bar_height_default", "status_bar_height_portrait", "quick_qs_offset_height" })
                framework.AppendLine($"    <dimen name=\"{name}\">{spec.StatusBarHeight}px</dimen>");
        }
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

        string work = Path.Combine(Path.GetTempPath(), "MuMuDeviceFrames", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (package, target, label, values) in new[]
                     {
                         (FrameworkOverlay, "android", "Device frame cutout", framework.ToString()),
                         (SystemUiOverlay, "com.android.systemui", "Device frame status bar", systemUi.ToString()),
                     })
            {
                string dir = Path.Combine(work, package);
                Directory.CreateDirectory(Path.Combine(dir, "res", "values"));
                File.WriteAllText(Path.Combine(dir, "AndroidManifest.xml"),
                    $"""
                     <?xml version="1.0" encoding="utf-8"?>
                     <manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{package}" android:versionCode="1" android:versionName="1.0">
                         <overlay android:targetPackage="{target}" android:priority="200" android:isStatic="false" />
                         <application android:hasCode="false" android:label="{label}" />
                     </manifest>
                     """);
                File.WriteAllText(Path.Combine(dir, "res", "values", "values.xml"), $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<resources>\n{values}</resources>\n");

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
            }

            // Installing is not proof: Android silently ignores an overlay whose signature doesn't satisfy the target.
            string list = instance.Shell("cmd overlay list").Output;
            foreach (var package in new[] { FrameworkOverlay, SystemUiOverlay })
                if (!list.Contains($"[x] {package}"))
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
