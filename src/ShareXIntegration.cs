using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace MuMuDeviceFrames;

internal static class ShareXIntegration
{
    // Set by the companion ShareX build in ShareX.csproj. An ordinary ShareX 21 build does not
    // understand DeviceFrameRecord, even when its numerical version is the same.
    private const string ProtocolMarker = "deviceframe1";

    public static string? FindCompatible(string? configuredPath)
    {
        // A running ShareX receives commands from every new instance. If it is too old, a newer
        // executable on disk cannot safely receive this command until ShareX is restarted.
        Process[] running = Process.GetProcessesByName("ShareX");
        try
        {
            if (running.Length > 0)
            {
                foreach (Process process in running)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (IsCompatible(path)) return path;
                    }
                    catch (Exception ex) { Log.Write($"Could not inspect running ShareX: {ex.Message}"); }
                }
                return null;
            }
        }
        finally
        {
            foreach (Process process in running) process.Dispose();
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string? path in new[]
        {
            configuredPath,
            Path.Combine(local, "Programs", "ShareX", "ShareX.exe"),
            Path.Combine(programs, "ShareX", "ShareX.exe"),
            Path.Combine(programsX86, "ShareX", "ShareX.exe")
        })
            if (IsCompatible(path)) return path;

        return null;
    }

    private static bool IsCompatible(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                FileVersionInfo.GetVersionInfo(path).ProductVersion?.Contains(ProtocolMarker, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    public static void Record(string shareXPath, Rectangle bounds)
    {
        var start = new ProcessStartInfo(shareXPath) { UseShellExecute = false };
        start.ArgumentList.Add("-silent");
        start.ArgumentList.Add("-DeviceFrameRecord");
        start.ArgumentList.Add($"rect={bounds.X.ToString(CultureInfo.InvariantCulture)},{bounds.Y.ToString(CultureInfo.InvariantCulture)},{bounds.Width.ToString(CultureInfo.InvariantCulture)},{bounds.Height.ToString(CultureInfo.InvariantCulture)}");
        using Process? process = Process.Start(start);
        if (process == null) throw new InvalidOperationException("ShareX did not start.");
    }

    public static void Stop(string shareXPath)
    {
        var start = new ProcessStartInfo(shareXPath) { UseShellExecute = false };
        start.ArgumentList.Add("-silent");
        start.ArgumentList.Add("-DeviceFrameStop");
        using Process? process = Process.Start(start);
        if (process == null) throw new InvalidOperationException("ShareX did not start.");
    }
}
