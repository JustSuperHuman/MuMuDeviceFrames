using Microsoft.Win32;

namespace MuMuDeviceFrames;

/// <summary>"Start with Windows" through the per-user Run key: no admin rights, no scheduled task, easy to undo.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MuMuDeviceFrames";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string Command => $"\"{ExePath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write(enabled ? $"Start with Windows: on ({Command})" : "Start with Windows: off");
    }

    /// <summary>If the entry points at an older copy of the exe (moved or updated elsewhere), point it at this one.</summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command);
                Log.Write($"Start with Windows: now {Command} (was {current})");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Start with Windows refresh failed: {ex.Message}");
        }
    }
}
