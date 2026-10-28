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

    /// <summary>
    /// When ShareX's screen recording started, or null when it is not recording. ShareX records through ffmpeg, so a
    /// recording is an ffmpeg process whose parent is ShareX; its start time is when the recording began.
    /// </summary>
    public static DateTime? RecordingSince()
    {
        var shareX = new HashSet<uint>();
        var ffmpeg = new List<(uint Pid, uint Parent)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (string.Equals(entry.szExeFile, "ShareX.exe", StringComparison.OrdinalIgnoreCase)) shareX.Add(entry.th32ProcessID);
                else if (string.Equals(entry.szExeFile, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) ffmpeg.Add((entry.th32ProcessID, entry.th32ParentProcessID));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        foreach (var (pid, parent) in ffmpeg)
        {
            if (!shareX.Contains(parent)) continue;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.StartTime;
            }
            catch
            {
                // It ended just now.
            }
        }
        return null;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void Stop(string shareXPath)
    {
        var start = new ProcessStartInfo(shareXPath) { UseShellExecute = false };
        start.ArgumentList.Add("-silent");
        start.ArgumentList.Add("-DeviceFrameStop");
        using Process? process = Process.Start(start);
        if (process == null) throw new InvalidOperationException("ShareX did not start.");
    }
}
