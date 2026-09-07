using UWGame.SimSide.Entities;
using UWGame.SimSide.Jobs;
using UWGame.SimSide.Maps;

namespace UWGame.Mods;

/// <summary>
/// Colonists stop walking into fights nobody sent them to.
///
/// THE REPORT. "Non-combatant settlers sometimes is walking towards aggressive fauna and attack
/// them while being alone, then taking damage in fight and sometimes dying foolishly. Even if
/// their Stance is Vigilant or lower (expected to let them stay out of danger)." - Kastuk.
///
/// WHERE IT COMES FROM. The allegiance's ThreatJobManager creates a ThreatJob for anything it
/// judges dangerous, and <c>EvaluateAttackJobs</c> offers every one of them to every colonist as
/// work. Nothing in that path asks whether this particular person should be picking a fight: a
/// cook with a kitchen knife scores the job the same way a guard with a rifle does, and the
/// nearest body wins.
///
/// WHAT THIS CHANGES. A threat job is declined unless the colonist's threat stance is Bold. The
/// stance is the game's own answer to "how much danger is this one willing to be in", it already
/// moves with health, morale and orders, and it is what the player is adjusting when they set
/// someone Vigilant and expect them to keep out of trouble.
///
/// THREE THINGS IT DELIBERATELY LEAVES ALONE:
///
///   * ASSET threats - something attacking the colony's animals, crops or structures. Declining
///     those would mean watching a vermin eat the harvest, which is not self-preservation.
///   * Hunting, and anything else the player ordered. An order is the player saying "yes, you".
///   * The threat evaluation itself, which also drives fleeing and stance changes. Making people
///     less willing to ATTACK is one change; making them blind to danger would be another.
///
/// NOT DONE, from the rest of the request: traits and professions as an input (a guard should
/// answer a threat a cook should not - the game has no combat profession to read), and restricting
/// which weapons get spent on which enemy, which is the material-policy work rather than this.
/// </summary>
public static class SelfPreservationMod
{
    public const string ModId = "selfpreservation";

    private static ModSetting onlyBoldFight;

    private static ModSetting animalsNeedCompany;

    /// <summary>
    /// Whether a colonist who is not in a Bold stance declines unordered fights.
    /// </summary>
    public static ModSetting OnlyBoldFight =>
        onlyBoldFight ?? (onlyBoldFight = ModSettings.Toggle(
            ModId, "onlyBoldFight", "ONLY BOLD COLONISTS PICK FIGHTS", defaultValue: true,
            toolTip: "A colonist whose threat stance is not Bold ignores threats nobody ordered " +
                     "them to deal with, instead of walking over to an aggressive animal alone. " +
                     "Defending the colony's animals and structures is unaffected, and so is " +
                     "anything you order."));

    /// <summary>
    /// Whether an animal of the colony waits for a person before joining a fight.
    /// </summary>
    public static ModSetting AnimalsNeedCompany =>
        animalsNeedCompany ?? (animalsNeedCompany = ModSettings.Toggle(
            ModId, "animalsNeedCompany", "DOGS FIGHT ONLY ALONGSIDE PEOPLE", defaultValue: true,
            toolTip: "A colony animal joins a fight only once one of your people is already on " +
                     "it, instead of charging anything it sees on its own."));

    public static void RegisterSettings()
    {
        _ = OnlyBoldFight;
        _ = AnimalsNeedCompany;
    }

    /// <summary>Whether anything here is switched on. Cheap enough to test at every call site.</summary>
    public static bool Enabled => OnlyBoldFight.On || AnimalsNeedCompany.On;

    /// <summary>
    /// Whether <paramref name="entity"/> should refuse to consider this unordered threat job.
    ///
    /// Called from EvaluateAttackJobs while it collects the jobs a colonist could take, so a
    /// declined job is simply never scored - no half-started walk, no cancelled order, and no
    /// interaction with whatever they were doing instead.
    /// </summary>
    public static bool DeclinesThreat(Entity entity, AttackJob job, bool isAssetThreat)
    {
        if (!Enabled || entity == null || job == null)
        {
            return false;
        }

        // Defending the colony's own things is not the behaviour being complained about.
        if (isAssetThreat)
        {
            return false;
        }

        if (entity.Intelligence == null)
        {
            return false;
        }

        // EntityType.Person is what the game itself uses to decide who counts as one of the
        // colony's people - Allegiance.Persons is filled from exactly this test.
        bool isPerson = entity.EntityType?.Person != null;

        if (!isPerson)
        {
            // An animal of the colony. It may join a fight that a person is already handling -
            // which is what a dog is for - but does not start one.
            if (!AnimalsNeedCompany.On)
            {
                return false;
            }
            return !HasHumanTaker(job);
        }

        if (!OnlyBoldFight.On)
        {
            return false;
        }

        // Bold is the stance of somebody who has accepted being in danger: it is what an attack
        // order puts them in, and what a confident, healthy colonist reaches on their own.
        return entity.Intelligence.ThreatStance != ThreatStance.Bold;
    }

    /// <summary>Whether one of the colony's people is already working this job.</summary>
    private static bool HasHumanTaker(AttackJob job)
    {
        foreach (Entity taker in job.GetTakersSortedByDistance())
        {
            if (taker?.EntityType?.Person != null)
            {
                return true;
            }
        }
        return false;
    }
}
