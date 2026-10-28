using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

internal static class ProcessRunner
{
    /// <summary>Runs a console program hidden and returns its exit code with stdout+stderr combined.</summary>
    public static (int ExitCode, string Output) Run(string file, IEnumerable<string> arguments, int timeoutMs = 90_000)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Run(info, timeoutMs);
    }

    /// <summary>Batch files (apksigner.bat) have to go through cmd.exe, which needs the whole command line pre-quoted.</summary>
    public static (int ExitCode, string Output) RunBatch(string batchFile, IEnumerable<string> arguments, int timeoutMs = 90_000)
    {
        var line = new StringBuilder($"\"{batchFile}\"");
        foreach (var argument in arguments) line.Append(" \"").Append(argument).Append('"');
        var info = new ProcessStartInfo("cmd.exe", $"/s /c \"{line}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (AndroidTools.JavaHome() is { } javaHome) info.Environment["JAVA_HOME"] = javaHome;
        return Run(info, timeoutMs);
    }

    /// <summary>Raw stdout, for binary output such as <c>adb exec-out screencap -p</c>.</summary>
    public static (int ExitCode, byte[] Output) RunBytes(string file, IEnumerable<string> arguments, int timeoutMs = 60_000)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, []);
        }
        copy.Wait(5_000);
        return (process.ExitCode, buffer.ToArray());
    }

    private static (int, string) Run(ProcessStartInfo info, int timeoutMs)
    {
        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, output + "\n(timed out)");
        }
        process.WaitForExit(); // flush the async readers
        lock (output) return (process.ExitCode, output.ToString());
    }
}

/// <summary>How to reach one emulator from outside: its MuMu instance index and its adb endpoint.</summary>
internal sealed record MuMuInstance(string Index, string ManagerPath, string AdbPath, string AdbSerial)
{
    public (int ExitCode, string Output) Adb(params string[] arguments) => ProcessRunner.Run(AdbPath, ["-s", AdbSerial, .. arguments]);

    public (int ExitCode, string Output) Shell(string command) => Adb("shell", command);

    /// <summary>A PNG of the Android display at native resolution, straight from the guest; null if it failed.</summary>
    public byte[]? Screencap()
    {
        var (exit, bytes) = ProcessRunner.RunBytes(AdbPath, ["-s", AdbSerial, "exec-out", "screencap", "-p"]);
        // PNG signature check: on failure adb prints text instead.
        return exit == 0 && bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' ? bytes : null;
    }
}

/// <param name="Override">The <c>wm size</c> override, when one is set (the iPhone Duo poses use it).</param>
/// <param name="Duo">Rotation, device state and postures overlay, asked only on the iPhone Duo's surface.</param>
/// <param name="DensityOverride">The <c>wm density</c> override, when one is set.</param>
internal sealed record AndroidDisplay(Size Native, int Density, Size? Override = null, DuoDevice? Duo = null, int? DensityOverride = null);

internal static class MuMuControl
{
    /// <summary>The instance behind an emulator process.</summary>
    public static MuMuInstance? Locate(uint pid) =>
        Connect(Enumerate(pid).FirstOrDefault(e => e.Pid == pid).Instance);

    /// <summary>The instance with the given MuMu name (window title), whether or not its window is visible.</summary>
    public static MuMuInstance? LocateByName(string name)
    {
        uint any = AnyEmulatorPid();
        return any == 0 ? null : Connect(Enumerate(any).FirstOrDefault(e => e.Name == name).Instance);
    }

    /// <summary>Every instance whose Android is up.</summary>
    public static List<MuMuInstance> RunningInstances()
    {
        uint any = AnyEmulatorPid();
        return any == 0 ? [] : Enumerate(any).Where(e => e.AndroidStarted).Select(e => Connect(e.Instance)!).ToList();
    }

    private static uint AnyEmulatorPid()
    {
        var names = AppConfig.Load().ProcessNames;
        foreach (var p in Process.GetProcesses())
        {
            using (p) if (names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) return (uint)p.Id;
        }
        return 0;
    }

    private static MuMuInstance? Connect(MuMuInstance? instance)
    {
        if (instance != null) ProcessRunner.Run(instance.AdbPath, ["connect", instance.AdbSerial], 15_000);
        return instance;
    }

