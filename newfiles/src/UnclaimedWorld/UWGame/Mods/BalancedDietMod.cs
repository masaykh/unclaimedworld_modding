using System.Collections.Generic;
using UWGame.SimSide.Items;

namespace UWGame.Mods;

/// <summary>
/// Meat carries protein, plants carry vitamins, and only a cooked meal carries both.
///
/// THE REPORT. "Food nutrients is implemented, but some food is too universal and contain
/// everything at once, like varied fish dishes, which is quite boring. Let's make animal product
/// contain more of protein and plant products to contain mostly micronutrients." - Kastuk.
///
/// THE STOCK TABLE. Twelve profiles, each naming an amount of foodEnergy, protein and
/// micronutrients: poorMeat, mediumMeat, richMeat, meatSoup, poorVegetables, richVegetables,
/// poorStaple, richStaple, highEnergy, balanced, meal, lowWeightBalancedMeal. Raw meat already
/// carries more protein than a vegetable does - the trouble is that it carries a useful amount of
/// everything else too, so one food source feeds a colonist indefinitely and the kitchen is optional.
///
/// WHAT THIS CHANGES. The RAW families are specialised and the PREPARED ones are left exactly as
/// they are:
///
///   meat        protein x1.4   micronutrients x0.4
///   vegetables  protein x0.5   micronutrients x1.5
///   staples     protein x0.6   micronutrients x0.8
///
/// foodEnergy is untouched throughout: how far a meal goes is the studio's balance, and this is
/// about what it is made of rather than how much of it you need.
///
/// Leaving balanced, meal, lowWeightBalancedMeal and highEnergy alone is the point rather than an
/// omission. A cooked meal SHOULD be complete - that is what cooking is for - and the change is
/// only worth anything if the prepared food stays better than the sum of raw parts.
///
/// NOT DONE: morale from eating the same thing every day. That needs somewhere to remember what a
/// colonist has been eating, which is new state on the person and a save-format change, rather
/// than a different number in a table.
/// </summary>
public static class BalancedDietMod
{
    public const string ModId = "diet";

    private static ModSetting specialiseRawFood;

    /// <summary>Whether raw food families are pushed towards what they are actually good for.</summary>
    public static ModSetting SpecialiseRawFood =>
        specialiseRawFood ?? (specialiseRawFood = ModSettings.Toggle(
            ModId, "specialiseRawFood", "RAW FOOD IS SPECIALISED", defaultValue: true,
            toolTip: "Meat carries more protein and fewer vitamins, vegetables the reverse, and " +
                     "staples less of both. Cooked meals are unchanged, so a varied diet or a " +
                     "kitchen becomes worth having. Food energy is untouched.",
            affectsSimulation: true, takesEffectOnNextLoad: true));

    public static void RegisterSettings()
    {
        _ = SpecialiseRawFood;
    }

    /// <summary>The profiles for food that comes off an animal.</summary>
    private static readonly string[] MeatProfiles = { "poorMeat", "mediumMeat", "richMeat", "meatSoup" };

    /// <summary>The profiles for food that grows.</summary>
    private static readonly string[] VegetableProfiles = { "poorVegetables", "richVegetables" };

    /// <summary>Grains and roots - bulk, not nutrition.</summary>
    private static readonly string[] StapleProfiles = { "poorStaple", "richStaple" };

    /// <summary>
    /// Called at the end of BaseDataLoader.InitFoodNutrientProfiles, with the table the game is
    /// about to use.
    ///
    /// Scales rather than replaces: every number stays a multiple of the studio's, so the
    /// relationship between poor, medium and rich survives, and so does anything they change in a
    /// later version of the game.
    /// </summary>
    public static void AdjustProfiles(List<FoodNutrientProfile> profiles)
    {
        if (!SpecialiseRawFood.On || profiles == null)
        {
            return;
        }

        foreach (FoodNutrientProfile profile in profiles)
        {
            if (profile?.FoodNutrientTypes == null || profile.DeleteRecord)
            {
                continue;
            }
            if (Contains(MeatProfiles, profile.KeyName))
            {
                Scale(profile, "protein", 1.4f);
                Scale(profile, "micronutrients", 0.4f);
            }
            else if (Contains(VegetableProfiles, profile.KeyName))
            {
                Scale(profile, "protein", 0.5f);
                Scale(profile, "micronutrients", 1.5f);
            }
            else if (Contains(StapleProfiles, profile.KeyName))
            {
                Scale(profile, "protein", 0.6f);
                Scale(profile, "micronutrients", 0.8f);
            }
        }
    }

    private static bool Contains(string[] keys, string key)
    {
        foreach (string candidate in keys)
        {
            if (candidate == key)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Multiplies one nutrient in one profile, if the profile carries it at all.
    ///
    /// A profile that does not list a nutrient is left alone rather than given one: "less protein"
    /// is a change to food that has protein, and inventing an amount where the studio wrote none
    /// would be a different decision wearing the same switch.
    /// </summary>
    private static void Scale(FoodNutrientProfile profile, string nutrientKey, float factor)
    {
        FoodNutrientAmount[] amounts = profile.FoodNutrientTypes;
        foreach (FoodNutrientAmount amount in amounts)
        {
            if (amount?.Nutrient != null && amount.Nutrient.KeyName == nutrientKey)
            {
                amount.Amount *= factor;
            }
        }
    }
}
