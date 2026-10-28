namespace MuMuDeviceFrames;

/// <summary>What an emulator must be to be a frame's device: native pixels, and the density that gives apps the real
/// device's size in points / dp. <paramref name="Known"/> is false for the category rule of thumb.</summary>
internal sealed record DeviceSpec(Size Native, int Density, bool Known)
{
    /// <summary>The size apps see, in dp (Android) or pt (iOS): pixels × 160 / density.</summary>
    public Size Logical => new((int)Math.Round(Native.Width * 160.0 / Density), (int)Math.Round(Native.Height * 160.0 / Density));
}

/// <summary>
/// Native resolution and density per frame model. Some frame images have screen holes a few pixels off the device's
/// panel (the art was scaled), so the real panel comes from this table. iPhones render at 3x (480 dpi) except the 2x
/// models (6s, 8, SE, XR, 11: 320); the 13 mini's 1080-pixel panel shows 375 pt, so 461. iPads are 2x. Pixels and
/// Galaxys use what the phone itself reports.
/// </summary>
internal static class DeviceSpecs
{
    // Category, model, portrait panel, density.
    private static readonly (string Category, string Model, int Width, int Height, int Density)[] Table =
    [
        ("Apple iPhone", "6s", 750, 1334, 320),
        ("Apple iPhone", "8", 750, 1334, 320),
        ("Apple iPhone", "SE", 750, 1334, 320),
        ("Apple iPhone", "8 Plus", 1242, 2208, 480),
        ("Apple iPhone", "X", 1125, 2436, 480),
        ("Apple iPhone", "XS", 1125, 2436, 480),
        ("Apple iPhone", "XR", 828, 1792, 320),
        ("Apple iPhone", "11", 828, 1792, 320),
        ("Apple iPhone", "11 Pro", 1125, 2436, 480),
        ("Apple iPhone", "11 Pro Max", 1242, 2688, 480),
        ("Apple iPhone", "12", 1170, 2532, 480),
        ("Apple iPhone", "12 Pro", 1170, 2532, 480),
        ("Apple iPhone", "12 Pro Max", 1284, 2778, 480),
        ("Apple iPhone", "13 mini", 1080, 2340, 461),
        ("Apple iPhone", "13", 1170, 2532, 480),
        ("Apple iPhone", "13 Pro", 1170, 2532, 480),
        ("Apple iPhone", "13 Pro Max", 1284, 2778, 480),
        ("Apple iPhone", "14", 1170, 2532, 480),
        ("Apple iPhone", "14 Pro", 1179, 2556, 480),
        ("Apple iPhone", "14 Pro Max", 1290, 2796, 480),
        ("Apple iPhone", "15", 1179, 2556, 480),
        ("Apple iPhone", "15 Pro", 1179, 2556, 480),
        ("Apple iPhone", "15 Pro Max", 1290, 2796, 480),
        ("Apple iPhone", "16", 1179, 2556, 480),
        ("Apple iPhone", "16 Plus", 1290, 2796, 480),
        ("Apple iPhone", "16 Pro", 1206, 2622, 480),
        ("Apple iPhone", "16 Pro Max", 1320, 2868, 480),
        ("Apple iPhone", "17", 1206, 2622, 480),
        ("Apple iPhone", "17 Pro", 1206, 2622, 480),
        ("Apple iPhone", "17 Pro Max", 1320, 2868, 480),
        ("Apple iPhone", "Air", 1260, 2736, 480),
        ("Apple iPad", "iPad Air - 10.9 M1", 1640, 2360, 320),
        ("Apple iPad", "iPad Air 11 M2 & M3", 1640, 2360, 320),
        ("Apple iPad", "iPad Air 13 M2 & M3", 2048, 2732, 320),
        ("Apple iPad", "iPad Pro 11 A12X to M2", 1668, 2388, 320),
        ("Apple iPad", "iPad Pro 11 M4 & M5", 1668, 2420, 320),
        ("Apple iPad", "iPad Pro 13 A12X to M2", 2048, 2732, 320),
        ("Apple iPad", "iPad Pro 13 M4 & M5", 2064, 2752, 320),
        ("Apple iPad", "iPad mini 8.3 A17 Pro", 1488, 2266, 320),
        ("Android Phone", "Pixel 8", 1080, 2400, 420),
        ("Android Phone", "Pixel 8 Pro", 1344, 2992, 480),
        ("Android Phone", "Pixel 9", 1080, 2424, 420),
        ("Android Phone", "Pixel 9 Pro", 1280, 2856, 480),
        ("Android Phone", "Pixel 9 Pro XL", 1344, 2992, 480),
        ("Android Phone", "Samsung Galaxy S21", 1080, 2400, 480),
        ("Android Tablet", "Pixel Tablet", 1600, 2560, 320),
        ("Android Tablet", "Samsung Galaxy Tab S11 Ultra", 1848, 2960, 360),
    ];

    /// <summary>The device a frame shows, the same way round as the frame (a landscape iPad frame is a landscape panel).</summary>
    public static DeviceSpec For(FrameInfo frame) => For(frame.Category, frame.Model, frame.Screen.Size);

    public static DeviceSpec For(string category, string model, Size hole)
    {
        if (string.Equals(category, IPhoneDuo.Category, StringComparison.OrdinalIgnoreCase) && string.Equals(model, IPhoneDuo.Model, StringComparison.OrdinalIgnoreCase))
            return new DeviceSpec(IPhoneDuo.Surface, IPhoneDuo.Density, true);

        bool landscape = hole.Width > hole.Height;
        foreach (var row in Table)
        {
            if (!string.Equals(row.Category, category, StringComparison.OrdinalIgnoreCase) || !string.Equals(row.Model, model, StringComparison.OrdinalIgnoreCase)) continue;
            return new DeviceSpec(landscape ? new Size(row.Height, row.Width) : new Size(row.Width, row.Height), row.Density, true);
        }

        // Unknown model: the frame's own screen, at the category's usual scale.
        int shortSide = Math.Min(hole.Width, hole.Height);
        int density = category switch
        {
            "Apple iPhone" => shortSide <= 828 ? 320 : 480,
            "Apple iPad" or "Apple Watch" => 320,
            "Android Tablet" => 320,
            _ => (int)Math.Round(160.0 * shortSide / 411), // a typical Android phone is 411 dp wide
        };
        return new DeviceSpec(hole, density, false);
    }

    /// <summary>True when an emulator with this panel and density already is the device (MuMu reports either orientation).</summary>
    public static bool Matches(DeviceSpec spec, Size physical, int density) =>
        (physical == spec.Native || physical == new Size(spec.Native.Height, spec.Native.Width)) && density == spec.Density;
}