    private readonly record struct Entry(string Name, uint Pid, bool AndroidStarted, MuMuInstance Instance);

    /// <summary>Finds MuMuManager next to the emulator's executable and asks it about all instances that have an adb port.</summary>
    private static List<Entry> Enumerate(uint anyEmulatorPid)
    {
        var entries = new List<Entry>();
        string exe;
        try
        {
            using var process = Process.GetProcessById((int)anyEmulatorPid);
            exe = process.MainModule?.FileName ?? "";
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot inspect emulator process {anyEmulatorPid}: {ex.Message}");
            return entries;
        }

        string? manager = null;
        for (var dir = Path.GetDirectoryName(exe); dir != null && manager == null; dir = Path.GetDirectoryName(dir))
            manager = new[] { @"nx_main\MuMuManager.exe", @"shell\MuMuManager.exe", "MuMuManager.exe" }.Select(rel => Path.Combine(dir, rel)).FirstOrDefault(File.Exists);
        if (manager == null)
        {
            Log.Write($"MuMuManager.exe not found near {exe}");
            return entries;
        }

        string adb = Path.Combine(Path.GetDirectoryName(exe)!, "adb.exe");
        if (!File.Exists(adb)) adb = AndroidSdk.Find("platform-tools", "adb.exe") ?? "adb";

        // MuMuManager's exit code is not meaningful (255 even on success); the JSON is what counts.
        var (_, output) = ProcessRunner.Run(manager, ["info", "-v", "all"]);
        try
        {
            int start = output.IndexOf('{'), end = output.LastIndexOf('}');
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            // With a single instance the object is flat; with several it is keyed by index.
            var candidates = doc.RootElement.TryGetProperty("index", out _) ? [doc.RootElement] : doc.RootElement.EnumerateObject().Select(p => p.Value).ToArray();
            foreach (var instance in candidates)
            {
                if (!instance.TryGetProperty("adb_port", out var port)) continue;
                string host = instance.TryGetProperty("adb_host_ip", out var ip) ? ip.GetString() ?? "127.0.0.1" : "127.0.0.1";
                string index = instance.GetProperty("index").GetString() ?? "0";
                entries.Add(new Entry(
                    instance.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    instance.TryGetProperty("pid", out var p) ? p.GetUInt32() : 0,
                    instance.TryGetProperty("is_android_started", out var up) && up.GetBoolean(),
                    new MuMuInstance(index, manager, adb, $"{host}:{port.GetInt32()}")));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not parse MuMuManager info: {ex.Message}");
        }
        return entries;
    }

    public static AndroidDisplay? QueryDisplay(MuMuInstance instance)
    {
        string wmSize = instance.Shell("wm size").Output;
        var size = Regex.Match(wmSize, @"Physical size:\s*(\d+)x(\d+)");
        string wmDensity = instance.Shell("wm density").Output;
        var density = Regex.Match(wmDensity, @"Physical density:\s*(\d+)");
        var densityOverride = Regex.Match(wmDensity, @"Override density:\s*(\d+)");
        if (!size.Success || !density.Success) return null;
        var physical = new Size(int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value));
        var duo = IPhoneDuo.IsSurface(physical) ? IPhoneDuo.ParseDevice(instance.Shell(IPhoneDuo.DeviceQuery).Output) : null;
        return new AndroidDisplay(physical, int.Parse(density.Groups[1].Value), IPhoneDuo.ParseSize(wmSize, "Override size"), duo,
            densityOverride.Success ? int.Parse(densityOverride.Groups[1].Value) : null);
    }

    /// <summary>Writes a custom resolution into the instance's MuMu settings. Takes effect on the next emulator start.</summary>
    public static string SetResolution(MuMuInstance instance, Size size, int dpi) =>
        ProcessRunner.Run(instance.ManagerPath,
        [
            "setting", "-v", instance.Index,
            "-k", "resolution_mode", "-val", "custom",
            "-k", "resolution_width.custom", "-val", size.Width.ToString(),
            "-k", "resolution_height.custom", "-val", size.Height.ToString(),
            "-k", "resolution_dpi.custom", "-val", dpi.ToString(),
        ]).Output.Trim();

