using Microsoft.Xna.Framework;
using UWGame.Control;

namespace UWGame.Mods;

/// <summary>
/// Stops the camera at the edge of the map.
///
/// THE STUDIO'S RULE. <c>MapClient.ChangeMapWindowWorldPosition</c> clamps the view's world
/// position to <c>[-halfScreen, mapSize - halfScreen]</c> - half a screen of overscroll at every
/// edge. So the map can be pushed until half the window is off the world, showing the empty grid
/// behind it with sprites hanging over nothing. Reported with a screenshot by Kastuk: "not show
/// the emptiness behind curtains".
///
/// THIS RULE. The visible rectangle stays inside the map: <c>[0, mapSize - screen]</c>. When the
/// map is SMALLER than the window there is no such range - the whole map fits with room to spare -
/// so it is centred instead, which is the only sensible reading of "do not show past the edge"
/// when the edge is inside the window on both sides.
///
/// Per axis, because a map can be wide and short.
///
/// WHY A MOD AND NOT A PATCH. It changes how the game plays - a camera that stops is a different
/// camera, and someone building at the very edge of a map may want the overscroll to see what they
/// are doing. The port's own patches leave gameplay alone.
/// </summary>
public static class MapEdgeMod
{
    public const string ModId = "mapedge";

    private static ModSetting stopAtEdge;

    /// <summary>Whether the camera is held inside the map.</summary>
    public static ModSetting StopAtEdge =>
        stopAtEdge ?? (stopAtEdge = ModSettings.Toggle(
            ModId, "stopAtEdge", "STOP SCROLLING AT THE MAP EDGE", defaultValue: true,
            toolTip: "Keeps the view inside the map instead of letting it scroll half a screen " +
                     "past every edge, where the empty grid behind the world shows."));

    /// <summary>Registered from Program.Main, like every other mod's settings.</summary>
    public static void RegisterSettings()
    {
        _ = StopAtEdge;
    }

    /// <summary>Whether the call site should use <see cref="Clamp"/> rather than the studio's.</summary>
    public static bool Enabled => StopAtEdge.On;

    /// <summary>
    /// The wanted position, held inside the map.
    ///
    /// <paramref name="drawArea"/> is the size of the visible area in world units - the game
    /// renders one to one at zoom 1, and the studio's own clamp compares DrawArea against
    /// MapWorldWidth the same way.
    /// </summary>
    public static Vector2 Clamp(Vector2 wanted, Dimension drawArea, float mapWorldWidth, float mapWorldHeight)
    {
        return new Vector2(
            ClampAxis(wanted.X, drawArea.Width, mapWorldWidth),
            ClampAxis(wanted.Y, drawArea.Height, mapWorldHeight));
    }

    private static float ClampAxis(float wanted, int screen, float world)
    {
        float slack = world - screen;
        if (slack <= 0f)
        {
            // The map does not fill the window. Centre it: half the difference on each side, which
            // is a negative offset because the view starts before the world does.
            return slack / 2f;
        }
        return Common.Clamp(wanted, 0f, slack);
    }
}
