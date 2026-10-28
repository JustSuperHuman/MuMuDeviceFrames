namespace MuMuDeviceFrames;

/// <summary>
/// Health check for an iPhone Duo emulator: every piece the Duo needs, checked in order, repaired when it can be, and
/// a line for each saying what was found or fixed. The structural pieces (surface, device states) repair through
/// <see cref="DuoSetup"/>, which restarts the emulator; everything after that is live.
/// </summary>
internal static class DuoDoctor
{
    public enum Outcome { Ok, Fixed, Failed, Note }

    public sealed record Finding(Outcome Outcome, string Text)
    {
        public override string ToString() => $"{Outcome switch { Outcome.Ok => "OK   ", Outcome.Fixed => "FIXED", Outcome.Failed => "FAIL ", _ => "NOTE " }} {Text}";
    }

    /// <param name="intended">The pose the tray app keeps this emulator in (from the config), if it has one.</param>
    public static List<Finding> Run(MuMuInstance instance, string title, DuoPose? intended, string? sdkRoot, bool repair)
    {
        var findings = new List<Finding>();
        void Add(Outcome outcome, string text)
        {
            findings.Add(new Finding(outcome, text));
            Log.Write($"'{title}': Duo doctor: {new Finding(outcome, text)}");
        }

        var display = MuMuControl.QueryDisplay(instance);
        if (display == null)
        {
            Add(Outcome.Failed, "Android is not answering over adb.");
            return findings;
        }

        // 1. The structural pieces: MuMu's settings, the surface, the device states. Repairs restart the emulator.
        string autoRotate = MuMuControl.GetSetting(instance, "window_auto_rotate") ?? "?";
        bool surface = IPhoneDuo.IsSurface(display.Native) && display.Density == IPhoneDuo.Density;
        bool foldable = display.Duo?.Foldable == true;
        if (!surface || !foldable)
        {
            string missing = string.Join(", ", new[]
            {
                surface ? null : $"surface (is {display.Native.Width}x{display.Native.Height} @ {display.Density} dpi)",
                foldable ? null : "foldable device states",

            }.Where(m => m != null));
            if (!repair)
            {
                Add(Outcome.Failed, $"Not set up: {missing}.");
                return findings;
            }
            if (DuoSetup.Run(instance, title, sdkRoot, _ => { }) is { } error)
            {
                Add(Outcome.Failed, $"Setup ({missing}): {error}");
                return findings;
            }
            Add(Outcome.Fixed, $"Set up {missing} (restarted the emulator).");
            display = MuMuControl.QueryDisplay(instance) ?? display;
        }
        else
        {
            Add(Outcome.Ok, $"Surface {IPhoneDuo.Surface.Width}x{IPhoneDuo.Surface.Height} at {IPhoneDuo.Density} dpi.");
            Add(Outcome.Ok, "Device states CLOSED (0), HALF_OPENED (1), OPENED (2).");
        }

        // MuMu has to turn its window with Android (it is live, no restart); off, the picture shrinks into the old window.
        if (autoRotate == "true") Add(Outcome.Ok, "MuMu turns its window with Android (window_auto_rotate on).");
        else if (!repair) Add(Outcome.Failed, "MuMu window auto-rotate is off: turning the display leaves the picture letterboxed.");
        else
        {
            MuMuControl.SetSettings(instance, ("window_auto_rotate", "true"));
            Add(Outcome.Fixed, "Turned MuMu's window auto-rotate on.");
        }

        // 2. What apps need to get the fold: Jetpack's OEM library and the postures mapping.
        string libraries = instance.Shell("pm list libraries").Output;
        if (libraries.Contains("androidx.window.extensions")) Add(Outcome.Ok, "Window extensions library (androidx.window.extensions) is on the image.");
        else Add(Outcome.Failed, "This Android image has no androidx.window.extensions: apps cannot get a FoldingFeature.");

        string postures = instance.Shell("cmd overlay lookup android android:array/config_device_state_postures").Output;
        bool posturesOk = postures.Contains("0:1") && postures.Contains("1:2") && postures.Contains("2:3");
        if (posturesOk) Add(Outcome.Ok, "Postures overlay maps 0:1, 1:2, 2:3.");
        else if (!repair) Add(Outcome.Failed, "Postures overlay missing.");
        else if (DuoSetup.InstallPostures(instance, sdkRoot) is { } overlayError) Add(Outcome.Failed, $"Postures overlay: {overlayError}");
        else Add(Outcome.Fixed, "Installed the postures overlay.");

        // 3. The pose: display, rotation, device state and fold line all agreeing.
        display = MuMuControl.QueryDisplay(instance) ?? display;
        var shown = IPhoneDuo.Detect(display);
        var pose = intended ?? shown ?? DuoPose.Folded;
        string features = instance.Shell("settings get global display_features").Output.Trim();
        string wantFeatures = IPhoneDuo.IsInner(pose) ? IPhoneDuo.FoldFeature(display.Native) : "null";
        bool inStep = shown == pose && IPhoneDuo.InStep(pose, display) && features == wantFeatures && display.DensityOverride is null or IPhoneDuo.Density;
        if (inStep)
        {
            Add(Outcome.Ok, $"Pose {IPhoneDuo.Variant(pose)}: {display.Duo?.Current?.Width}x{display.Duo?.Current?.Height}, device state {display.Duo?.FoldState}, fold line {(features == "null" ? "none" : features)}.");
        }
        else if (!repair)
        {
            Add(Outcome.Failed, $"Pose {IPhoneDuo.Variant(pose)} out of step (shows {shown?.ToString() ?? "no pose"}, sees {display.Duo?.Current}, state {display.Duo?.FoldState}, fold line {features}).");
        }
        else
        {
            instance.Shell(string.Join("; ", IPhoneDuo.Commands(pose, display.Native, foldable: true)));
            var now = MuMuControl.QueryDisplay(instance);
            if (IPhoneDuo.Detect(now) == pose && IPhoneDuo.InStep(pose, now)) Add(Outcome.Fixed, $"Put the pose back to {IPhoneDuo.Variant(pose)} (was {shown?.ToString() ?? "none"}, state {display.Duo?.FoldState}).");
            else Add(Outcome.Failed, $"Could not put the pose back to {IPhoneDuo.Variant(pose)}.");
        }

        // 3b. The safe areas apps get (WindowManager's cutout) for the pose, when the safe-area overlay is in.
        string overlays = instance.Shell("cmd overlay list android").Output;
        if (overlays.Contains("[x] " + AndroidCutout.FrameworkOverlay, StringComparison.Ordinal) && IPhoneDuo.Detect(display) is { } posed
            && DuoSafeAreas.For(display.Density, display.Native) is { } areas)
        {
            var want = areas.Insets(posed);
            (int, int, int, int)? Read() => MuMuControl.QueryDisplay(instance, cutout: true)?.Duo?.Cutout;
            var got = Read();
            if (got == want) Add(Outcome.Ok, $"Safe areas {want} for {IPhoneDuo.Variant(posed)}.");
            else if (!repair) Add(Outcome.Failed, $"Safe areas apps get are {got?.ToString() ?? "unknown"}, not {want}.");
            else
            {
                if (display.Override is { } forced) instance.Shell(IPhoneDuo.ForceSizeAgain(forced));
                Thread.Sleep(500);
                if (Read() != want)
                {
                    instance.Shell(IPhoneDuo.RefreshSafeAreas);
                    Thread.Sleep(1000);
                }
                var now = Read();
                if (now == want) Add(Outcome.Fixed, $"Safe areas were {got?.ToString() ?? "unknown"}; now {want}.");
                else Add(Outcome.Failed, $"Safe areas are still {now?.ToString() ?? "unknown"}, not {want}.");
            }
        }

        // 4. iOS's navigation gestures (MuMu switches them off at every boot; the tray app turns them back on).
        bool rooted = MuMuControl.GetSetting(instance, "root_permission") == "true";
        if (DuoSetup.GesturesOn(instance)) Add(Outcome.Ok, "Navigation gestures on (swipe up for home, from an edge for back).");
        else if (!rooted) Add(Outcome.Note, "Navigation gestures off: they need MuMu's root permission (on for new iPhone Duos).");
        else if (!repair) Add(Outcome.Failed, "Navigation gestures are off.");
        else if (DuoSetup.EnableGestures(instance) is { } gestureError) Add(Outcome.Failed, $"Could not turn the navigation gestures on: {gestureError}");
        else Add(Outcome.Fixed, "Turned the navigation gestures back on.");

        // 5. Notes: the tray app.
        if (!TrayApp.IsRunning) Add(Outcome.Note, "The tray app is not running, so nothing heals the pose after an Android restart.");
        return findings;
    }
}
