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
