using System.Reflection;

// Dependency checks use temporary SDK folders, not an emulator or package installation.
var assembly = Assembly.Load("MuMuDeviceFrames");
if (args is ["--hold-operation", var serial, var ready, var release])
{
    using var operation = (IDisposable)Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.AndroidOperation")!, serial)!;
    File.WriteAllText(ready, "locked");
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(release) && DateTime.UtcNow < deadline) Thread.Sleep(20);
    return;
}
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
object Folded = Enum.Parse(duoPose, "Folded"), Open = Enum.Parse(duoPose, "Open"), HalfOpen = Enum.Parse(duoPose, "HalfOpen"),
    OpenPortrait = Enum.Parse(duoPose, "OpenPortrait"), Tabletop = Enum.Parse(duoPose, "Tabletop");
object? CallTyped(string type, string method, Type[] types, params object?[] args) =>
    assembly.GetType($"MuMuDeviceFrames.{type}")!.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, types)!.Invoke(null, args);
var mumuPanel = new System.Drawing.Size(2034, 2670); // MuMu's 2670 x 2034 surface, reported as a turned portrait panel

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
var turned = (string[])Call("IPhoneDuo", "Commands", Folded, new System.Drawing.Size(2034, 2670), false)!;
Check(turned.Contains("wm size 2034x1398") && turned.Contains("wm user-rotation lock 1") && turned.Contains("wm fixed-to-user-rotation enabled") && turned.Contains("wm density 480"),
    "Fold on MuMu's turned panel swaps the size and pins rotation 90");
// The safe areas are waterfalls on the panel's own edges, turned by each pose group's rotation. Measured with the probe
// on MuMu's turned panel: at 90 the panel's top showed on the left (so its bottom on the right), at 270 on the right.
// Neither rail edge may be the panel's top: upright (rotation 0) a stale rail set would size the status bar.
var areas = Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.DuoSafeAreas")!, 252, 204, 102, true)!;
var railNatural = (System.Runtime.CompilerServices.ITuple)areas.GetType().GetProperty("RailNatural")!.GetValue(areas)!;
var uprightNatural = (System.Runtime.CompilerServices.ITuple)areas.GetType().GetProperty("UprightNatural")!.GetValue(areas)!;
Check((int)railNatural[3]! == 252 && (int)railNatural[0]! == 102 && (int)railNatural[1]! == 0 && (int)railNatural[2]! == 0,
    "Rail poses (rotation 90): right 84 pt from the panel's bottom edge, bottom 34 pt from its left edge, nothing on its top");
Check((int)uprightNatural[3]! == 102 && (int)uprightNatural[0]! == 0 && (int)uprightNatural[1]! == 204 && (int)uprightNatural[2]! == 0,
    "Upright poses (rotation 0): the top status element and home indicator are both reserved cutouts");
Check(((string[])Call("IPhoneDuo", "Commands", OpenPortrait, new System.Drawing.Size(2034, 2670), false)!).Contains("wm user-rotation lock 0"),
    "Upright poses keep rotation 0");
Check(((string[])Call("IPhoneDuo", "Commands", Open, new System.Drawing.Size(2034, 2670), false)!).Contains("wm size 1878x2670"), "Unfold on MuMu's turned panel swaps the size");
var natural = (string[])Call("IPhoneDuo", "Commands", Folded, new System.Drawing.Size(2670, 2034), false)!;
Check(natural.Contains("wm size 1398x2034") && natural.Contains("wm user-rotation lock 0"), "Fold on a landscape-natural panel keeps the size and rotation 0");
Check((bool)Call("IPhoneDuo", "LooksLikeSurface", new System.Drawing.Size(1017, 775))! && !(bool)Call("IPhoneDuo", "LooksLikeSurface", new System.Drawing.Size(589, 1281))!,
    "Duo surface recognised by aspect, a phone picture is not");

