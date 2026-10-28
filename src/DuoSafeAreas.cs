namespace MuMuDeviceFrames;

/// <summary>
/// A repair tried a few times with growing gaps, then given up on. <c>waits[i]</c> is how long to wait after try
/// <c>i</c> before the next one, or, after the last, before giving up: one try per wait. <see cref="Reset"/> once the
/// thing is right again (or something else changed).
/// </summary>
internal sealed class Backoff(params TimeSpan[] waits)
{
    private int _tries;
    private DateTime _last;

    /// <summary>Tries since the last <see cref="Reset"/>.</summary>
    public int Tries => _tries;

    /// <summary>Every try was made and the wait after the last one has passed.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>True (and counted) when a try is due now; turns <see cref="GaveUp"/> on once the last wait is over.</summary>
    public bool TryNow(DateTime now)
    {
        if (GaveUp) return false;
        if (_tries > 0 && now - _last < waits[_tries - 1]) return false;
        if (_tries == waits.Length)
        {
            GaveUp = true;
            return false;
        }
        _tries++;
        _last = now;
        return true;
    }

    public void Reset()
    {
        _tries = 0;
        GaveUp = false;
    }
}

/// <summary>
/// The iPhone Duo's safe areas, in pixels on the displays as held: the rail poses (closed, open, book, tent) have the
/// rail on the right and the home indicator at the bottom; the open display upright (open portrait, tabletop) has the
/// corner status element at the top and the home indicator at the bottom. As iOS reports them: 84 pt rail (Mail's
/// content ends there in Apple's renders), 68 pt top (the corner element ends 63.4 pt down, plus 4.6 pt), 34 pt home
/// indicator.
/// </summary>
internal sealed record DuoSafeAreas(int Rail, int UprightTop, int Home, bool Turned)
{
    public static DuoSafeAreas For(int density, Size physical)
    {
        int Px(double pt) => (int)Math.Round(pt * density / 160.0);
        return new DuoSafeAreas(Px(IosChrome.RailPt), Px(IosChrome.UprightTopPt), Px(IosChrome.HomeIndicatorPt), physical.Width < physical.Height);
    }

    /// <summary>The safe areas apps get in a pose, as Android reports the cutout (display orientation, pixels).</summary>
    public (int Left, int Top, int Right, int Bottom) Insets(DuoPose pose) => IPhoneDuo.IsUpright(pose) ? (0, 0, 0, Home) : (0, 0, Rail, Home);

    /// <summary>Tent's waterfall: the rail on the right and the home indicator at the bottom, at tent's rotation.</summary>
    public (int Left, int Top, int Right, int Bottom) TentNatural =>
        IPhoneDuo.ToPanel((0, 0, Rail, Home), IPhoneDuo.RotationFor(DuoPose.Tent, Turned));

    /// <summary>The rail poses' waterfall on the panel's edges (they run at <see cref="IPhoneDuo.RotationFor"/>).</summary>
    public (int Left, int Top, int Right, int Bottom) RailNatural =>
        IPhoneDuo.ToPanel((0, 0, Rail, Home), IPhoneDuo.RotationFor(DuoPose.Open, Turned));

    /// <summary>
    /// The upright poses' waterfall on the panel's edges: the home indicator only. Their top comes from the status bar
    /// height instead (<see cref="UprightTop"/>): SystemUI sizes the status bar at the rotation change, with whatever
    /// cutout Android holds at that moment, which is still the rail poses' set until the safe areas are refreshed. At
    /// rotation 90 the rail set lies on the panel's bottom and left edges, which never show at the top upright.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom) UprightNatural =>
        IPhoneDuo.ToPanel((0, 0, 0, Home), IPhoneDuo.RotationFor(DuoPose.OpenPortrait, Turned));

    public override string ToString() => $"{Rail}|{UprightTop}|{Home}|{(Turned ? "turned" : "landscape")}";
}
