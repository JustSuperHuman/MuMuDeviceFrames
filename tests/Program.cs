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
    Check(drawn.Count == 5, "All five Duo frames drawn");
    var creaseStrength = new Dictionary<object, int>();
    foreach (var pose in new[] { Folded, Open, HalfOpen, OpenPortrait, Tabletop })
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
        bool isInner = !Equals(pose, Folded);
        Check((glass != null) == isInner && glassInside, $"{pose} glass layer: {(isInner ? "crease inside the screen only" : "none")}");
        if (glass != null) creaseStrength[pose] = Enumerable.Range(0, glass.Length / 4).Max(i => glass[i * 4 + 3]);
        if (Equals(pose, Folded))
        {
            // 20 px in from each top corner: inside the 60 px hinge-side radius, outside the 180 px opening-side one.
            Check(mask[(screen.Top + 20) * width + screen.Left + 20] == 255 && mask[(screen.Top + 20) * width + screen.Right - 21] == 0,
                "Closed screen corners: tight at the hinge, generous on the opening side");
        }
    }
    Check(creaseStrength[HalfOpen] > creaseStrength[Open] * 1.5, "Half open frame shows a deeper crease than open");
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
    mumuPanel, 480, (System.Drawing.Size?)inner, Activator.CreateInstance(duoDevice, (System.Drawing.Size?)current, foldable, state, true), null)!;
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

// The Duo's upright bar: its items start where the 44 pt screen corner can no longer cut them (6 pt down, where the
// glyphs begin, the arc is 44 - sqrt(44^2 - 38^2) = 21.8 pt in).
var duoBar = Call("IosChrome", "DuoBarLayout", new System.Drawing.Size(1878, 2670), 480)!;
int duoInset = (int)duoBar.GetType().GetProperty("Inset")!.GetValue(duoBar)!;
Check(duoInset / 3.0 >= 44 - Math.Sqrt(44 * 44 - 38 * 38) + 4, $"Duo status bar items clear the screen corner ({duoInset / 3.0} pt in)");