// The drawn frames: native-resolution screens (screenshots are never resampled), asymmetric closed corners, and a
// bezel that is transparent wherever the screen is (any visible pixel there would swallow taps meant for the app).
string frames = Path.Combine(Path.GetTempPath(), "deviceframes-duo-" + Guid.NewGuid().ToString("N"));
try
{
    var drawn = (System.Collections.IList)Call("DuoFrames", "Ensure", frames)!;
    Check(drawn.Count == 6, "All six Duo frames drawn");
    var creaseStrength = new Dictionary<object, int>();
    var tentPose = Enum.Parse(duoPose, "Tent");
    foreach (var pose in new[] { Folded, Open, HalfOpen, OpenPortrait, Tabletop, tentPose })
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
        bool isInner = !Equals(pose, Folded) && !Equals(pose, tentPose);
        Check((glass != null) == isInner && glassInside, $"{pose} glass layer: {(isInner ? "crease inside the screen only" : "none")}");
        if (glass != null) creaseStrength[pose] = Enumerable.Range(0, glass.Length / 4).Max(i => glass[i * 4 + 3]);
        if (Equals(pose, Folded))
        {
            // 20 px in from each top corner: inside the 60 px hinge-side radius, outside the 180 px opening-side one.
            Check(mask[(screen.Top + 20) * width + screen.Left + 20] == 255 && mask[(screen.Top + 20) * width + screen.Right - 21] == 0,
                "Closed screen corners: tight at the hinge, generous on the opening side");
        }
        if (Equals(pose, tentPose))
        {
            // The closed phone turned a quarter clockwise: the hinge (tight corners) on top, the opening side below.
            Check(mask[(screen.Top + 20) * width + screen.Left + 20] == 255 && mask[(screen.Bottom - 21) * width + screen.Left + 20] == 0,
                "Tent screen corners: tight along the hinge on top, generous at the bottom");
        }
    }
    Check(creaseStrength[HalfOpen] > creaseStrength[Open] * 1.5, "Half open frame shows a deeper crease than open");

    // A capture requested while Open can return after Android has folded or turned upright. It must be identical
    // to a capture requested in the final pose, including the status element's placement and scale.
    var chromeType = assembly.GetType("MuMuDeviceFrames.ChromeState")!;
    var kindType = assembly.GetType("MuMuDeviceFrames.ChromeKind")!;
    object Chrome(string kind, System.Drawing.Size screen) => Activator.CreateInstance(chromeType, Enum.Parse(kindType, kind), true, screen, 480, false, "9:41", false)!;
    var openFrame = Call("DuoFrames", "Frame", frames, Open)!;
    foreach (var (pose, size, kind) in new[] { (Folded, new System.Drawing.Size(1398, 2034), "DuoRailClosed"), (OpenPortrait, new System.Drawing.Size(1878, 2670), "DuoCorner") })
    {
        using var image = new System.Drawing.Bitmap(size.Width, size.Height);
        using (var g = System.Drawing.Graphics.FromImage(image)) g.Clear(System.Drawing.Color.FromArgb(22, 24, 26));
        using var png = new MemoryStream();
        image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        using var duringSwitch = (System.Drawing.Bitmap)Call("FramedScreenshot", "Compose", openFrame, png.ToArray(), null, null, Chrome("DuoRailOpen", new(2670, 1878)))!;
        using var expected = (System.Drawing.Bitmap)Call("FramedScreenshot", "Compose", Call("DuoFrames", "Frame", frames, pose), png.ToArray(), null, null, Chrome(kind, size))!;
        using var actualPng = new MemoryStream();
        using var expectedPng = new MemoryStream();
        duringSwitch.Save(actualPng, System.Drawing.Imaging.ImageFormat.Png);
        expected.Save(expectedPng, System.Drawing.Imaging.ImageFormat.Png);
        Check(actualPng.ToArray().SequenceEqual(expectedPng.ToArray()), $"Screenshot during Open -> {pose} uses the captured pose's frame and chrome");
    }
}
finally
{
    Directory.Delete(frames, recursive: true);
}

// Foldable: the device state tells half open from open on the inner display; the outer display is always closed.
var detectTypes = new[] { typeof(System.Drawing.Size), typeof(System.Drawing.Size?), typeof(int?), typeof(System.Drawing.Size?) };
var inner = new System.Drawing.Size(1878, 2670);
Check(Equals(CallTyped("IPhoneDuo", "Detect", detectTypes, mumuPanel, inner, 1, null), HalfOpen), "Inner display in device state HALF_OPENED is half open");
Check(Equals(CallTyped("IPhoneDuo", "Detect", detectTypes, mumuPanel, inner, 2, null), Open), "Inner display in device state OPENED is open");
Check(Equals(CallTyped("IPhoneDuo", "Detect", detectTypes, mumuPanel, inner, null, null), Open), "Inner display without device states is open");
Check(Equals(CallTyped("IPhoneDuo", "Detect", detectTypes, mumuPanel, new System.Drawing.Size(2034, 1398), 1, null), Folded),
    "Outer display is closed whatever the device state says");

// The one-line device query, as JG-Duo answered it, and as a phone without device states would.
Type duoDevice = assembly.GetType("MuMuDeviceFrames.DuoDevice")!;
T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;
var opened = Call("IPhoneDuo", "ParseDevice", " cur=2670x1878 app=2670x1878 rng=1878x1878-2670x2670\nstates:0,1,2\nstate:2\npostures:1\n")!;
Check(Prop<System.Drawing.Size?>(opened, "Current") == new System.Drawing.Size(2670, 1878) && Prop<bool>(opened, "Foldable")
      && Prop<int?>(opened, "FoldState") == 2 && Prop<bool>(opened, "Postures"), "Device query: foldable, OPENED, postures on, landscape");
var plain = Call("IPhoneDuo", "ParseDevice", " cur=1398x2034 app=1398x2034\nstates:0\nstate:0\npostures:0\n")!;
Check(!Prop<bool>(plain, "Foldable") && Prop<int?>(plain, "FoldState") == null && !Prop<bool>(plain, "Postures"), "Device query: one DEFAULT state is not a foldable");

