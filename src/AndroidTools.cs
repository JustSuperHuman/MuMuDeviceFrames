using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

/// <summary>Dependency checks and the interactive, opt-in WinGet setup shared by all emulators.</summary>
internal static class AndroidTools
{
    private static string? _javaHome;

    public static void Refresh() => _javaHome = null;

    public static string? JavaHome()
    {
        if (_javaHome != null && File.Exists(Path.Combine(_javaHome, "bin", "java.exe"))) return _javaHome;
        foreach (string home in JavaCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string java = Path.Combine(home, "bin", "java.exe");
            if (!File.Exists(java)) continue;
            try
            {
                var (exit, output) = ProcessRunner.Run(java, ["-version"], 5000);
                if (exit == 0 && JavaMajor(output) >= 17) return _javaHome = home;
            }
            catch (Exception ex) { Log.Write($"Java check failed: {ex.Message}"); }
        }
        return null;
    }

    internal static int JavaMajor(string output)
    {
        var match = Regex.Match(output, "version\\s+\"(?<major>\\d+)(?:\\.(?<minor>\\d+))?");
        if (!match.Success) return 0;
        int major = int.Parse(match.Groups["major"].Value);
        return major == 1 && match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : major;
    }

    private static IEnumerable<string> JavaCandidates()
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            if (Environment.GetEnvironmentVariable("JAVA_HOME", target) is { Length: > 0 } home) yield return home.Trim('"');
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(dir)) yield return Path.GetFullPath(Path.Combine(dir.Trim('"'), ".."));
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(local, "Programs", "Eclipse Adoptium"), Path.Combine(local, "Programs", "Java"),
            Path.Combine(local, "Programs", "MuMuDeviceFramesJava"),
        })
            if (Directory.Exists(root))
                foreach (string home in Directory.GetDirectories(root).OrderDescending()) yield return home;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr");
    }

    public static IReadOnlyList<string> Missing(string? sdkRoot)
    {
        var missing = new List<string>();
        foreach (string file in new[] { "aapt2.exe", "zipalign.exe", "apksigner.bat", "lib/apksigner.jar" })
            if (AndroidSdk.Newest("build-tools", file, sdkRoot) == null) missing.Add(file);
        if (AndroidSdk.Newest("platforms", "android.jar", sdkRoot) == null) missing.Add("Android SDK platform");
        if (JavaHome() == null) missing.Add("Java 17 or newer");
        return missing;
    }

    public static async Task<int> InstallAsync(string sdkRoot)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "tools", "install-android-tools.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Android tools setup script is missing. Reinstall MuMu Device Frames.", script);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            // This console is intentional: the user sees progress and can answer package/license prompts or close it to cancel.
            CreateNoWindow = false,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-SdkRoot", sdkRoot, "-LogPath", Path.Combine(AppConfig.Dir, "android-tools-install.log") })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Android tools setup.");
        await process.WaitForExitAsync();
        Refresh();
        return process.ExitCode;
    }
}
