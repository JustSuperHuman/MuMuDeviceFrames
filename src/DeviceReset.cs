namespace MuMuDeviceFrames;

internal sealed record ResetResult(
    bool Ok,
    bool Restarted,
    bool MissingTools,
    string Summary,
    AndroidDisplay? Display,
    DuoPose? Pose);

/// <summary>
/// Brings an emulator's Android display, density, rotation, safe-area overlays, status bar and MuMu host window back in
/// line with its target frame in one pass. Pure geometry helpers stay separate so tests can exercise every branch
/// without a live emulator.
/// </summary>
internal static class DeviceReset
{
    private const string ClearOverrides =
        "wm size reset; wm density reset; wm fixed-to-user-rotation default; wm user-rotation free; " +
        "settings delete global display_features; cmd device_state state reset";

    /// <summary>
    /// Android <c>user-rotation</c> lock value (<c>0</c> = natural, <c>1</c> = 90°) that puts the display the same way
    /// round as the frame, whether MuMu's underlying panel is portrait-natural or landscape-natural.
    /// </summary>
    public static int TargetRotation(bool frameIsLandscape, Size physicalPanel) =>
        frameIsLandscape == (physicalPanel.Width > physicalPanel.Height) ? 0 : 1;

    /// <summary>
    /// True when MuMu's physical panel does not match <paramref name="spec"/> and needs <c>MuMuControl.SetResolution</c>
    /// plus an emulator restart. Portrait frames require a portrait physical panel so cutout overlays measure 1:1;
    /// landscape frames accept MuMu's rotated-panel representation as well.
    /// </summary>
    public static bool NeedsPanelRestart(DeviceSpec spec, AndroidDisplay display, bool frameIsLandscape)
    {
        if (!frameIsLandscape) return display.Native != spec.Native;
        return display.Native != spec.Native && display.Native != new Size(spec.Native.Height, spec.Native.Width);
    }

    /// <summary>
    /// Clamps an anchor so the entire rendered frame (and any reserved top tab bar margin in <paramref name="work"/>)
    /// lies inside <paramref name="work"/>, healing off-screen or corner-wedged anchors.
    /// </summary>
    public static Point ClampAnchor(Size frameSize, Point anchor, Rectangle work) =>
        DeviceAnchor.Centre(DeviceAnchor.Place(frameSize, anchor, work));