// Drift: after an Android restart MuMu turns the display back and the state override is gone; both must be noticed.
object Display(System.Drawing.Size current, bool foldable, int? state) => Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.AndroidDisplay")!,
    mumuPanel, 480, (System.Drawing.Size?)inner, Activator.CreateInstance(duoDevice, (System.Drawing.Size?)current, foldable, state, true, null, null, null, null, null), null)!;
var inStepTypes = new[] { duoPose, assembly.GetType("MuMuDeviceFrames.AndroidDisplay")! };
Check((bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, HalfOpen, Display(new(2670, 1878), true, 1))!, "Half open in step");
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, HalfOpen, Display(new(2670, 1878), true, 0))!, "Half open whose device state was reset is out of step");
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, Display(new(1878, 2670), true, 2))!, "Open pose turned portrait by MuMu is out of step");
Check((bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, Display(new(2670, 1878), false, null))!, "Open on a non-foldable Duo needs no device state");

// Order on a foldable: apps must never see a fold while the window is the outer display's size (the extensions throw).
var fold = ((string[])Call("IPhoneDuo", "Commands", Folded, mumuPanel, true)!).ToList();
Check(fold.IndexOf("cmd device_state state 0") < fold.IndexOf("wm size 2034x1398") && fold.Contains("settings delete global display_features"),
    "Folding drops the device state and the fold line before shrinking the display");
var half = ((string[])Call("IPhoneDuo", "Commands", HalfOpen, mumuPanel, true)!).ToList();
Check(half.IndexOf("wm size 1878x2670") < half.IndexOf("cmd device_state state 1")
      && half.IndexOf("settings put global display_features fold-[0,1335,1878,1335]") < half.IndexOf("cmd device_state state 1"),
    "Half opening grows the display and sets the fold line before the device state");
Check(((string[])Call("IPhoneDuo", "Commands", Open, mumuPanel, true)!).Last() == "cmd device_state state 2", "Opening ends in device state OPENED");

// The fold line, in the natural orientation: on MuMu's turned panel the inner display is 1878 wide and 2670 tall, so a
// line across it at y 1335 is the vertical middle of the landscape picture apps see.
Check((string)Call("IPhoneDuo", "FoldFeature", mumuPanel)! == "fold-[0,1335,1878,1335]", "Fold line on MuMu's turned panel");
Check((string)Call("IPhoneDuo", "FoldFeature", new System.Drawing.Size(2670, 2034))! == "fold-[1335,0,1335,1878]", "Fold line on a landscape-natural panel");

// The hotkeys: Ctrl+Alt+D closes anything open and opens a closed phone; Ctrl+Alt+B goes to half open and back to flat.
Check(Equals(Call("IPhoneDuo", "Toggle", HalfOpen), Folded) && Equals(Call("IPhoneDuo", "Toggle", Folded), Open), "Fold hotkey: half open closes, closed opens");
Check(Equals(Call("IPhoneDuo", "ToggleHalf", Open), HalfOpen) && Equals(Call("IPhoneDuo", "ToggleHalf", HalfOpen), Open), "Book hotkey: open half opens, half open flattens");
Check(Equals(Call("IPhoneDuo", "ToggleHalf", OpenPortrait), Tabletop) && Equals(Call("IPhoneDuo", "ToggleHalf", Tabletop), OpenPortrait),
    "Book hotkey keeps the way round: open portrait and tabletop swap");

// MuMu's window after a turn it did not follow: the turned picture sits centred in the old landscape window, which only
// a drag used to fix. MuMu's own toolbar beside the picture (unequal margins) is not that.
Check((bool)Call("WindowFit", "IsLetterboxed", 230, 230, 40, 3)!, "A portrait picture centred in a landscape window is letterboxed");
Check(!(bool)Call("WindowFit", "IsLetterboxed", 4, 4, 40, 3)! && !(bool)Call("WindowFit", "IsLetterboxed", 4, 60, 40, 3)!,
    "MuMu's normal 4 px sides, or its side toolbar, are not letterboxing");
// The fix keeps the scale: a 1009 x 769 picture area turned is 769 x 1009 in a window of the same chrome, same centre.
var fitted = (System.Drawing.Rectangle)Call("WindowFit", "Fit", new System.Drawing.Rectangle(100, 300, 1017, 812), new System.Drawing.Size(586, 769), 215, 216, 40, 3,
    new System.Drawing.Rectangle(0, 0, 3840, 2100))!;
Check(fitted.Width == 769 + 8 && fitted.Height == 1009 + 43 && Math.Abs(fitted.X + fitted.Width / 2 - (100 + 1017 / 2)) <= 1
      && Math.Abs(fitted.Y + fitted.Height / 2 - (300 + 812 / 2)) <= 1,
    $"Letterboxed window refitted round its centre at the same scale ({fitted})");

