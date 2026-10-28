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
// The safe areas are waterfalls on the panel's own edges, turned by each pose group's rotation. Measured with the probe
// on MuMu's turned panel: at 90 the panel's top showed on the left (so its bottom on the right), at 270 on the right.
// Neither rail edge may be the panel's top: upright (rotation 0) a stale rail set would size the status bar.
var areas = Activator.CreateInstance(assembly.GetType("MuMuDeviceFrames.DuoSafeAreas")!, 252, 204, 102, true)!;
var railNatural = (System.Runtime.CompilerServices.ITuple)areas.GetType().GetProperty("RailNatural")!.GetValue(areas)!;
var uprightNatural = (System.Runtime.CompilerServices.ITuple)areas.GetType().GetProperty("UprightNatural")!.GetValue(areas)!;
Check((int)railNatural[3]! == 252 && (int)railNatural[0]! == 102 && (int)railNatural[1]! == 0 && (int)railNatural[2]! == 0,
    "Rail poses (rotation 90): right 84 pt from the panel's bottom edge, bottom 34 pt from its left edge, nothing on its top");
Check((int)uprightNatural[3]! == 102 && (int)uprightNatural[0]! == 0 && (int)uprightNatural[1]! == 0 && (int)uprightNatural[2]! == 0,
    "Upright poses (rotation 0): the home indicator on the panel's bottom edge; their top comes from the status bar");
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
