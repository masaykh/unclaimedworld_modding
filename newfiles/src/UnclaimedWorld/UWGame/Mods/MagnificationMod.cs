namespace UWGame.Mods;

/// <summary>
/// Magnification below 1, and a warning when the interface will not fit.
///
/// HOW MAGNIFICATION WORKS HERE. The whole game - world and interface together - is drawn into a
/// render target of <c>backBuffer / ZoomFactor</c> and stretched to the window. Above 1 that means
/// fewer, larger pixels: everything gets bigger and coarser. <c>Controller</c> then clamps the
/// factor to a minimum of 1, so the other direction was simply unavailable.
///
/// WHAT THIS CHANGES. The floor comes down to <see cref="Minimum"/>. Below 1 the render target is
/// LARGER than the window and is scaled down, so the interface takes less of the screen and more
/// of the world is visible - which is what somebody on a large monitor asking for a smaller UI
/// actually wants. It costs fill rate: 0.5 is four times the pixels.
///
/// AND A WARNING. The interface is laid out against a minimum drawable width; below it, panels are
/// cut off rather than rearranged, which is the "it's just cut out part of screen at low enough
/// resolution" in the report. The game now says so at startup instead of leaving somebody to
/// discover a truncated dialog.
///
/// NOT DONE, and it is the larger half of the request: separating interface scale from world zoom.
/// One render target holds both, so they cannot scale independently without giving the world its
/// own target and its own camera transform. That is a rendering change rather than a setting, and
/// this mod would be the wrong place for it.
/// </summary>
public static class MagnificationMod
{
    public const string ModId = "magnification";

    /// <summary>
    /// The smallest factor allowed. 0.5 is four times the pixels of 1.0 - already a real cost on a
    /// large display - and small enough that the interface is a third of the height it was.
    /// </summary>
    public const float Minimum = 0.5f;

    /// <summary>
    /// The narrowest draw area the interface is laid out for. The widest fixed panel is the save
    /// and load dialog at 822 logical pixels; below about a thousand there is no room for it plus
    /// the margins the panels assume.
    /// </summary>
    public const int MinimumUsableWidth = 1024;

    /// <summary>The matching height, from the same panels.</summary>
    public const int MinimumUsableHeight = 720;

    private static ModSetting allowBelowOne;

    private static ModSetting warnWhenTooSmall;

    /// <summary>Whether magnification may go below 1.</summary>
    public static ModSetting AllowBelowOne =>
        allowBelowOne ?? (allowBelowOne = ModSettings.Toggle(
            ModId, "allowBelowOne", "ALLOW MAGNIFICATION BELOW 1.0", defaultValue: true,
            toolTip: "Lets the magnification slider go down to 0.5, which renders at a higher " +
                     "resolution than the window and scales down - a smaller interface and more " +
                     "of the world on screen. It costs performance: 0.5 is four times the pixels."));

    /// <summary>Whether to complain when the resulting draw area is too small for the interface.</summary>
    public static ModSetting WarnWhenTooSmall =>
        warnWhenTooSmall ?? (warnWhenTooSmall = ModSettings.Toggle(
            ModId, "warnWhenTooSmall", "WARN WHEN THE INTERFACE WILL NOT FIT", defaultValue: true,
            toolTip: "Reports at startup when the resolution and magnification together leave " +
                     "less room than the interface is laid out for, instead of letting panels be " +
                     "cut off at the screen edge."));

    public static void RegisterSettings()
    {
        _ = AllowBelowOne;
        _ = WarnWhenTooSmall;
    }

    /// <summary>
    /// The magnification to actually use, given what the options file asked for.
    ///
    /// The studio's line was <c>ClampBottom(ZoomFactor, 1f)</c>; this replaces it, so with the
    /// switch off the answer is identical.
    /// </summary>
    public static float Clamp(float wanted)
    {
        if (!AllowBelowOne.On)
        {
            return Common.ClampBottom(wanted, 1f);
        }
        // A zero or negative factor would divide the back buffer by zero. The floor is a real
        // limit rather than a guard against nonsense, but it serves as both.
        return Common.Clamp(wanted, Minimum, 4f);
    }

    /// <summary>
    /// Whether the scaled render target is used at all. Was <c>ActiveZoomFactor &gt; 1f</c>, which
    /// silently disabled everything below 1 even once the clamp allowed it.
    /// </summary>
    public static bool ZoomIsActive(float activeZoomFactor)
    {
        if (!AllowBelowOne.On)
        {
            return activeZoomFactor > 1f;
        }
        return !Common.IsZero(activeZoomFactor - 1f);
    }

    /// <summary>
    /// A sentence about the interface not fitting, or null when it does.
    ///
    /// Reported once at startup by the caller. It names both numbers because either can be the
    /// cause: a small window, or a magnification that made a large one small.
    /// </summary>
    public static string TooSmallWarning(int drawWidth, int drawHeight, float magnification)
    {
        if (!WarnWhenTooSmall.On)
        {
            return null;
        }
        if (drawWidth >= MinimumUsableWidth && drawHeight >= MinimumUsableHeight)
        {
            return null;
        }
        return $"The interface is laid out for at least {MinimumUsableWidth}x{MinimumUsableHeight}"
            + $" and this session has {drawWidth}x{drawHeight}"
            + (Common.IsZero(magnification - 1f) ? "" : $" (magnification {magnification:0.00})")
            + ". Panels wider than that are cut off at the screen edge rather than rearranged."
            + " Raise the resolution, or lower the magnification in the options.";
    }
}