// The device keeps its centre through pose and size changes, but never hangs off its monitor or under the taskbar.
var anchorWork = new System.Drawing.Rectangle(0, 0, 2560, 1400);
var anchorPlaced = (System.Drawing.Rectangle)Call("DeviceAnchor", "Place", new System.Drawing.Size(605, 833), new System.Drawing.Point(1200, 700), anchorWork)!;
Check(anchorPlaced.X + anchorPlaced.Width / 2 == 1200 && anchorPlaced.Y + anchorPlaced.Height / 2 == 700, "A closed Duo is centred on its anchor");
var anchorOpened = (System.Drawing.Rectangle)Call("DeviceAnchor", "Place", new System.Drawing.Size(1073, 775), new System.Drawing.Point(2300, 700), anchorWork)!;
Check(anchorOpened.Right == 2560 && anchorOpened.Y + anchorOpened.Height / 2 == 700, "Opening near the right edge moves only as far as it takes to stay on the work area");
var anchorHuge = (System.Drawing.Rectangle)Call("DeviceAnchor", "Place", new System.Drawing.Size(1200, 1600), new System.Drawing.Point(1200, 700), anchorWork)!;
Check(anchorHuge.Top == 0, "A device taller than the work area keeps its top on it");

// iOS's top safe area per iPhone, which Android's status bar height is set to: Dynamic Island phones differ by model.
var safeArea = (System.Runtime.CompilerServices.ITuple)Call("DeviceSpecs", "IosSafeArea", "Apple iPhone", "17 Pro")!;
Check((int)safeArea[0]! == 62 && (int)((System.Runtime.CompilerServices.ITuple)Call("DeviceSpecs", "IosSafeArea", "Apple iPhone", "15")!)[0]! == 59
      && (int)((System.Runtime.CompilerServices.ITuple)Call("DeviceSpecs", "IosSafeArea", "Apple iPhone", "SE")!)[0]! == 20,
    "iOS status bar safe areas: 62 pt on the 17 Pro, 59 on the 15, 20 on Touch ID phones");
Check(Call("DeviceSpecs", "IosSafeArea", "Android Phone", "Pixel 8") == null, "Android frames get no iOS safe area");

// The Duo's upright status element (open portrait, tabletop) reaches about 63 pt down, as on Apple's card (dots at
// 59 to 61 pt): Android's top inset in those poses must clear it, and no further.
float cornerBottom = (float)assembly.GetType("MuMuDeviceFrames.IosChrome")!.GetProperty("CornerBottom")!.GetValue(null)!;
Check(cornerBottom is > 60 and < 66, $"Duo corner element ends {cornerBottom:0.0} pt down");
// A device taller than the screen (iPhone 8 at MuMu's default window: 910 x 1817 on a 1392 px work area) shrinks MuMu's
// picture so the whole device fits, rather than hanging under the taskbar.
var fits = (System.Drawing.Size?)Call("DeviceAnchor", "PictureToFit", new System.Drawing.Size(720, 1281), new System.Drawing.Size(910, 1817), new System.Drawing.Rectangle(0, 0, 5120, 1392));
Check(fits is { } f8 && f8.Height * 1817 / 1281 <= 1392 && f8.Height >= 975, $"An iPhone 8 too tall for the screen gets a picture that fits ({fits})");
Check(Call("DeviceAnchor", "PictureToFit", new System.Drawing.Size(720, 1281), new System.Drawing.Size(600, 1200), new System.Drawing.Rectangle(0, 0, 5120, 1392)) == null,
    "A device that fits is left at its size");

// Tent, Apple's sixth pose: the outer display turned landscape (hinge on top), a closed device state, no fold line.
var Tent = Enum.Parse(duoPose, "Tent");
Check(Equals(Call("IPhoneDuo", "ParseCliName", "tent"), Tent) && (string)Call("IPhoneDuo", "Variant", Tent)! == "Tent", "tent parses and names its frame");
var tentCommands = ((string[])Call("IPhoneDuo", "Commands", Tent, new System.Drawing.Size(2034, 2670), true)!).ToList();
Check(tentCommands.Contains("wm size 2034x1398") && tentCommands.Contains("wm user-rotation lock 0") && tentCommands.Contains("cmd device_state state 0")
      && tentCommands.Contains("settings delete global display_features"),
    "Tent: the outer display landscape at rotation 0 (MuMu stretches a narrow landscape display at 90), closed, no fold line");
Check(Equals(CallTyped("IPhoneDuo", "Detect", [typeof(System.Drawing.Size), typeof(System.Drawing.Size?), typeof(int?), typeof(System.Drawing.Size?)],
          new System.Drawing.Size(2034, 2670), (System.Drawing.Size?)new System.Drawing.Size(1398, 2034), (int?)0, (System.Drawing.Size?)new System.Drawing.Size(2034, 1398)), Tent)
      && Equals(CallTyped("IPhoneDuo", "Detect", [typeof(System.Drawing.Size), typeof(System.Drawing.Size?), typeof(int?), typeof(System.Drawing.Size?)],
          new System.Drawing.Size(2034, 2670), (System.Drawing.Size?)new System.Drawing.Size(2034, 1398), (int?)0, (System.Drawing.Size?)new System.Drawing.Size(1398, 2034)), Folded),
    "The outer display shown landscape is tent, upright is closed");
Check(Equals(Call("IPhoneDuo", "Toggle", Tent), Open) && Equals(Call("IPhoneDuo", "PoseForScreen", new System.Drawing.Size(2034, 1398)), Tent),
    "Fold hotkey opens a tent; a 2034 x 1398 screenshot is framed as tent");
