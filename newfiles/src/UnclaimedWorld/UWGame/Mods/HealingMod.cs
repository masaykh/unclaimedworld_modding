using System;
using UWGame.SimSide.AI.Needs;
using UWGame.SimSide.Entities;
using UWGame.SimSide.Entities.Biological;

namespace UWGame.Mods;

/// <summary>
/// Recovery that finishes, and that depends on how well someone is being looked after.
///
/// WHAT THE GAME ALREADY DOES, because "healing is not implemented" turned out to be not quite
/// right. <c>Body.RegainHitpoints</c> heals every tick:
///
///     rate = max(EnergyLevel, 0.5) * FractionOfMaxHitpointsGainedPerDay * MaxHitpoints
///     ceiling = lowestHitpointsFraction + (1 - lowestHitpointsFraction) * MaxRegainLimit
///
/// For a human that is 0.3 of maximum hitpoints per day, and MaxRegainLimit is 0.5. So somebody
/// beaten down to 40% heals to 40 + 60 x 0.5 = 70% and then stops, for ever. That is why it reads
/// as "settlers seems like never healing up by time" - they heal, halfway, and then carry the rest
/// of that wound until something else kills them. Reported by Kastuk.
///
/// WHAT THIS CHANGES.
///
///   * The ceiling goes to full. A colonist who is fed, rested and content recovers completely.
///   * The rate is driven by the things his request names - food energy, protein, micronutrients,
///     sleep and morale - rather than by muscle energy alone. Being fed and rested is what makes
///     recovery quick; being hungry and miserable is what makes it crawl.
///
/// The rate is a MULTIPLIER on the studio's own number, never a replacement for it, so a species
/// that heals fast still heals fast and the balance the studio set stays visible underneath.
///
/// NOT DONE, and worth saying: no doctor, no medicine, no medical building, and no way to order
/// someone to rest - all four are in the request, and all four are new systems rather than a
/// different arithmetic on an existing one. This is the part that can be done without inventing a
/// profession.
/// </summary>
public static class HealingMod
{
    public const string ModId = "healing";

    private static ModSetting fullRecovery;

    private static ModSetting needsDrivenRate;

    /// <summary>
    /// Whether a wound can heal all the way. Off leaves the studio's half-way ceiling in place.
    /// </summary>
    public static ModSetting FullRecovery =>
        fullRecovery ?? (fullRecovery = ModSettings.Toggle(
            ModId, "fullRecovery", "WOUNDS HEAL COMPLETELY", defaultValue: true,
            toolTip: "The game stops healing halfway back to full - a colonist hurt to 40% " +
                     "recovers to 70% and no further, permanently. This lets them finish."));

    /// <summary>
    /// Whether food, sleep and morale change how fast someone heals.
    /// </summary>
    public static ModSetting NeedsDrivenRate =>
        needsDrivenRate ?? (needsDrivenRate = ModSettings.Toggle(
            ModId, "needsDrivenRate", "FOOD AND REST SPEED HEALING", defaultValue: true,
            toolTip: "Healing runs between a third and about twice the normal speed depending on " +
                     "food energy, protein, micronutrients, sleep and morale. Off uses the " +
                     "studio's rate, which only looks at muscle energy."));

    public static void RegisterSettings()
    {
        _ = FullRecovery;
        _ = NeedsDrivenRate;
    }

    /// <summary>Whether either half of this mod is doing anything.</summary>
    public static bool Enabled => FullRecovery.On || NeedsDrivenRate.On;

    /// <summary>
    /// The fraction of maximum hitpoints a body may recover to.
    ///
    /// <paramref name="studioCeiling"/> is what the game computed; returning it unchanged is what
    /// happens with the switch off.
    /// </summary>
    public static float RecoveryCeiling(float studioCeiling)
    {
        return FullRecovery.On ? 1f : studioCeiling;
    }

    /// <summary>
    /// What to multiply the studio's healing rate by, for this entity, right now.
    ///
    /// Between <see cref="SlowestFactor"/> and <see cref="FastestFactor"/>. The five needs are
    /// averaged rather than multiplied together: one bad number should slow recovery down, not
    /// stop it, and a colonist who is miserable but well fed should still mend.
    ///
    /// A need the species does not have is skipped rather than counted as zero - not every
    /// creature has all five, and an absent need is no evidence about recovery either way.
    /// </summary>
    public static float RateFactor(Entity entity)
    {
        if (!NeedsDrivenRate.On || entity == null)
        {
            return 1f;
        }

        float total = 0f;
        int counted = 0;
        AddNeed(entity, "foodEnergy", ref total, ref counted);
        AddNeed(entity, "protein", ref total, ref counted);
        AddNeed(entity, "micronutrients", ref total, ref counted);
        AddNeed(entity, "sleep", ref total, ref counted);

        float morale = Morale(entity);
        if (morale >= 0f)
        {
            total += morale;
            counted++;
        }

        if (counted == 0)
        {
            return 1f;
        }

        // An average of 1 - everything satisfied - is the studio's own speed doubled; an average
        // of 0 is a third of it. Someone starving still mends, slowly, which is both kinder and
        // more believable than a colonist whose wounds freeze the moment they miss a meal.
        float average = Common.Clamp(total / counted, 0f, 1f);
        return SlowestFactor + (FastestFactor - SlowestFactor) * average;
    }

    /// <summary>Multiplier when nothing is satisfied.</summary>
    public const float SlowestFactor = 0.33f;

    /// <summary>Multiplier when everything is.</summary>
    public const float FastestFactor = 2f;

    private static void AddNeed(Entity entity, string key, ref float total, ref int counted)
    {
        BiologicalEntity biological = entity.BiologicalEntity;
        if (biological?.Needs?.NeedsList == null)
        {
            return;
        }
        if (biological.Needs.NeedsList.TryGetValue(key, out Need need) && need != null)
        {
            total += Common.Clamp(need.CurrentLevel, 0f, 1f);
            counted++;
        }
    }

    /// <summary>Morale as a 0..1 fraction, or -1 when this entity has no morale to speak of.</summary>
    private static float Morale(Entity entity)
    {
        try
        {
            if (entity.Intelligence == null)
            {
                return -1f;
            }
            return Common.Clamp(entity.Intelligence.Morale, 0f, 1f);
        }
        catch (Exception)
        {
            return -1f;
        }
    }
}
