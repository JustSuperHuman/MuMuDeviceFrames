namespace MuMuDeviceFrames;

/// <summary>
/// Empties Android's status bar so an iOS-style one can be drawn over it. SystemUI's icon blocklist (the System UI
/// Tuner setting) hides the clock and every system icon; notification icons are switched off by the SystemUI overlay
/// (<see cref="AndroidCutout"/>). Plain shell settings (no root), undone by <see cref="Restore"/>.
/// Demo mode is deliberately not used: its fake network model draws a satellite icon the blocklist can't hide.
/// </summary>
internal static class AndroidStatusBar
{
    private const string Blocklist = "clock,wifi,mobile,battery,bluetooth,alarm_clock,rotate,headset,cast,hotspot,vpn,ethernet,airplane,volume,zen," +
                                     "managed_profile,data_saver,location,nfc,mute,speakerphone,call_strength,sensors_off,screen_record,camera,microphone," +
                                     "ime,sync_failing,sync_active,tty,cdma_eri,secure,phone_evdo_signal,phone_signal,data_connection,satellite,connected_display";

    public static void Hide(MuMuInstance instance)
    {
        instance.Shell($"settings put secure icon_blacklist {Blocklist}");
    }

    public static void Restore(MuMuInstance instance)
    {
        instance.Shell("settings delete secure icon_blacklist");
    }

    public static bool IsHidden(MuMuInstance instance) => instance.Shell("settings get secure icon_blacklist").Output.Contains("clock");

    /// <summary>True when the foreground app asks for a light status bar, i.e. dark glyphs on a light background.</summary>
    public static bool WantsDarkGlyphs(MuMuInstance instance) =>
        instance.Shell("dumpsys window displays | sed -n '/mLastStatusBarAppearanceRegions/,+1p'").Output.Contains("LIGHT_STATUS_BARS");
}