var tentPicture = (System.Drawing.Rectangle?)Call("IPhoneDuo", "PictureInRender", Tent, new System.Drawing.Size(769, 1009));
Check(tentPicture is { X: 0, Width: 769, Height: >= 527 and <= 529 }, $"Tent picture in MuMu's upright window: full width, centred ({tentPicture})");
var tentNatural = (System.Runtime.CompilerServices.ITuple)areas.GetType().GetProperty("TentNatural")!.GetValue(areas)!;
Check((int)tentNatural[2]! == 252 && (int)tentNatural[3]! == 102 && (int)tentNatural[1]! == 0, "Tent (rotation 0): rail on the panel's right edge, home indicator on its bottom, nothing on top");

// The menu's geometry. Cells cut in whole pixels have to tile their area exactly at every scale, or a segment edge
// lands on half a pixel and blurs; the pose bar's arrows must skip poses that cannot be chosen and must not wrap;
// a recording's time reads like a clock.
foreach (var (width, count, gap) in new[] { (374, 5, 6), (561, 5, 9), (370, 6, 0), (555, 6, 0), (740, 6, 0), (7, 3, 0) })
{
    var area = new System.Drawing.Rectangle(9, 4, width, 40);
    var cells = (System.Drawing.Rectangle[])Call("MenuLayout", "Split", area, count, gap)!;
    bool tiles = cells.Length == count && cells[0].Left == area.Left && cells[^1].Right == area.Right
        && cells.Zip(cells.Skip(1)).All(p => p.Second.Left == p.First.Right + gap)
        && cells.Max(c => c.Width) - cells.Min(c => c.Width) <= 1 && cells.All(c => c.Y == area.Y && c.Height == area.Height);
    Check(tiles, $"Menu cells: {count} across {width} px with {gap} px gaps tile it exactly, widths within 1 px");
}
var block = (System.Drawing.Rectangle)Call("MenuLayout", "Block", new System.Drawing.Size(398, 70), 6, 6)!;
Check(block == new System.Drawing.Rectangle(6, 3, 386, 64), "Menu block: the side inset of a row highlight, half the gap above and below");
bool[] poseEnabled = [true, true, false, true, false, true];
int Step(int from, int delta, bool[] enabled) => (int)Call("MenuLayout", "Step", from, delta, enabled.ToList())!;
Check(Step(1, 1, poseEnabled) == 3 && Step(3, 1, poseEnabled) == 5 && Step(3, -1, poseEnabled) == 1,
    "Pose bar arrows skip book and tabletop when the emulator cannot half open");
Check(Step(5, 1, poseEnabled) == 5 && Step(0, -1, poseEnabled) == 0, "Pose bar arrows stop at the ends instead of wrapping");
Check(Step(2, 1, [false, false, false]) == 2 && Step(-1, 1, poseEnabled) == 0 && Step(6, -1, poseEnabled) == 5,
    "Pose bar arrows stay put with nothing to choose, and enter from either end");
foreach (var (seconds, text) in new[] { (0, "0:00"), (42, "0:42"), (65, "1:05"), (3599, "59:59"), (3600, "1:00:00"), (3723, "1:02:03"), (-5, "0:00") })
    Check((string)Call("MenuLayout", "Elapsed", TimeSpan.FromSeconds(seconds))! == text, $"Recording time {seconds} s reads {text}");

// The watchdog's view of Android: rotation, fold line, safe areas, the app in front and uptime, from the one query.
var watched = Call("IPhoneDuo", "ParseDevice", " cur=1878x2670 app=1878x2670\nstates:0,1,2\nstate:1\npostures:1\nrotation:0\nfeatures:fold-[0,1335,1878,1335]\n" +
    "cutout:Cutout=DisplayCutout{insets=Rect(0, 0 - 0, 102)\nfocus:mCurrentFocus=Window{7da5b96 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}\nuptime:5123.45\n")!;
Check(Prop<int?>(watched, "Rotation") == 0 && Prop<string?>(watched, "Features") == "fold-[0,1335,1878,1335]" && Prop<string?>(watched, "Focus") == "app.lawnchair"
      && Equals(watched.GetType().GetProperty("Cutout")!.GetValue(watched), ((int, int, int, int)?)(0, 0, 0, 102)) && Prop<double?>(watched, "Uptime") == 5123.45,
    "Device query: rotation, fold line, safe areas, the app in front and uptime");
object Watched(System.Drawing.Size current, int state, int rotation, string features) => Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.AndroidDisplay")!,
    mumuPanel, 480, (System.Drawing.Size?)inner, Activator.CreateInstance(duoDevice, (System.Drawing.Size?)current, true, (int?)state, true, (int?)rotation, features, null, null, null), null)!;
