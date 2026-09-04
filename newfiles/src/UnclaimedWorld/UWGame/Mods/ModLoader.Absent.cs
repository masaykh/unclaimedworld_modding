using System;
using System.Collections.Generic;

namespace UWGame.Mods;

/// <summary>
/// The Harmony mod loader, compiled OUT. Selected by building without the <c>harmony</c>
/// feature: <c>-p:UwFeatures="unhiddenmod"</c>. See the SELECTABLE FEATURES block in
/// UnclaimedWorld.csproj.
///
/// A build without this feature references no HarmonyLib and ships no 0Harmony.dll, so nothing
/// can load third-party assemblies into the process. That is a real choice, not a cosmetic one:
/// the loader exists to run other people's code, and someone who does not want that should be
/// able to have a build that cannot.
///
/// Same stub arrangement as <see cref="UnhiddenMod"/> - see the note there for why this is
/// preferable to <c>#if</c> at the call sites. <see cref="Enabled"/> is a constant false and
/// <see cref="LoadAll"/> does nothing, so <c>user/Mods</c> is never even looked at.
///
/// Exactly one of this file and ModLoader.cs is ever compiled; the csproj removes the other.
/// </summary>
public static class ModLoader
{
    /// <summary>Always false: no loader is present in this build.</summary>
    public const bool Enabled = false;

    public static IReadOnlyList<string> Loaded => Array.Empty<string>();

    public static IReadOnlyList<string> Failed => Array.Empty<string>();

    public static void Disable()
    {
    }

    /// <summary>
    /// Does nothing, and deliberately does not report anything either. A build without the
    /// loader should be silent about mods, not warn about a feature the user chose to omit.
    /// </summary>
    public static void LoadAll(Action<string, string> log = null)
    {
    }
}