    /// <summary>
    /// Computes clean host window bounds when the window is letterboxed, wrong-way-round, larger than the work area, or
    /// accidentally shrunk tiny. Returns <c>null</c> when the current window bounds already fit cleanly.
    /// </summary>
    public static Rectangle? IdealWindowBounds(Rectangle window, Rectangle render, Size frameSize, Size nativePanel, Rectangle work)
    {
        if (render.Width <= 0 || render.Height <= 0 || work.Width <= 0 || work.Height <= 0) return null;

        int left = render.Left - window.Left, right = window.Right - render.Right;
        int top = render.Top - window.Top, bottom = window.Bottom - render.Bottom;
        bool letterboxed = WindowFit.IsLetterboxed(left, right, top, bottom);

        int chromeW = left > WindowFit.Slack && right > WindowFit.Slack && Math.Abs(left - right) <= 2 ? 2 * WindowFit.Side : Math.Max(0, left + right);
        int chromeH = bottom > WindowFit.Slack ? Math.Max(0, top - bottom + 2 * WindowFit.Below) : Math.Max(0, top + bottom);

        bool wantLandscape = frameSize.Width > frameSize.Height;
        double targetAspect = wantLandscape
            ? (double)Math.Max(nativePanel.Width, nativePanel.Height) / Math.Max(1, Math.Min(nativePanel.Width, nativePanel.Height))
            : (double)Math.Min(nativePanel.Width, nativePanel.Height) / Math.Max(1, Math.Max(nativePanel.Width, nativePanel.Height));

        int currentLong = Math.Max(render.Width, render.Height);
        int workShort = Math.Min(work.Width - chromeW, work.Height - chromeH);
        int minComfortableLong = (int)Math.Round(workShort * 0.45);
        bool tooSmall = currentLong < minComfortableLong && Math.Max(nativePanel.Width, nativePanel.Height) >= minComfortableLong;
        bool wrongWayRound = (render.Width > render.Height) != wantLandscape;
        bool tooLarge = frameSize.Width > work.Width || frameSize.Height > work.Height;

        if (!letterboxed && !tooSmall && !wrongWayRound && !tooLarge) return null;

        // Pick the desired picture size before chrome: keep current scale unless too small or too large.
        double desiredLong = currentLong;
        if (tooSmall)
        {
            double goalHeight = (work.Height - chromeH) * 0.82;
            double goalWidth = (work.Width - chromeW) * 0.82;
            desiredLong = wantLandscape ? Math.Min(goalWidth, goalHeight * targetAspect) : Math.Min(goalHeight, goalWidth / targetAspect);
            desiredLong = Math.Min(desiredLong, Math.Max(nativePanel.Width, nativePanel.Height));
        }

        double picW = wantLandscape ? desiredLong : desiredLong * targetAspect;
        double picH = wantLandscape ? desiredLong / targetAspect : desiredLong;

        // Account for the bezel's proportion over the picture so the whole framed device fits inside work.
        double bezelFactorW = !wrongWayRound && render.Width > 0 ? Math.Max(1.0, (double)frameSize.Width / render.Width) : 1.14;
        double bezelFactorH = !wrongWayRound && render.Height > 0 ? Math.Max(1.0, (double)frameSize.Height / render.Height) : 1.10;
        double maxPicW = Math.Max(120, (work.Width / bezelFactorW));
        double maxPicH = Math.Max(120, (work.Height / bezelFactorH));
        double shrink = Math.Min(1.0, Math.Min(maxPicW / picW, maxPicH / picH));
        picW *= shrink;
        picH *= shrink;

        // Also guarantee the raw MuMu window itself fits work.
        double winShrink = Math.Min(1.0, Math.Min((work.Width - chromeW) / picW, (work.Height - chromeH) / picH));
        int finalW = (int)Math.Round(picW * winShrink) + chromeW;
        int finalH = (int)Math.Round(picH * winShrink) + chromeH;

        int x = window.X + window.Width / 2 - finalW / 2;
        int y = window.Y + window.Height / 2 - finalH / 2;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - finalW));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - finalH));
        var candidate = new Rectangle(x, y, finalW, finalH);
        return Math.Abs(candidate.Width - window.Width) <= 2 && Math.Abs(candidate.Height - window.Height) <= 2 && !letterboxed ? null : candidate;
    }

    public static ResetResult Run(
        MuMuInstance instance,
        string title,
        FrameInfo frame,
        AppConfig appConfig,
        InstanceConfig settings,
        Func<Size?> currentRenderSize,
        Action<string>? progress = null)
    {
        using var operation = instance.ExclusiveOperation();

        if (IPhoneDuo.IsDuo(frame))
            return RunDuo(instance, title, frame, appConfig.AndroidSdk, settings, currentRenderSize, progress);

        return RunStandard(instance, title, frame, appConfig, settings, currentRenderSize, progress);
    }

    private static ResetResult RunDuo(
        MuMuInstance instance,
        string title,
        FrameInfo frame,
        string? sdkRoot,
        InstanceConfig settings,
        Func<Size?> currentRenderSize,
        Action<string>? progress)
    {
        var pose = IPhoneDuo.PoseOf(frame) ?? settings.DuoPose ?? DuoPose.Folded;
        settings.DuoPose = pose;
        settings.Frame = IPhoneDuo.FrameKey(pose);

        var initial = MuMuControl.QueryDisplay(instance);
        bool willRestart = initial == null || !IPhoneDuo.IsSurface(initial.Native) || initial.Density != IPhoneDuo.Density || initial.Duo?.Foldable != true;
        if (willRestart) progress?.Invoke($"{title}: setting up iPhone Duo display and foldable states (restarting)…");

        var findings = DuoDoctor.Run(instance, title, pose, sdkRoot, repair: true, settings);
        bool failed = findings.Any(f => f.Outcome == DuoDoctor.Outcome.Failed);

        // Ensure MuMu's host window matches the Duo pose's orientation too.
        bool wantUpright = IPhoneDuo.TurnsWindow(pose);
        if (currentRenderSize() is { Width: > 0, Height: > 0 } render && (render.Width < render.Height) != wantUpright)
        {
            MuMuControl.TurnWindow(instance);
            Thread.Sleep(350);
        }

        var display = MuMuControl.QueryDisplay(instance, cutout: true);
        var reached = IPhoneDuo.Detect(display) ?? pose;
        if (failed)
        {
            string reason = findings.First(f => f.Outcome == DuoDoctor.Outcome.Failed).Text;
            return new ResetResult(false, willRestart, false, $"{title}: {reason}", display, reached);
        }
        return new ResetResult(true, willRestart, false, $"{title}: reset to iPhone Duo ({IPhoneDuo.Variant(reached)}).", display, reached);
    }

    private static ResetResult RunStandard(
        MuMuInstance instance,
        string title,
        FrameInfo frame,
        AppConfig appConfig,
        InstanceConfig settings,
        Func<Size?> currentRenderSize,
        Action<string>? progress)
    {
        var spec = DeviceSpecs.For(frame);
        if (MuMuControl.GetSetting(instance, "window_auto_rotate") != "true")
            MuMuControl.SetSettings(instance, ("window_auto_rotate", "true"));

        var display = MuMuControl.QueryDisplay(instance);
        if (display == null)
            return new ResetResult(false, false, false, $"{title}: Android is not responding over adb.", null, null);

        // Clean up any leftover foldable or wm overrides before evaluating or fitting the panel.
        if (settings.DuoOverlay != null || display.Duo?.Postures == true)
            instance.Shell($"cmd overlay disable --user 0 {IPhoneDuo.PosturesOverlay} 2>/dev/null");
        settings.DuoOverlay = null;
        settings.DuoPose = null;
        if (display.Override != null || display.DensityOverride != null || display.Duo?.Foldable == true)
        {
            instance.Shell(ClearOverrides);
            display = MuMuControl.QueryDisplay(instance) ?? display;
        }

        bool restarted = false;
        if (NeedsPanelRestart(spec, display, frame.IsLandscape))
        {
            restarted = true;
            progress?.Invoke($"{title}: setting screen to {spec.Native.Width}×{spec.Native.Height} at {spec.Density} dpi and restarting…");
            string? foreground = DeviceFit.ForegroundApp(instance);
            MuMuControl.SetResolution(instance, spec.Native, spec.Density);
            string? restartError = MuMuControl.RestartAndWait(instance, TimeSpan.FromMinutes(3));
            if (restartError != null)
                return new ResetResult(false, true, false, $"{title}: {restartError}", null, null);

            display = MuMuControl.QueryDisplay(instance);
            if (display == null)
                return new ResetResult(false, true, false, $"{title}: Android did not report its display after restarting.", null, null);

            if ((display.DensityOverride ?? display.Density) != spec.Density)
            {
                instance.Shell($"wm density {spec.Density}");
                display = MuMuControl.QueryDisplay(instance) ?? display;
            }
            if (foreground != null)
                instance.Shell($"monkey -p {foreground} -c android.intent.category.LAUNCHER 1");
        }
        else
        {
            MuMuControl.SetResolution(instance, display.Native, spec.Density);
            if (display.Density == spec.Density) instance.Shell("wm density reset");
            else instance.Shell($"wm density {spec.Density}");
            display = MuMuControl.QueryDisplay(instance) ?? display;
        }

        // Put Android in the frame's orientation (portrait for portrait frames, landscape for landscape ones).
        int rotation = TargetRotation(frame.IsLandscape, display.Native);
        instance.Shell($"wm fixed-to-user-rotation default; wm user-rotation lock {rotation}");
        Thread.Sleep(220);

        // Rebuild and apply cutout / safe-area overlays immediately (including any queued PendingIosSetup).
        bool missingTools = false;
        string? cutoutIssue = null;
        settings.PendingIosSetup = null;
        bool wantsCutout = appConfig.CutoutFor(title, frame);
        bool iosBar = appConfig.IosStatusBarFor(title, frame);
        bool wantsHome = settings.IosHomeIndicator != false;

        if (!wantsCutout)
        {
            if (settings.InstalledCutout != null)
            {
                AndroidCutout.Remove(instance);
                settings.InstalledCutout = null;
            }
            AndroidStatusBar.Restore(instance);
        }
        else
        {
            var missing = AndroidTools.Missing(appConfig.AndroidSdk);
            if (missing.Count > 0)
            {
                missingTools = true;
            }
            else if (AndroidCutout.Measure(frame, display, appConfig.StatusBarPaddingFactor, ios: iosBar, withHome: wantsHome) is { } cutoutSpec)
            {
                cutoutIssue = AndroidCutout.Apply(instance, cutoutSpec, appConfig.AndroidSdk, iosBar);
                if (cutoutIssue == null)
                    settings.InstalledCutout = AndroidCutout.Signature(frame, display, appConfig.StatusBarPaddingFactor, iosBar, wantsHome);
            }

            if (iosBar && settings.InstalledCutout != null) AndroidStatusBar.Hide(instance);
            else AndroidStatusBar.Restore(instance);
        }

        // If MuMu's host window is still turned the opposite way from the frame, turn the window to match.
        bool wantLandscape = frame.IsLandscape;
        if (currentRenderSize() is { Width: > 0, Height: > 0 } renderSize && (renderSize.Width > renderSize.Height) != wantLandscape)
        {
            MuMuControl.TurnWindow(instance);
            Thread.Sleep(350);
        }

        var finalDisplay = MuMuControl.QueryDisplay(instance) ?? display;
        if (cutoutIssue != null)
            return new ResetResult(false, restarted, false, $"{title}: screen reset, but safe areas failed ({cutoutIssue}).", finalDisplay, null);

        string orientation = wantLandscape ? "landscape" : "portrait";
        return new ResetResult(true, restarted, missingTools,
            $"{title}: reset to {frame.Model} ({spec.Native.Width}×{spec.Native.Height} · {spec.Density} dpi, {orientation}).",
            finalDisplay, null);
    }
}
