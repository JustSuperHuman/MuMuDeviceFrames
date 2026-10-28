using System.Reflection;

// Dependency checks use temporary SDK folders, not an emulator or package installation.
var assembly = Assembly.Load("MuMuDeviceFrames");
object? Call(string type, string method, params object?[] args) => assembly.GetType($"MuMuDeviceFrames.{type}")!
    .GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}
foreach (var (text, expected) in new[]
{
    ("openjdk version \"21.0.12.1\"", 21), ("java version \"17.0.1\"", 17),
    ("java version \"1.8.0_401\"", 8), ("java failed to start", 0),
}) Check((int)Call("AndroidTools", "JavaMajor", text)! == expected, $"Java version: {text}");

string temporary = Path.Combine(Path.GetTempPath(), "deviceframes-tests-" + Guid.NewGuid().ToString("N"));
string? home = Environment.GetEnvironmentVariable("ANDROID_HOME"), sdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
try
{
    string empty = Path.Combine(temporary, "empty"), complete = Path.Combine(temporary, "SDK with spaces & symbols");
    Directory.CreateDirectory(empty);
    foreach (string file in new[] { "build-tools/35.0.0/aapt2.exe", "build-tools/35.0.0/zipalign.exe", "build-tools/35.0.0/apksigner.bat", "build-tools/35.0.0/lib/apksigner.jar", "platforms/android-35/android.jar" })
    {
        string path = Path.Combine(complete, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }
    Environment.SetEnvironmentVariable("ANDROID_HOME", empty);
    Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", complete);
    Check((string?)Call("AndroidSdk", "Root", (object?)null) == complete, "Complete SDK wins over stale ANDROID_HOME");
    Check((string?)Call("AndroidSdk", "Root", empty) == empty, "Explicit SDK config stays authoritative");
    string nonexistent = Path.Combine(temporary, "not installed yet");
    Check((string?)Call("AndroidSdk", "Root", nonexistent) == nonexistent, "Explicit missing SDK remains install target");
    var missing = (IReadOnlyList<string>)Call("AndroidTools", "Missing", empty)!;
    Check(missing.Contains("aapt2.exe") && missing.Contains("Android SDK platform"), "Missing tools identified");
    missing = (IReadOnlyList<string>)Call("AndroidTools", "Missing", complete)!;
    Check(!missing.Contains("aapt2.exe") && !missing.Contains("Android SDK platform"), "Complete SDK detected");
    File.Delete(Path.Combine(complete, "build-tools/35.0.0/lib/apksigner.jar"));
    missing = (IReadOnlyList<string>)Call("AndroidTools", "Missing", complete)!;
    Check(missing.Contains("lib/apksigner.jar"), "Incomplete signer installation detected");
}
finally
{
    Environment.SetEnvironmentVariable("ANDROID_HOME", home);
    Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", sdkRoot);
    Directory.Delete(temporary, recursive: true);
}

// iPhone Duo: pose detection, picture placement and frames (pure logic, no emulator).
Type duoPose = assembly.GetType("MuMuDeviceFrames.DuoPose")!;
object Folded = Enum.Parse(duoPose, "Folded"), Open = Enum.Parse(duoPose, "Open");

// MuMu reports a landscape resolution as a portrait panel turned 90 degrees, so its overrides come back swapped.
foreach (var (output, expected, why) in new (string, object?, string)[]
{
    ("Physical size: 2034x2670\nOverride size: 2034x1398", Folded, "MuMu's turned panel, folded"),
    ("Physical size: 2034x2670\nOverride size: 1878x2670", Open, "MuMu's turned panel, open"),
    ("Physical size: 2670x2034\r\nOverride size: 1398x2034", Folded, "landscape-natural panel, folded"),
    ("Physical size: 2034x2670", null, "no override: the whole surface, no pose"),
    ("Physical size: 1206x2622\nOverride size: 1398x2034", null, "a phone that is not set up as a Duo never swaps frames"),
    ("Physical size: 2034x2670\nOverride size: 1080x1920", null, "an unrelated override is not a pose"),
}) Check(Equals(Call("IPhoneDuo", "ParsePose", output), expected), $"Duo pose from wm size: {why}");

// Must match what SurfaceFlinger reported on the emulator: content Rect(636, 0, 2034, 2034) and Rect(0, 78, 2670, 1956).
Check((System.Drawing.Rectangle)Call("IPhoneDuo", "PictureOnSurface", Folded)! == new System.Drawing.Rectangle(636, 0, 1398, 2034), "Folded picture sits 1:1, centred, at x 636");
Check((System.Drawing.Rectangle)Call("IPhoneDuo", "PictureOnSurface", Open)! == new System.Drawing.Rectangle(0, 78, 2670, 1878), "Open picture sits 1:1, centred, at y 78");
Check((System.Drawing.Rectangle?)Call("IPhoneDuo", "PictureInRender", Folded, new System.Drawing.Size(2670, 2034)) == new System.Drawing.Rectangle(636, 0, 1398, 2034),
    "Window at native size: picture is the surface rectangle unchanged");
Check((System.Drawing.Rectangle?)Call("IPhoneDuo", "PictureInRender", Open, new System.Drawing.Size(1017, 775)) == System.Drawing.Rectangle.FromLTRB(0, 30, 1017, 745),
    "Scaled window: open picture rounds to whole window pixels");
Check(Call("IPhoneDuo", "PictureInRender", Folded, new System.Drawing.Size(775, 1017)) == null, "A portrait window is not showing the Duo surface");

// Every window size must give a picture the Duo frame stretches onto exactly; a crop would hide app content under the bezel.
double tolerance = (double)assembly.GetType("MuMuDeviceFrames.FrameLibrary")!.GetField("StretchTolerance")!.GetRawConstantValue()!;
bool allFit = true;
foreach (var pose in new[] { Folded, Open })
{
    var screen = (System.Drawing.Size)Call("IPhoneDuo", "ScreenSize", pose)!;
    for (int w = 400; w <= 2670; w++)
    {
        var size = new System.Drawing.Size(w, (int)Math.Round(w * 2034.0 / 2670));
        var p = (System.Drawing.Rectangle)Call("IPhoneDuo", "PictureInRender", pose, size)!;
        double aspect = (double)Math.Min(p.Width, p.Height) / Math.Max(p.Width, p.Height), want = (double)Math.Min(screen.Width, screen.Height) / Math.Max(screen.Width, screen.Height);
        allFit &= Math.Abs(aspect / want - 1) <= tolerance && p.Left >= 0 && p.Top >= 0 && p.Right <= size.Width && p.Bottom <= size.Height;
    }
}
Check(allFit, "Duo picture fits its frame exactly at every window width from 400 to 2670 px");

// wm size takes natural-orientation sizes; the rotation lock keeps a portrait-only app from turning MuMu's window round.
var turned = (string[])Call("IPhoneDuo", "Commands", Folded, new System.Drawing.Size(2034, 2670))!;
Check(turned.Contains("wm size 2034x1398") && turned.Contains("wm user-rotation lock 1") && turned.Contains("wm fixed-to-user-rotation enabled") && turned.Contains("wm density 480"),
    "Fold on MuMu's turned panel swaps the size and pins rotation 90");
Check(((string[])Call("IPhoneDuo", "Commands", Open, new System.Drawing.Size(2034, 2670))!).Contains("wm size 1878x2670"), "Unfold on MuMu's turned panel swaps the size");
var natural = (string[])Call("IPhoneDuo", "Commands", Folded, new System.Drawing.Size(2670, 2034))!;
Check(natural.Contains("wm size 1398x2034") && natural.Contains("wm user-rotation lock 0"), "Fold on a landscape-natural panel keeps the size and rotation 0");
Check((bool)Call("IPhoneDuo", "LooksLikeSurface", new System.Drawing.Size(1017, 775))! && !(bool)Call("IPhoneDuo", "LooksLikeSurface", new System.Drawing.Size(589, 1281))!,
    "Duo surface recognised by aspect, a phone picture is not");

// The drawn frames: native-resolution screens (screenshots are never resampled), asymmetric closed corners, and a
// bezel that is transparent wherever the screen is (any visible pixel there would swallow taps meant for the app).
string frames = Path.Combine(Path.GetTempPath(), "deviceframes-duo-" + Guid.NewGuid().ToString("N"));
try
{
    var drawn = (System.Collections.IList)Call("DuoFrames", "Ensure", frames)!;
    Check(drawn.Count == 2, "Both Duo frames drawn");
    foreach (var pose in new[] { Folded, Open })
    {
        var info = Call("DuoFrames", "Frame", frames, pose)!;
        var source = Call("FrameSource", "Load", info)!;
        T Get<T>(string name) => (T)source.GetType().GetProperty(name)!.GetValue(source)!;
        var (width, mask, bgra, screen) = (Get<int>("Width"), Get<byte[]>("Mask"), Get<byte[]>("Bgra"), Get<System.Drawing.Rectangle>("Screen"));
        var glass = (byte[]?)source.GetType().GetProperty("Glass")!.GetValue(source);
        Check(screen.Size == (System.Drawing.Size)Call("IPhoneDuo", "ScreenSize", pose)!, $"{pose} frame screen is the display's native size");
        Check(Equals(Call("IPhoneDuo", "PoseOf", info), pose), $"{pose} frame maps back to its pose");
        bool clear = true, glassInside = true;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 255 && bgra[i * 4 + 3] != 0) clear = false;
            if (glass != null && glass[i * 4 + 3] != 0 && mask[i] == 0) glassInside = false;
        }
        Check(clear, $"{pose} bezel is fully transparent over the screen");
        Check(bgra[(screen.Top + screen.Height / 2) * width * 4 + (screen.Left - 12) * 4 + 3] == 255, $"{pose} bezel is opaque beside the screen");
        Check((glass != null) == Equals(pose, Open) && glassInside, $"{pose} glass layer: {(Equals(pose, Open) ? "crease inside the screen only" : "none")}");
        if (Equals(pose, Folded))
        {
            // 20 px in from each top corner: inside the 60 px hinge-side radius, outside the 180 px opening-side one.
            Check(mask[(screen.Top + 20) * width + screen.Left + 20] == 255 && mask[(screen.Top + 20) * width + screen.Right - 21] == 0,
                "Closed screen corners: tight at the hinge, generous on the opening side");
        }
    }
}
finally
{
    Directory.Delete(frames, recursive: true);
}
