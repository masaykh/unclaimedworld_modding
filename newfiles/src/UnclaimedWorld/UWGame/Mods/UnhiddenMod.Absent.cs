using System.Collections.Generic;
using UWGame.ClientSide.Interface.Inventory;
using UWGame.SimSide.Entities;
using UWGame.SimSide.Processes;
using UWGame.SimSide.Scenarios;

namespace UWGame.Mods;

/// <summary>
/// The Unhidden Mod, compiled OUT. Selected by building without the <c>unhiddenmod</c> feature:
/// <c>-p:UwFeatures="harmony"</c>. See the SELECTABLE FEATURES block in UnclaimedWorld.csproj.
///
/// WHY A STUB RATHER THAN #if AT EVERY CALL SITE. The mod is reached from eleven places across
/// the game's own files. Wrapping each one in <c>#if UW_FEATURE_UNHIDDENMOD</c> would mean
/// eleven more edits to the studio's source - which matters twice over: it makes the code harder
/// to read, and it makes the patch series distributed in kit/ larger for no benefit. With a
/// stub, those eleven call sites are byte-identical whether the feature is in or out.
///
/// <see cref="Enabled"/> is a compile-time constant false, so every guarded block is dead code
/// the compiler drops. The methods exist only to satisfy the calls inside those blocks; none of
/// them can execute. The real content - the items, the recipes, the two smithies - is not
/// compiled into the assembly at all, which is the honest meaning of "I did not install that".
///
/// Exactly one of this file and UnhiddenMod.cs is ever compiled; the csproj removes the other.
/// </summary>
public static class UnhiddenMod
{
    /// <summary>Always false: the mod is not present in this build.</summary>
    public const bool Enabled = false;

    /// <summary>Kept assignable because Client.HandleInput writes to it inside a dead branch.</summary>
    public static bool ShadowsDisabled;

    public static void Disable()
    {
    }

    public static void ApplyCulture()
    {
    }

    public static List<Scenario> AllScenarioHeaders()
    {
        return null;
    }

    public static void AddItems(List<EntityType> listOfEntityTypes)
    {
    }

    public static void AddProcesses(List<ProcessType> listOfProcessTypes)
    {
    }

    public static void RegisterAttainability(
        Dictionary<EntityType, Dictionary<ProcessType, AttainableInfo>> attainableInfo)
    {
    }
}

/// <summary>
/// Stub for the mod's smithy redefinitions, for the same reason as <see cref="UnhiddenMod"/>.
///
/// It needs its own type because BaseDataLoader.InitEntityTypes calls
/// <c>UnhiddenModSmithies.Replace</c> directly rather than through UnhiddenMod - so leaving it
/// out broke the two feature combinations that exclude the mod, which is exactly what building
/// all four combinations is for.
/// </summary>
internal static class UnhiddenModSmithies
{
    public static void Replace(List<EntityType> listOfEntityTypes)
    {
    }
}
