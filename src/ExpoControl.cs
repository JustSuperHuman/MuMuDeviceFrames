using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MuMuDeviceFrames;

internal enum ExpoAction { Reload, ToggleGear }

internal sealed record ExpoResult(bool Success, string Message);

/// <summary>Controls the foreground Expo development build in one MuMu Android guest.</summary>
internal static class ExpoControl
{
    private static readonly Regex FocusPackage = new(@"mCurrentFocus=Window\{[^\r\n]*?\su\d+\s+(?<package>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)/", RegexOptions.Compiled);
    private static readonly Regex Bounds = new(@"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex FabPreference = new("<boolean name=\"showFab\" value=\"(true|false)\"", RegexOptions.Compiled);

    public static ExpoResult Run(MuMuInstance instance, ExpoAction action)
    {
        var package = ForegroundExpoPackage(instance);
        if (package == null) return new(false, "No Expo development app is in front on this emulator.");
        return action == ExpoAction.Reload ? Reload(instance, package) : ToggleGear(instance, package);
    }

    private static string? ForegroundExpoPackage(MuMuInstance instance)
    {
        var focus = instance.Shell("dumpsys window displays");
        if (focus.ExitCode != 0) return null;
        var match = FocusPackage.Match(focus.Output);
        if (!match.Success) return null;
        string package = match.Groups["package"].Value;
        if (package == "host.exp.exponent") return package; // Expo Go

        var installed = instance.Shell($"dumpsys package {package}");
        return installed.ExitCode == 0 && installed.Output.Contains("expo.modules.devlauncher", StringComparison.OrdinalIgnoreCase)
            ? package : null;
    }

    private static ExpoResult Reload(MuMuInstance instance, string package)
    {
        // React Native registers this broadcast receiver only while development support is active.
        var result = instance.Shell($"am broadcast -a {package}.RELOAD_APP_ACTION -p {package}");
        return result.ExitCode == 0 && result.Output.Contains("Broadcast completed", StringComparison.OrdinalIgnoreCase)
            ? new(true, "Expo reload sent.")
            : new(false, $"Expo reload failed: {result.Output.Trim()}");
    }

    private static ExpoResult ToggleGear(MuMuInstance instance, string package)
    {
        bool menuOpen = false;
        try
        {
            XDocument? hierarchy = null;
            // The app itself may never become idle enough for uiautomator. Expo's menu is a stable screen to inspect.
            // If the menu was already open, the first MENU key closes it and the next one opens it again.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var open = instance.Shell("input keyevent 82"); // Android MENU key opens Expo's developer menu.
                if (open.ExitCode != 0) return new(false, "Could not open the Expo developer menu.");
                Thread.Sleep(700);
                hierarchy = DumpUi(instance);
                if (hierarchy != null && MenuIsOpen(hierarchy))
                {
                    menuOpen = true;
                    break;
                }
            }
            if (!menuOpen || hierarchy == null) return new(false, "The Expo developer menu did not open.");

            bool? before = ReadGearPreference(instance, package);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var viewport = GetBounds(hierarchy.Descendants("node").FirstOrDefault());
                if (viewport == null) return new(false, "Could not read the Android screen size.");
                var row = hierarchy.Descendants("node")
                    .FirstOrDefault(n => (string?)n.Attribute("text") == "Tools button")?
                    .Ancestors("node").FirstOrDefault(n => (string?)n.Attribute("clickable") == "true");
                var area = GetBounds(row);
                var scroll = GetBounds(row?.Ancestors("node").FirstOrDefault(n => (string?)n.Attribute("class") == "android.widget.ScrollView"));
                if (area is { } box && scroll is { } visible &&
                    box.Left >= visible.Left && box.Top >= visible.Top && box.Right <= visible.Right && box.Bottom <= visible.Bottom &&
                    box.Left >= 0 && box.Top >= 0 && box.Right <= viewport.Value.Right && box.Bottom <= viewport.Value.Bottom)
                {
                    var tap = instance.Shell($"input tap {(box.Left + box.Right) / 2} {(box.Top + box.Bottom) / 2}");
                    if (tap.ExitCode != 0) return new(false, "Could not change the Expo tools button.");
                    for (int check = 0; check < 4; check++)
                    {
                        Thread.Sleep(150);
                        bool? after = ReadGearPreference(instance, package);
                        if (after == null) break; // Expo Go may not allow run-as; the tap still succeeded.
                        if (after != before) return new(true, after.Value ? "Expo gear shown." : "Expo gear hidden.");
                    }
                    return before == null
                        ? new(true, "Expo gear toggled.")
                        : new(false, "The Expo tools button did not change. Try again.");
                }

                int width = viewport.Value.Right - viewport.Value.Left, height = viewport.Value.Bottom - viewport.Value.Top;
                var swipe = instance.Shell($"input swipe {width / 2} {height * 82 / 100} {width / 2} {height * 50 / 100} 300");
                if (swipe.ExitCode != 0) return new(false, "Could not scroll the Expo developer menu.");
                Thread.Sleep(350);
                hierarchy = DumpUi(instance);
                if (hierarchy == null || !MenuIsOpen(hierarchy)) return new(false, "Could not find the Expo tools button.");
            }
            return new(false, "Could not find the Expo tools button in the developer menu.");
        }
        finally
        {
            if (menuOpen) instance.Shell("input keyevent 82");
        }
    }

    private static bool MenuIsOpen(XDocument hierarchy) =>
        hierarchy.Descendants("node").Any(n => (string?)n.Attribute("text") == "Reload") &&
        hierarchy.Descendants("node").Any(n => ((string?)n.Attribute("text"))?.StartsWith("Runtime version:", StringComparison.Ordinal) == true);

    private static (int Left, int Top, int Right, int Bottom)? GetBounds(XElement? node)
    {
        var match = Bounds.Match((string?)node?.Attribute("bounds") ?? "");
        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value))
            : null;
    }

    private static XDocument? DumpUi(MuMuInstance instance)
    {
        string path = $"/sdcard/mumuframes-{Guid.NewGuid():N}.xml";
        try
        {
            // MuMu can exit uiautomator with code 139 after writing a valid hierarchy file.
            instance.Shell($"uiautomator dump {path}");
            var xml = instance.Shell($"cat {path}");
            return xml.ExitCode == 0 ? XDocument.Parse(xml.Output) : null;
        }
        catch (Exception ex)
        {
            Log.Write($"Expo UI inspection failed: {ex.Message}");
            return null;
        }
        finally { instance.Shell($"rm -f {path}"); }
    }

    private static bool? ReadGearPreference(MuMuInstance instance, string package)
    {
        var result = instance.Shell($"run-as {package} cat shared_prefs/expo.modules.devmenu.sharedpreferences.xml");
        if (result.ExitCode != 0) return null;
        var match = FabPreference.Match(result.Output);
        return match.Success ? match.Groups[1].Value == "true" : null;
    }
}
