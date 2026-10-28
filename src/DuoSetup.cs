namespace MuMuDeviceFrames;

/// <summary>
/// Turns a MuMu emulator into the iPhone Duo: the 2670 × 2034 surface at 480 dpi, and an Android foldable with the
/// device states CLOSED, HALF_OPENED and OPENED, so apps get Jetpack WindowManager's FoldingFeature.
///
/// The device states live in /data/system/devicestate/device_state_configuration.xml, which only root can write. On
/// MuMu's Android 15 image root is <c>adb root</c> (there is no <c>su</c>), available after MuMu's root_permission
/// setting and a restart. So the first time this takes two restarts: one with root to write the file, one with root
/// back as it was so Android reads it. Every step is skipped when already done; run again, it just applies the closed
/// pose. After that nothing needs root: the pose commands run as the shell user.
/// </summary>
internal static class DuoSetup
{
    private const string StatesDir = "/data/system/devicestate", StatesFile = StatesDir + "/device_state_configuration.xml";

    /// <summary>Maps the device states to Jetpack postures (a framework overlay; apps read it once per process).</summary>
    public static string? InstallPostures(MuMuInstance instance, string? sdkRoot) =>
        AndroidCutout.InstallOverlay(instance, IPhoneDuo.PosturesOverlay, "android", "iPhone Duo postures", IPhoneDuo.PosturesValues, sdkRoot);

    /// <summary>Runs every missing step. Null on success, otherwise what went wrong; progress goes to the callback and the log.</summary>
    public static string? Run(MuMuInstance instance, string title, string? sdkRoot, Action<string> progress)
    {
        void Say(string text)
        {
            Log.Write($"'{title}': Duo setup: {text}");
            progress(text);
        }

        var display = MuMuControl.QueryDisplay(instance);
        if (display == null) return "Android is not answering over adb.";
        bool resize = !IPhoneDuo.IsSurface(display.Native) || display.Density != IPhoneDuo.Density;
        bool addStates = display.Duo?.Foldable != true;

        // MuMu turns its window (and Android with it) to follow the app in front; the Duo surface has to stay put.
        MuMuControl.SetSettings(instance, ("window_auto_rotate", "false"));
        if (resize) MuMuControl.SetResolution(instance, IPhoneDuo.Surface, IPhoneDuo.Density);

        if (addStates)
        {
            string root = MuMuControl.GetSetting(instance, "root_permission") ?? "false";
            if (root != "true") MuMuControl.SetSettings(instance, ("root_permission", "true"));
            Say("restarting with root to add the foldable device states");
            if (RestartAndWait(instance) is { } restartError) return restartError;
            string? error = WriteDeviceStates(instance);
            if (root != "true") MuMuControl.SetSettings(instance, ("root_permission", root));
            if (error != null) return error;
            Say("restarting so Android loads the device states");
            if (RestartAndWait(instance) is { } secondError) return secondError;
        }
        else if (resize)
        {
            Say($"restarting at {IPhoneDuo.Surface.Width}x{IPhoneDuo.Surface.Height}");
            if (RestartAndWait(instance) is { } restartError) return restartError;
        }

        display = MuMuControl.QueryDisplay(instance);
        if (display?.Duo is not { Foldable: true } duo) return "Android did not load the device states (cmd device_state print-states).";
        if (!duo.Postures)
        {
            Say("installing the postures overlay");
            if (InstallPostures(instance, sdkRoot) is { } overlayError) return overlayError;
        }
        instance.Shell(string.Join("; ", IPhoneDuo.Commands(DuoPose.Folded, display.Native, foldable: true)));
        Say("done: CLOSED, HALF_OPENED and OPENED, folded");
        return null;
    }

    /// <summary>As root: writes the state file with the owner, mode and SELinux label system_server expects.</summary>
    private static string? WriteDeviceStates(MuMuInstance instance)
    {
        instance.Adb("root");
        bool rooted = false;
        for (int i = 0; i < 20 && !rooted; i++)
        {
            Thread.Sleep(1000);
            MuMuControl.Reconnect(instance);
            rooted = instance.Shell("id").Output.Contains("uid=0(");
        }
        if (!rooted) return "adb root did not take (MuMu root permission is off?).";

        string local = Path.Combine(Path.GetTempPath(), $"device_state_configuration-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(local, IPhoneDuo.DeviceStateConfig.Replace("\r\n", "\n"));
            const string staged = "/data/local/tmp/device_state_configuration.xml";
            var push = instance.Adb("push", local, staged);
            if (push.ExitCode != 0) return $"adb push failed: {push.Output.Trim()}";
            instance.Shell($"mkdir -p {StatesDir} && cp {staged} {StatesFile} && chown -R system:system {StatesDir} && " +
                           $"chmod 0755 {StatesDir} && chmod 0644 {StatesFile} && restorecon -R {StatesDir}; rm -f {staged}");
            string check = instance.Shell($"ls -lZ {StatesFile}").Output;
            return check.Contains("system system") && check.Contains("system_data_file") ? null : $"Could not write {StatesFile}: {check.Trim()}";
        }
        finally
        {
            File.Delete(local);
        }
    }

    private static string? RestartAndWait(MuMuInstance instance)
    {
        MuMuControl.Restart(instance);
        Thread.Sleep(6000); // the old Android answers "booted" until it has gone down
        return MuMuControl.WaitForBoot(instance, TimeSpan.FromMinutes(3)) ? null : "The emulator did not come back after the restart.";
    }
}
