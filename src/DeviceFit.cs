using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

/// <summary>
/// Makes an emulator a frame's device (<see cref="DeviceSpecs"/>). A different density alone is applied live
/// (<c>wm density</c>, and MuMu's setting so it holds after restarts). A different resolution needs MuMu's own setting
/// and a restart: the app in front is reopened afterwards. Leftovers of an iPhone Duo pose are cleared first, since
/// <c>wm size</c>, rotation lock and the fold line would otherwise apply to the new panel.
/// </summary>
internal static class DeviceFit
{
    /// <summary>What has to happen for the emulator to be the device: nothing, a live density change, or a restart.</summary>
    public enum Step { None, Density, Restart }

    public static Step Needed(DeviceSpec spec, AndroidDisplay display)
    {
        int density = display.DensityOverride ?? display.Density;
        bool panel = (display.Native == spec.Native || display.Native == new Size(spec.Native.Height, spec.Native.Width)) && display.Override == null;
        return !panel ? Step.Restart : density != spec.Density ? Step.Density : Step.None;
    }

    private const string ClearDuo = "wm size reset; wm density reset; wm fixed-to-user-rotation default; wm user-rotation free; " +
                                    "settings delete global display_features; cmd device_state state reset";

    public static string? ApplyDensity(MuMuInstance instance, DeviceSpec spec, AndroidDisplay display)
    {
        MuMuControl.SetResolution(instance, display.Native, spec.Density); // the next start keeps it
        instance.Shell($"wm density {spec.Density}");
        var now = MuMuControl.QueryDisplay(instance);
        return now != null && (now.DensityOverride ?? now.Density) == spec.Density ? null : "wm density did not take.";
    }

    public static string? Restart(MuMuInstance instance, DeviceSpec spec)
    {
        string? app = ForegroundApp(instance);
        instance.Shell(ClearDuo);
        MuMuControl.SetResolution(instance, spec.Native, spec.Density);
        MuMuControl.Restart(instance);
        Thread.Sleep(6000); // the old Android answers "booted" until it has gone down
        if (!MuMuControl.WaitForBoot(instance, TimeSpan.FromMinutes(3))) return "The emulator did not come back after the restart.";
        var display = MuMuControl.QueryDisplay(instance);
        if (display == null || Needed(spec, display) != Step.None)
            return $"MuMu is at {display?.Native.Width}x{display?.Native.Height} @ {display?.Density} dpi after the restart.";
        if (app != null) instance.Shell($"monkey -p {app} -c android.intent.category.LAUNCHER 1");
        return null;
    }

    /// <summary>The package of the app in front, unless it is the launcher (nothing to put back then).</summary>
    public static string? ForegroundApp(MuMuInstance instance)
    {
        string output = instance.Shell("dumpsys activity activities | grep -m1 topResumedActivity; " +
                                       "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tail -n 1").Output;
        return ParseForeground(output);
    }

    internal static string? ParseForeground(string output)
    {
        var top = Regex.Match(output, @"topResumedActivity=ActivityRecord\{\S+ u\d+ ([\w.]+)/");
        if (!top.Success) return null;
        string package = top.Groups[1].Value;
        var home = Regex.Match(output, @"^([\w.]+)/", RegexOptions.Multiline);
        return home.Success && home.Groups[1].Value == package ? null : package;
    }
}