string foldLine = (string)Call("IPhoneDuo", "FoldFeature", mumuPanel)!;
Check((bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, Watched(new(2670, 1878), 2, 1, foldLine))!, "Open at rotation 90 with its fold line is in step");
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, Watched(new(2670, 1878), 2, 3, foldLine))!,
    "Open turned to 270 looks the same but is out of step (its safe areas would be on the wrong edges)");
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, Watched(new(2670, 1878), 2, 1, "null"))!, "Open without its fold line is out of step");
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Folded, Watched(new(1398, 2034), 0, 1, foldLine))!, "Closed with a fold line left over is out of step");
var turnedPairs = new[] { (Open, OpenPortrait), (HalfOpen, Tabletop), (Folded, Tent) };
Check(turnedPairs.All(p => Equals(Call("IPhoneDuo", "Turned", p.Item1), p.Item2) && Equals(Call("IPhoneDuo", "Turned", p.Item2), p.Item1)),
    "Turning the device a quarter: open and open portrait, book and tabletop, closed and tent");
var insetsOf = areas.GetType().GetMethod("Insets")!;
Check(Equals(insetsOf.Invoke(areas, [Open]), (0, 0, 252, 102)) && Equals(insetsOf.Invoke(areas, [Tent]), (0, 0, 252, 102)) && Equals(insetsOf.Invoke(areas, [OpenPortrait]), (0, 204, 0, 102)),
    "Safe areas the watchdog expects: the rail and home indicator, upright the top status element and home indicator");

// Each pose's last picture stands in for the live one at the next switch into that pose. It must never come back for
// another size (the window was resized: it would not fit the frame), and a newer picture must replace an older one.
var shotsType = assembly.GetType("MuMuDeviceFrames.PoseShots")!;
var textureType = assembly.GetType("MuMuDeviceFrames.Texture")!;
var renderKeyType = assembly.GetType("MuMuDeviceFrames.RenderKey")!;
object Texture(byte fill) => Activator.CreateInstance(textureType, Enumerable.Repeat(fill, 16).ToArray(), 2, 2)!;
object Key(string frame, int w, int h) => Activator.CreateInstance(renderKeyType, frame, w, h, 1)!;
var shots = Activator.CreateInstance(shotsType)!;
object? ShotOf(object pose, object key) => shotsType.GetMethod("Get")!.Invoke(shots, [pose, key]);
void KeepShot(object pose, object key, object image) => shotsType.GetMethod("Put")!.Invoke(shots, [pose, key, image]);
var openKey = Key("Apple iPhone/iPhone Duo/Open", 1009, 711);
Check(ShotOf(Open, openKey) == null, "Pose pictures: none before the first switch (the dark screen, as before)");
var first = Texture(10);
KeepShot(Open, openKey, first);
Check(ReferenceEquals(ShotOf(Open, openKey), first), "Pose pictures: the last picture of Open comes back for Open at the same size");
Check(ShotOf(Open, Key("Apple iPhone/iPhone Duo/Open", 1260, 888)) == null, "Pose pictures: not after the window was resized");
Check(ShotOf(Tent, openKey) == null && ShotOf(OpenPortrait, Key("Apple iPhone/iPhone Duo/Open portrait", 711, 1009)) == null,
    "Pose pictures: one pose's picture never stands in for another");
var newer = Texture(20);
KeepShot(Open, openKey, newer);
Check(ReferenceEquals(ShotOf(Open, openKey), newer), "Pose pictures: a newer picture replaces the older one");
shotsType.GetMethod("Clear")!.Invoke(shots, []);
Check(ShotOf(Open, openKey) == null, "Pose pictures: cleared, nothing comes back");

// The safe-area repair turns the display off and on, pausing every app. It once ran every 5 s for 40 minutes when it
// could not help; it must be tried at most three times, with growing gaps, then given up on until things change.
var backoffType = assembly.GetType("MuMuDeviceFrames.Backoff")!;
object NewBackoff() => Activator.CreateInstance(backoffType, [new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10) }])!;
bool TryAt(object b, double seconds) => (bool)backoffType.GetMethod("TryNow")!.Invoke(b, [new DateTime(2026, 1, 1).AddSeconds(seconds)])!;
bool GaveUp(object b) => (bool)backoffType.GetProperty("GaveUp")!.GetValue(b)!;
var repair = NewBackoff();
var tried = Enumerable.Range(0, 120).Where(second => TryAt(repair, second)).ToList();
Check(tried.SequenceEqual([0, 5, 25]), $"Safe-area repair: three tries in two minutes of polling, at 0, 5 and 25 s ({string.Join(", ", tried)})");
Check(GaveUp(repair), "Safe-area repair: given up 10 s after the third try, and nothing more is tried");
var early = NewBackoff();
TryAt(early, 0);
Check(!TryAt(early, 4.9) && TryAt(early, 5) && !GaveUp(early), "Safe-area repair: the second try waits the full 5 s");
backoffType.GetMethod("Reset")!.Invoke(repair, []);
Check(!GaveUp(repair) && TryAt(repair, 200) && (int)backoffType.GetProperty("Tries")!.GetValue(repair)! == 1,
    "Safe-area repair: once they are right again (or the pose changes) the next problem gets three fresh tries");