    // Goes home, then force-stops each running user-installed app except the launcher and the keyboard.
    private const string CloseAppsScript =
        "input keyevent 3; " +
        "home=$(cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tail -n 1 | cut -d/ -f1); " +
        "ime=$(settings get secure default_input_method | cut -d/ -f1); " +
        "for p in $(pm list packages -3 | cut -d: -f2); do " +
        "[ \"$p\" = \"$home\" ] || [ \"$p\" = \"$ime\" ] && continue; " +
        "pidof $p >/dev/null && am force-stop $p && echo \"closed $p\"; " +
        "done; exit 0";

    /// <summary>Closes the apps running in the emulator, as "clear all" in Recents would. Returns the packages it stopped.</summary>
    public static List<string> CloseApps(MuMuInstance instance)
    {
        var result = instance.Shell(CloseAppsScript);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output.Trim());
        return result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("closed ", StringComparison.Ordinal))
            .Select(line => line["closed ".Length..])
            .ToList();
    }

    public static string Restart(MuMuInstance instance) =>
        ProcessRunner.Run(instance.ManagerPath, ["control", "-v", instance.Index, "restart"], 180_000).Output.Trim();

    /// <summary>One of the instance's MuMu settings (<c>MuMuManager setting -k</c>), null if MuMu did not report it.</summary>
    public static string? GetSetting(MuMuInstance instance, string key)
    {
        var output = ProcessRunner.Run(instance.ManagerPath, ["setting", "-v", instance.Index, "-k", key]).Output;
        var match = Regex.Match(output, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Writes MuMu settings; most take effect on the next start of the instance.</summary>
    public static string SetSettings(MuMuInstance instance, params (string Key, string Value)[] settings) =>
        ProcessRunner.Run(instance.ManagerPath, ["setting", "-v", instance.Index, .. settings.SelectMany(s => new[] { "-k", s.Key, "-val", s.Value })]).Output.Trim();

    /// <summary>adb drops the connection when adbd or the emulator restarts.</summary>
    public static void Reconnect(MuMuInstance instance) => ProcessRunner.Run(instance.AdbPath, ["connect", instance.AdbSerial], 15_000);

    /// <summary>Waits until Android has finished booting (after a restart), reconnecting adb as it goes.</summary>
    public static bool WaitForBoot(MuMuInstance instance, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Reconnect(instance);
            if (instance.Shell("getprop sys.boot_completed").Output.Trim() == "1") return true;
            Thread.Sleep(2000);
        }
        return false;
    }
}

internal static class AndroidSdk
{
    public static string? Root(string? configured = null) =>
        !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) :
        Roots().FirstOrDefault(HasBuildTools) ?? Roots().FirstOrDefault(Directory.Exists);

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");

    private static IEnumerable<string> Roots() => new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT", EnvironmentVariableTarget.User), DefaultRoot,
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool HasBuildTools(string root) => Directory.Exists(Path.Combine(root, "build-tools")) &&
        Directory.GetDirectories(Path.Combine(root, "build-tools")).Any(d =>
            new[] { "aapt2.exe", "zipalign.exe", "apksigner.bat", "lib/apksigner.jar" }.All(f => File.Exists(Path.Combine(d, f)))) &&
        Directory.Exists(Path.Combine(root, "platforms")) &&
        Directory.GetDirectories(Path.Combine(root, "platforms")).Any(d => File.Exists(Path.Combine(d, "android.jar")));

    public static string? Find(string folder, string file, string? configured = null)
    {
        var root = Root(configured);
        if (root == null) return null;
        var path = Path.Combine(root, folder, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Newest versioned sub-folder (build-tools\36.1.0, platforms\android-36) that contains the given file.</summary>
    public static string? Newest(string folder, string file, string? configured = null)
    {
        var root = Root(configured);
        if (root == null || !Directory.Exists(Path.Combine(root, folder))) return null;
        return Directory.GetDirectories(Path.Combine(root, folder))
            .Where(d => File.Exists(Path.Combine(d, file)))
            .OrderByDescending(d => d, Comparer<string>.Create((a, b) => Native.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b))))
            .Select(d => Path.Combine(d, file))
            .FirstOrDefault();
    }
}
