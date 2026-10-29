using System.Diagnostics;
using static MuMuDeviceFrames.Native;

namespace MuMuDeviceFrames;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Maintenance commands (no tray):
        //   --restore                          un-clip every emulator window (after a crash or kill of this app)
        //   --snapshot <title> <out.png> [key|-] [checker|transparent|#RRGGBB]
        //                                      offline composite of emulator + frame, for checking alignment
        //   --download [folder]                fetch the frame set (default: the configured FramesRoot)
        if (args.Length >= 1 && args[0] == "--download")
        {
            string root = args.Length > 1 ? Path.GetFullPath(args[1]) : AppConfig.Load().FramesRoot;
            var result = FrameDownloader.DownloadAsync(root, new Progress<string>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            Log.Write($"Download to {root}: {result.Downloaded} new, {result.UpToDate} up to date, {result.Failed} failed of {result.Total}");
            return result.Failed == 0 ? 0 : 1;
        }
        //   --startup on|off                   start with Windows (per-user Run key)
        if (args.Length >= 2 && args[0] == "--startup") { Startup.Set(args[1] == "on"); return 0; }
        //   --exit                             ask the running instance to quit cleanly
        if (args.Length >= 1 && args[0] == "--exit") return TrayApp.RequestExitOfRunningInstance() ? 0 : 1;
        //   --expo-reload / --expo-gear [name] run an Expo action on the active or named emulator
        if (args.Length is 1 or 2 && args[0] == "--expo-reload")
            return args.Length == 2 ? RunExpoCommand(args[1], ExpoAction.Reload) : TrayApp.RequestExpoActionOfRunningInstance(ExpoAction.Reload) ? 0 : 1;
        if (args.Length is 1 or 2 && args[0] == "--expo-gear")
            return args.Length == 2 ? RunExpoCommand(args[1], ExpoAction.ToggleGear) : TrayApp.RequestExpoActionOfRunningInstance(ExpoAction.ToggleGear) ? 0 : 1;
        //   --duo-fold / --duo-unfold / --duo-half [name]
        //                                      iPhone Duo closed, open flat or half open (book), through the running tray
        //                                      app so the frame follows (active or named emulator); without it, adb directly
        //   --duo-pose <closed|open|book|portrait|tabletop|tent> [name]   any of the six poses
        if (args.Length is 2 or 3 && args[0] == "--duo-pose")
        {
            if (IPhoneDuo.ParseCliName(args[1]) is not { } chosen) { Log.Write($"--duo-pose: unknown pose '{args[1]}'"); return 2; }
            string? who = args.Length == 3 ? args[2] : null;
            return RunDuoCommand(who, chosen);
        }
        if (args.Length is 1 or 2 && args[0] is "--duo-fold" or "--duo-unfold" or "--duo-half")
        {
            var pose = args[0] switch { "--duo-fold" => DuoPose.Folded, "--duo-half" => DuoPose.HalfOpen, _ => DuoPose.Open };
            string? name = args.Length == 2 ? args[1] : null;
            return RunDuoCommand(name, pose);
        }
        //   --duo-setup <name>                 make an emulator the iPhone Duo: surface, foldable device states (root once,
        //                                      up to two restarts), postures overlay; then folded
        if (args.Length == 2 && args[0] == "--duo-setup")
        {
            var instance = MuMuControl.LocateByName(args[1]);
            if (instance == null) { Log.Write($"Duo setup: emulator '{args[1]}' not found"); return 2; }
            var error = DuoSetup.Run(instance, args[1], AppConfig.Load().AndroidSdk, _ => { });
            if (error != null) Log.Write($"'{args[1]}': Duo setup failed: {error}");
            return error == null ? 0 : 1;
        }
        //   --menu-shot <emulator> <out.png> [light|dark] [scale] [recording-seconds]
        //                                      the emulator's bezel menu drawn into a PNG by the running tray app
        if (args.Length is >= 3 and <= 6 && args[0] == "--menu-shot")
        {
            double scale = args.Length > 4 && double.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 1;
            int? recording = args.Length > 5 && int.TryParse(args[5], out int seconds) ? seconds : null;
            return TrayApp.RequestMenuShotOfRunningInstance(args[1], Path.GetFullPath(args[2]), args.Length > 3 && args[3] == "dark", scale, recording) ? 0 : 1;
        }
        //   --duo-create [name]                a new iPhone Duo emulator from nothing (default name "iPhone Duo")
        if (args.Length is 1 or 2 && args[0] == "--duo-create")
        {
            // With the tray app running, it gives the new emulator the Duo frame and leaves it alone until this is done.
            bool tray = TrayApp.IsRunning;
            var error = DuoSetup.Create(args.Length == 2 ? args[1] : null, AppConfig.Load().AndroidSdk, text => Say(text), out string created,
                name => { if (tray) TrayApp.RequestDuoCreatingOfRunningInstance(name, true); });
            if (tray && created.Length > 0) TrayApp.RequestDuoCreatingOfRunningInstance(created, false);
            Say(error == null ? $"{created} is ready: an iPhone Duo, folded. Fold and unfold from its frame's pose buttons." : $"iPhone Duo not created: {error}");
            return error == null ? 0 : 1;
        }
        //   --duo-doctor [name] [--check]      check every piece of an iPhone Duo emulator and repair what it can
        if (args.Length >= 1 && args[0] == "--duo-doctor")
        {
            bool check = args.Contains("--check");
            string? who = args.Skip(1).FirstOrDefault(a => a != "--check");
            return RunDoctor(who, repair: !check);
        }
        //   --prepare                          draw the built-in frames now (the installer does, so the first start is quick)
        if (args.Length == 1 && args[0] == "--prepare") return FrameLibrary.Load(AppConfig.Load().FramesRoot).Frames.Count > 0 ? 0 : 1;
        //   --android-tools-check              exit 0 when the Android display tools (aapt2, apksigner, Java) are ready
        if (args.Length == 1 && args[0] == "--android-tools-check")
        {
            var missing = AndroidTools.Missing(AppConfig.Load().AndroidSdk);
            Say(missing.Count == 0 ? "Android display tools are ready." : $"Missing: {string.Join(", ", missing)}");
            return missing.Count == 0 ? 0 : 1;
        }
        //   --remove-overlays                  take this app's Android overlays (cutout, status bar, postures) off running emulators
        if (args.Length == 1 && args[0] == "--remove-overlays")
        {
            int count = 0;
            foreach (var instance in MuMuControl.RunningInstances())
            {
                AndroidCutout.Remove(instance);
                instance.Shell($"cmd overlay disable --user 0 {IPhoneDuo.PosturesOverlay}");
                instance.Adb("uninstall", IPhoneDuo.PosturesOverlay);
                count++;
            }
            Say($"Removed the overlays from {count} running emulator(s).");
            return 0;
        }
        //   --set-frame <name> <frame|auto>     choose an emulator's frame ("Category/Model/Variant"); with fit on, the
        //                                      emulator becomes that device
        if (args.Length == 3 && args[0] == "--set-frame")
        {
            string? key = string.Equals(args[2], "auto", StringComparison.OrdinalIgnoreCase) ? null : args[2];
            if (TrayApp.IsRunning) return TrayApp.RequestSetFrameOfRunningInstance(args[1], key) ? 0 : 1;
            var config = AppConfig.Load();
            if (key != null && FrameLibrary.Load(config.FramesRoot).Find(key) == null) return 2;
            config.Instance(args[1]).Frame = key;
            config.Save();
            return 0;
        }
        //   --duo-frames <folder>              draw the iPhone Duo frames into a folder (they are built in otherwise)
        if (args.Length == 2 && args[0] == "--duo-frames")
        {
            foreach (var pose in IPhoneDuo.Poses) DuoFrames.Write(pose, DuoFrames.Dir(Path.GetFullPath(args[1]), pose));
            return 0;
        }
        //   --record-frame [name]             record the whole visible frame with ShareX (active/last or named emulator)
        if (args.Length is 1 or 2 && args[0] == "--record-frame")
            return TrayApp.RequestFrameRecordingOfRunningInstance(args.Length == 2 ? args[1] : null) ? 0 : 1;
        if (args.Length == 1 && args[0] == "--stop-frame-recording")
        {
            try
            {
                string? shareX = ShareXIntegration.FindCompatible(AppConfig.Load().ShareXExecutable);
                if (shareX == null) return 1;
                ShareXIntegration.Stop(shareX);
                return 0;
            }
            catch (Exception ex) { Log.Write($"Could not stop ShareX recording: {ex}"); return 1; }
        }
        if (args.Length >= 1 && args[0] == "--restore") { Restore(); return RestoreAndroid(); }
        //   --render <key> <w> <h> <out.png>   frame rendered for an arbitrary picture size (no emulator needed)
        if (args.Length >= 5 && args[0] == "--render" && int.TryParse(args[2], out int w) && int.TryParse(args[3], out int h))
            return Diagnostics.RenderOnly(args[1], w, h, args[4]);
        //   --cutout-spec <key> <w> <h> <dpi>  what the Android overlays would contain for that frame and display (log.txt)
        if (args.Length >= 5 && args[0] == "--cutout-spec" && int.TryParse(args[2], out int dw) && int.TryParse(args[3], out int dh) && int.TryParse(args[4], out int dpi))
        {
            var config = AppConfig.Load();
            var frame = FrameLibrary.Load(config.FramesRoot).Find(args[1]);
            if (frame == null) return 2;
            var spec = AndroidCutout.Measure(frame, new AndroidDisplay(new Size(dw, dh), dpi), config.StatusBarPaddingFactor);
            Log.Write($"Cutout spec {frame.Key} on {dw}x{dh}@{dpi}: {spec}");
            return spec == null ? 3 : 0;
        }
        //   --screenshot <name> <out.png> [key|-] [transparent|checker|#RRGGBB]
        //                                      Android screenshot inside the frame at native resolution (pixel-perfect)
        if (args.Length >= 3 && args[0] == "--screenshot")
            return Diagnostics.Screenshot(args[1], args[2], args.Length > 3 && args[3] != "-" ? args[3] : null, args.Length > 4 ? args[4] : null);
        //   --window <title> <out.png>         the emulator window as MuMu draws it (before picture)
        if (args.Length >= 3 && args[0] == "--snapshot")
            return Diagnostics.Snapshot(args[1], args[2], args.Length > 3 && args[3] != "-" ? args[3] : null, args.Length > 4 ? args[4] : null);
        if (args.Length >= 3 && args[0] == "--window") return Diagnostics.WindowShot(args[1], args[2]);

        using var mutex = new Mutex(true, @"Local\MuMuDeviceFrames", out bool first);
        if (!first) return 0;

        // A previous run that was killed leaves its clip regions behind; start from a clean slate.
        Restore();

        TrayApp? app = null;
        Application.ThreadException += (_, e) => Log.Write($"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"Fatal: {e.ExceptionObject}");
            Restore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => app?.Shutdown();

        Log.Write("Started");
        app = new TrayApp();
        Application.Run(app);
        Log.Write("Exited");
        return 0;
    }

    private static int Restore()
    {
        var names = AppConfig.Load().ProcessNames;
        int count = 0;
        EnumWindows((hwnd, _) =>
        {
            if (FindChildByClass(hwnd, EmulatorSession.RenderClass) == IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                return true;
            }
            EmulatorClip.Remove(hwnd);
            count++;
            return true;
        }, IntPtr.Zero);
        Log.Write($"Restore: cleared clip region on {count} emulator window(s)");
        return 0;
    }

    private static int RunExpoCommand(string name, ExpoAction action)
    {
        try
        {
            var instance = MuMuControl.LocateByName(name);
            var result = instance == null
                ? new ExpoResult(false, $"Could not find emulator '{name}'.")
                : ExpoControl.Run(instance, action);
            Log.Write($"Expo {action} on '{name}': {result.Message}");
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write($"Expo {action} on '{name}' failed: {ex}");
            return 1;
        }
    }

    private static bool _console;

    /// <summary>A line for whoever ran the command: the console it came from (this is a GUI app), and the log.</summary>
    internal static void Say(string text)
    {
        if (!_console)
        {
            _console = true;
            if (!Console.IsOutputRedirected) AttachConsole(-1); // ATTACH_PARENT_PROCESS
        }
        try { Console.WriteLine(text); } catch { /* no console */ }
        Log.Write(text);
    }

    /// <summary>--duo-doctor: the named emulator, or the only running one on the iPhone Duo surface.</summary>
    private static int RunDoctor(string? name, bool repair)
    {
        var config = AppConfig.Load();
        MuMuInstance? instance = null;
        if (name != null) instance = MuMuControl.LocateByName(name);
        else
        {
            var duos = new List<(string Name, MuMuInstance Instance)>();
            foreach (var running in MuMuControl.RunningInstances())
                if (MuMuControl.QueryDisplay(running) is { } display && IPhoneDuo.IsSurface(display.Native)
                    && MuMuControl.FindManager() is { } manager && MuMuControl.List(manager).FirstOrDefault(i => i.Index == running.Index).Name is { } found)
                    duos.Add((found, running));
            if (duos.Count == 1) (name, instance) = duos[0];
            else
            {
                Say(duos.Count == 0 ? "No running iPhone Duo emulator found; name one: --duo-doctor <emulator>." : $"Several Duo emulators run ({string.Join(", ", duos.Select(d => d.Name))}); name one.");
                return 2;
            }
        }
        if (instance == null) { Say($"No running emulator named {name}."); return 2; }
        Say($"Checking {name}{(repair ? " (and repairing)" : "")}…");
        var findings = DuoDoctor.Run(instance, name!, config.Instance(name!).DuoPose, config.AndroidSdk, repair, config.Instance(name!));
        if (repair) config.Save();
        foreach (var finding in findings) Say(finding.ToString());
        File.WriteAllLines(Path.Combine(AppConfig.Dir, "duo-doctor.txt"), findings.Select(f => f.ToString()));
        return findings.Any(f => f.Outcome == DuoDoctor.Outcome.Failed) ? 1 : 0;
    }

    /// <summary>Fold or unfold without the tray app: the same wm commands, straight over adb.</summary>
    private static int RunDuoCommand(string? name, DuoPose pose)
    {
        try
        {
            if (TrayApp.IsRunning) return TrayApp.RequestDuoPoseOfRunningInstance(pose, name) ? 0 : 1;
            // The tray claims its mutex before loading frames and creating its message window. Do not race its
            // startup with a direct adb transition or a config write just because that window is not ready yet.
            if (Mutex.TryOpenExisting(@"Local\MuMuDeviceFrames", out var starting))
            {
                using (starting)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!TrayApp.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(100);
                    return TrayApp.IsRunning && TrayApp.RequestDuoPoseOfRunningInstance(pose, name) ? 0 : 1;
                }
            }
            if (name == null) return 1;
            var instance = MuMuControl.LocateByName(name);
            using var operation = instance?.ExclusiveOperation();
            var display = instance == null ? null : MuMuControl.QueryDisplay(instance);
            if (instance == null || display == null) { Log.Write($"iPhone Duo {pose}: emulator '{name}' not found"); return 1; }
            if (!IPhoneDuo.IsSurface(display.Native)) { Log.Write($"iPhone Duo {pose}: '{name}' is {display.Native.Width}x{display.Native.Height}, not set up as an iPhone Duo"); return 1; }
            var config = AppConfig.Load();
            var settings = config.Instance(name);
            bool refresh = settings.DuoOverlay != null;
            if (refresh && settings.DuoProtectSafeAreas && DuoSetup.EnsureSafeAreaSupport(instance) is { } error)
            { Log.Write(error); return 1; }
            instance.Shell(string.Join("; ", IPhoneDuo.SwitchCommands(pose, display.Native, display.Duo?.Foldable == true, refresh, settings.DuoProtectSafeAreas)));
            var now = MuMuControl.WaitForPose(instance, pose);
            var reached = IPhoneDuo.Detect(now);
            Log.Write($"iPhone Duo {pose} on '{name}' (adb): now {reached?.ToString() ?? "no pose"}");
            if (reached != pose || !IPhoneDuo.InStep(pose, now)) return 1;
            settings.DuoPose = pose;
            config.Save();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"iPhone Duo {pose} on '{name}' failed: {ex}");
            return 1;
        }
    }

    /// <summary>Brings back the Android status bar on every running instance whose bar this tool hid (after a crash/kill).</summary>
    private static int RestoreAndroid()
    {
        int count = 0;
        foreach (var instance in MuMuControl.RunningInstances())
        {
            if (!AndroidStatusBar.IsHidden(instance)) continue;
            AndroidStatusBar.Restore(instance);
            count++;
        }
        Log.Write($"Restore: Android status bar restored on {count} emulator(s)");
        return 0;
    }
}