// The repair itself: the same forced size again (what brings WindowManager's safe areas back).
Check((string)Call("IPhoneDuo", "ForceSizeAgain", new System.Drawing.Size(1878, 2670))! == "wm size 1878x2670", "Safe-area repair forces the display size it has again");
// The watchdog's query no longer dumps WindowManager's windows (that crashed system_server once) unless asked to.
string routine = (string)CallTyped("IPhoneDuo", "DeviceQuery", [typeof(bool)], false)!, withCutout = (string)CallTyped("IPhoneDuo", "DeviceQuery", [typeof(bool)], true)!;
Check(!routine.Contains("dumpsys window") && withCutout.Contains("dumpsys window displays") && !withCutout.Contains("dumpsys window 2"),
    "Watchdog query: no WindowManager dump once a second; its displays section only when the safe areas are read");
var dmShown = Call("IPhoneDuo", "ParseDevice", "shown:real 2670 x 1878\nstates:0,1,2\nstate:2\npostures:1\nfocus:    displayId=0, name='ActivityRecord{120168585 u0 com.justgains.justgainsmobile.internal/.MainActivity t54}', dispatchingTimeout=25000ms\n")!;
Check(Prop<System.Drawing.Size?>(dmShown, "Current") == new System.Drawing.Size(2670, 1878) && Prop<string?>(dmShown, "Focus") == "com.justgains.justgainsmobile.internal",
    "Watchdog query: the size shown from DisplayManager, the app in front from the input dispatcher");

// Android 15 lets edge-to-edge apps draw through cutout insets. Protection is healthy only when the organizer's
// actual app rectangle agrees too, not just when Android reports the desired waterfall.
var protectedDevice = Call("IPhoneDuo", "ParseDevice", "shown:real 2670 x 1878\ncutout:Cutout=DisplayCutout{insets=Rect(0, 0 - 252, 102)\n" +
    "safearea:HideDisplayCutoutController states: mEnabled=true HideDisplayCutoutOrganizer states: mCurrentDisplayBounds=Rect(0, 0 - 2418, 1776)\n" +
    "InsetsSource id=1 type=statusBars frame=[0,0][0,0] visible=true\n")!;
bool Matches(object device, bool protect) => (bool)areas.GetType().GetMethod("Matches")!.Invoke(areas, [Open, device, protect])!;
Check(Prop<bool?>(protectedDevice, "SafeAreaProtection") == true && Prop<System.Drawing.Rectangle?>(protectedDevice, "SafeContent") == new System.Drawing.Rectangle(0, 0, 2418, 1776)
    && Matches(protectedDevice, true), "Protected safe areas: actual organizer bounds and cutout must both match");
var insetOnly = Call("IPhoneDuo", "ParseDevice", "cutout:Cutout=DisplayCutout{insets=Rect(0, 0 - 252, 102)\nsafearea:\nInsetsSource type=statusBars frame=[0,0][0,0]\n")!;
Check(!Matches(insetOnly, true) && Matches(insetOnly, false), "Correct insets without a controller fail protection but pass app-managed mode");
var staleBounds = Call("IPhoneDuo", "ParseDevice", "cutout:Cutout=DisplayCutout{insets=Rect(0, 0 - 252, 102)\nsafearea:mEnabled=true mCurrentDisplayBounds=Rect(0, 0 - 2670, 1878)\n")!;
Check(!Matches(staleBounds, true), "An enabled controller with stale full-display bounds is not healthy");
Check(!Matches(protectedDevice, false), "App-managed mode is not healthy while Android is still protecting app windows");
var staleBar = Call("IPhoneDuo", "ParseDevice", "cutout:Cutout=DisplayCutout{insets=Rect(0, 0 - 252, 102)\n" +
    "safearea:mEnabled=true mCurrentDisplayBounds=Rect(0, 0 - 2418, 1776)\nInsetsSource id=1 type=statusBars frame=[0,0][2670,252] visible=true\n")!;
Check(Prop<int?>(protectedDevice, "StatusBarHeight") == 0 && Prop<int?>(staleBar, "StatusBarHeight") == 252 && !Matches(staleBar, true),
    "Correct protected bounds with a stale 252px status bar fail validation (extra top space after Tent)");
Check(Prop<bool?>(dmShown, "SafeAreaProtection") == null && Prop<System.Drawing.Rectangle?>(dmShown, "SafeContent") == null,
    "Routine pose polls do not invent a protection failure when safe areas were not queried");
Check(((string)Call("AndroidCutout", "DuoSignature", areas, true)!).EndsWith("|protected")
    && ((string)Call("AndroidCutout", "DuoSignature", areas, false)!).EndsWith("|edge-to-edge"), "Protection mode changes invalidate the installed overlay signature");
Check((bool)Call("AndroidCutout", "HasDuoHome", "duo-v6|252|204|102|turned")!
    && (bool)Call("AndroidCutout", "HasDuoHome", Call("AndroidCutout", "DuoSignature", areas, true))!
    && !(bool)Call("AndroidCutout", "HasDuoHome", "duo-v60|other")!, "Home indicator survives the overlay upgrade without accepting unknown versions");
Check(Prop<bool>(Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.InstanceConfig")!)!, "DuoProtectSafeAreas"), "Existing and new emulator configs default to protected system areas");
var safeBounds = areas.GetType().GetMethod("ContentBounds")!;
foreach (var (pose, expected) in new[]
{
    (Folded, new System.Drawing.Rectangle(0, 0, 1146, 1932)), (Open, new System.Drawing.Rectangle(0, 0, 2418, 1776)),
    (HalfOpen, new System.Drawing.Rectangle(0, 0, 2418, 1776)), (Tent, new System.Drawing.Rectangle(0, 0, 1782, 1296)),
    (OpenPortrait, new System.Drawing.Rectangle(0, 204, 1878, 2364)), (Tabletop, new System.Drawing.Rectangle(0, 204, 1878, 2364)),
}) Check(Equals(safeBounds.Invoke(areas, [pose]), expected), $"Protected app window: {pose} = {expected}");
foreach (var panel in new[] { mumuPanel, new System.Drawing.Size(2670, 2034) })
{
    var geometry = Call("DuoSafeAreas", "For", 480, panel)!;
    foreach (var pose in new[] { Folded, Open, HalfOpen, OpenPortrait, Tabletop, Tent })
    {
        var expected = (System.Runtime.CompilerServices.ITuple)insetsOf.Invoke(geometry, [pose])!;
        string edgeSet = pose.Equals(Tent) ? "TentNatural" : (bool)Call("IPhoneDuo", "IsUpright", pose)! ? "UprightNatural" : "RailNatural";
        var edges = (System.Runtime.CompilerServices.ITuple)geometry.GetType().GetProperty(edgeSet)!.GetValue(geometry)!;
        int rotation = (int)Call("IPhoneDuo", "RotationFor", pose, panel.Width < panel.Height)!;
        Check(Enumerable.Range(0, 4).All(side => Equals(expected[side], edges[(int)Call("IPhoneDuo", "PanelEdge", side, rotation)!])),
            $"Safe-area rotation: {pose} on {panel}");
        var transition = (string[])Call("IPhoneDuo", "SwitchCommands", pose, panel, true, true, true)!;
        Check(transition[0] == "cmd device_state state 0" && Array.IndexOf(transition, "settings delete global display_features") < Array.FindIndex(transition, s => s.StartsWith("wm size "))
            && (!(bool)Call("IPhoneDuo", "IsInner", pose)! || transition[^1] == $"cmd device_state state {Call("IPhoneDuo", "StateFor", pose)}")
            && transition.Any(s => s.Contains("sleep 0.2")), $"Pose transition clears the fold before geometry and refreshes protected bounds: {pose} on {panel}");
        string barRefresh = (string)Call("IPhoneDuo", "RefreshStatusBar", pose, panel)!;
        Check(Array.IndexOf(transition, barRefresh) > Array.IndexOf(transition, (string)assembly.GetType("MuMuDeviceFrames.IPhoneDuo")!.GetField("RefreshSafeAreas")!.GetValue(null)!)
            && barRefresh.EndsWith($"wm user-rotation lock {rotation}"), $"Status bar is measured after the new cutout and finishes at the intended rotation: {pose} on {panel}");
    }
}
object driftedDensity = Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.AndroidDisplay")!, mumuPanel, 480, inner,
    Activator.CreateInstance(duoDevice, (System.Drawing.Size?)new System.Drawing.Size(2670, 1878), true, (int?)2, true, (int?)1, foldLine, null, null, null), (int?)320)!;
Check(!(bool)CallTyped("IPhoneDuo", "InStep", inStepTypes, Open, driftedDensity)!, "The watchdog repairs density drift even when the pose geometry is unchanged");

// The tray queue cannot serialize an independent CLI doctor. Exercise the actual named mutex across processes.
string lockFixture = Path.Combine(Path.GetTempPath(), "deviceframes-lock-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(lockFixture);
string testSerial = "test-" + Guid.NewGuid().ToString("N"), readyFile = Path.Combine(lockFixture, "ready"), releaseFile = Path.Combine(lockFixture, "release");
IDisposable Operation() => (IDisposable)Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.AndroidOperation")!, testSerial)!;
using (Operation()) using (Operation()) Check(true, "An emulator operation can call setup or doctor recursively on the same thread");
var childInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) childInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
foreach (string arg in new[] { "--hold-operation", testSerial, readyFile, releaseFile }) childInfo.ArgumentList.Add(arg);
using (var child = System.Diagnostics.Process.Start(childInfo)!)
{
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(readyFile) && DateTime.UtcNow < deadline && !child.HasExited) Thread.Sleep(20);
        Check(File.Exists(readyFile), "Independent CLI process acquired the emulator operation lock");
        var contender = Task.Run(() => { using (Operation()) return true; });
        Thread.Sleep(150);
        bool blocked = !contender.IsCompleted;
        File.WriteAllText(releaseFile, "release");
        Check(blocked && contender.Wait(5000) && contender.Result, "A second process waits for the emulator operation and continues when it is released");
    }
    finally
    {
        File.WriteAllText(releaseFile, "release");
        if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
        Directory.Delete(lockFixture, recursive: true);
    }
}
