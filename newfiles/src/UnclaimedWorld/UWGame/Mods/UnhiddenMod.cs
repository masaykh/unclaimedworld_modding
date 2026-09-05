using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UWGame.ClientSide.Interface.Inventory;
using UWGame.ClientSide.Renderables;
using UWGame.SimSide.AllGameData;
using UWGame.SimSide.AllGameData.Scenarios;
using UWGame.SimSide.Entities;
using UWGame.SimSide.Items;
using UWGame.SimSide.Policies;
using UWGame.SimSide.Processes;
using UWGame.SimSide.Scenarios;

namespace UWGame.Mods;

/// <summary>
/// "Unhidden Mod" - a set of community fixes and content additions, contributed as BepInEx /
/// HarmonyLib patches and applied here directly in source.
///
/// PROVENANCE. The patches came from a modder working without source: four files, 814 lines,
/// twelve [HarmonyPatch] targets, six private methods reached by reflection, seven private
/// fields reached by Harmony's "___field" injection, and no transpilers. They are reproduced
/// here as ordinary source edits, which is why none of that machinery survives - HarmonyLib,
/// BepInEx and the reflection all exist to reach things you cannot name from outside an
/// assembly, and from inside the assembly they are just fields and methods. The original patch
/// files are kept verbatim under patches/ as the record of what was intended.
///
/// The one thing not carried over is UnclaimedWorldInjector.GM.Logger, the BepInEx plugin's own
/// logger. Those calls were debug tracing and are dropped rather than rerouted.
///
/// WHAT IT CHANGES, in two groups:
///
///   Interface and controls - no effect on simulation state
///     * the main menu gains EDIT and TEST buttons, calling the MainMenuScreen.EditMap and
///       TestMap methods the studio already ships but never wired to a button
///     * the scenario picker lists user scenarios from user/Scenarios, not only built-in ones
///     * saved games sort newest first
///     * stockpile categories sort ascending
///     * the mouse wheel scrolls data sheets
///     * "O" toggles shadows, by forcing DateAndTime.SunIsUp false
///     * Config.Culture becomes en-GB rather than InvariantCulture
///
///   Simulation content - CHANGES BALANCE AND SAVE COMPATIBILITY
///     * item:charcoal is redefined to carry the "fuelForForge" fuel TAG
///     * makeCharcoalFromPeat is added: 1 dry peat -> 1 REAL charcoal
///     * structure:simpleSmithy and structure:improvisedSmithy are redefined to require the
///       "fuelForForge" tag instead of the literal item:charcoal, so any tagged fuel works
///
/// WHAT IT NO LONGER CHANGES, and why. It used to add item:peatCharcoal, a second forge fuel,
/// with a makePeatCharcoal recipe. That was the riskiest thing here: a NEW EntityType key, which
/// the snapshot serializer resolves by name, so a save holding one item of it could not be loaded
/// by a build without the mod at all - not "might misbehave", could not load. It also could not be
/// made into gunpowder or sold, because much of the game matches charcoal by key rather than by
/// tag. Both problems are answered by producing the real item instead, which makeCharcoalFromPeat
/// does, so the separate item was removed rather than kept alongside it.
///
/// What remains adds one ProcessType key and no EntityType keys, and it is switchable at
/// <c>unhidden.charcoalFromPeat</c> in user/ModSettings.xml. A save is stamped with the modded
/// content it was made with (see <see cref="ModSettings.Signature"/>), so loading it into a
/// mismatched configuration is caught and offered a fix rather than failing in the loader.
/// </summary>
public static class UnhiddenMod
{
    /// <summary>
    /// Whether the mod is active. ON by default in this build; pass -nomods for a stock game
    /// (parsed in GameStateManagement.Program.Main).
    ///
    /// Every call site tests this, so the stock path is the studio's code with one branch in
    /// front of it rather than a separate build. That matters for a modding base: there is one
    /// binary to support, and "does this happen without the mod?" is answerable without a
    /// recompile.
    /// </summary>
    public static bool Enabled { get; private set; } = true;

    public static void Disable()
    {
        Enabled = false;
    }

    /// <summary>Toggled by "O" in Client.HandleInput, read by DateAndTime.ComputeSunAndLight.</summary>
    public static bool ShadowsDisabled;

    // ---------------------------------------------------------------------------- settings
    //
    // The mod's switches, in user/ModSettings.xml and in the MODS section of the options menu.
    // Registered once from Program.Main, BEFORE any data table is built - AddProcesses reads
    // CharcoalFromPeat while the tables are being assembled, so a registration that happened
    // later would be a registration that happened after it mattered.
    //
    // Each field holds the ModSetting object rather than a copy of its value, so the options
    // menu changing a value is visible here with no plumbing in between.

    private static ModSetting charcoalFromPeat;

    private static ModSetting peatBuilding;

    private static ModSetting experimental;

    /// <summary>
    /// Whether the peat route is in the tables. The one switch here that changes what a save
    /// contains: it adds the makeCharcoalFromPeat ProcessType key, which a save can name.
    /// </summary>
    public static ModSetting CharcoalFromPeat =>
        charcoalFromPeat ?? (charcoalFromPeat = ModSettings.Toggle(
            ModId, "charcoalFromPeat", "CHARCOAL FROM PEAT", defaultValue: true,
            toolTip: "Adds the recipe that turns 1 dry peat into 1 charcoal in a kiln, for maps " +
                     "where trees are scarce. Off gives the stock game's firewood route only. " +
                     "Changes what a save contains: a save made with this on names a recipe a " +
                     "build without it does not have.",
            affectsSimulation: true, takesEffectOnNextLoad: true));

    /// <summary>
    /// Whether peat can stand in for the firewood a campfire or a primitive kitchen is built with.
    ///
    /// Separate from the charcoal recipe because it is a separate thing to want, and because each
    /// switch that adds a ProcessType key is a thing a save can name - so they are listed
    /// separately in a save's stamp rather than lumped together.
    /// </summary>
    public static ModSetting PeatBuilding =>
        peatBuilding ?? (peatBuilding = ModSettings.Toggle(
            ModId, "peatBuilding", "BUILD WITH PEAT", defaultValue: true,
            toolTip: "Lets a campfire, an improvised kitchen and a mudbrick kitchen be built with " +
                     "dry peat instead of firewood. The fuel is not spent building them - it is " +
                     "the load they start with - so peat works as well as a log does.",
            affectsSimulation: true, takesEffectOnNextLoad: true));

    /// <summary>
    /// A switch that does nothing, on purpose.
    ///
    /// It is here so that trying something out costs a recompile and not a redesign: hang an
    /// experiment on <c>if (UnhiddenMod.Experimental.On)</c>, and it arrives with a config entry,
    /// a menu checkbox and a save stamp already working. Marked as affecting the simulation
    /// because an experiment usually does, and a save made under one should say so.
    /// </summary>
    public static ModSetting Experimental =>
        experimental ?? (experimental = ModSettings.Toggle(
            ModId, "experimental", "EXPERIMENTAL", defaultValue: false,
            toolTip: "Reserved for whatever is being tried out in this build. Off in a release.",
            affectsSimulation: true, takesEffectOnNextLoad: true));

    /// <summary>The prefix its settings carry in the file and in save signatures.</summary>
    public const string ModId = "unhidden";

    /// <summary>
    /// Registers every switch above. Call once at startup, after ModSettings.Load and before the
    /// first data load. Touching each property is what registers it - there is no list to keep in
    /// step with the fields, which is one fewer thing to forget.
    /// </summary>
    public static void RegisterSettings()
    {
        _ = CharcoalFromPeat;
        _ = PeatBuilding;
        _ = Experimental;
    }

    /// <summary>
    /// Scenario headers including user-made ones.
    ///
    /// The patch called AllScenarioLoader.LoadAllScenarioHeaders() directly. That enumerates
    /// user/Scenarios with Directory.GetDirectories, which throws DirectoryNotFoundException on
    /// any installation that has never created the folder - every fresh install. Falling back to
    /// the built-in list keeps the scenario menu usable instead of taking out the main menu.
    /// </summary>
    public static List<Scenario> AllScenarioHeaders()
    {
        try
        {
            return (from s in AllScenarioLoader.LoadAllScenarioHeaders()
                    orderby s.SortOrder
                    select s).ToList();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return (from s in RGScenarioLoader.LoadAllScenarioHeaders()
                    orderby s.SortOrder
                    select s).ToList();
        }
    }

    /// <summary>
    /// en-GB rather than InvariantCulture. Both use "." as the decimal separator, so the XML
    /// number parsing that runs through Config.Culture is unaffected; date presentation changes.
    /// </summary>
    public static void ApplyCulture()
    {
        Config.Culture = CultureInfo.GetCultureInfo("en-GB");
    }

    private static EntityType charcoal;

    private static ProcessType makeCharcoalFromPeat;

    private static readonly AttainableInfo Producable = new AttainableInfo(1)
    {
        IsProducable = true
    };

    /// <summary>
    /// Called at the end of ItemLoader.Init. Redefines charcoal to advertise a fuel TAG rather
    /// than being matched by item key, and adds peat charcoal alongside it.
    /// </summary>
    public static void AddItems(List<EntityType> listOfEntityTypes)
    {
        int index = listOfEntityTypes.FindIndex((EntityType s) => s.KeyName == "item:charcoal");
        if (index < 0)
        {
            return;
        }

        listOfEntityTypes[index] = new EntityType("item:charcoal")
        {
            Name = "Charcoal",
            SummaryDescription = "A traditional fuel type made from heat treated organic matter",
            Description = "Charcoal can provide a more intense heat than firewood and is often required for primitive metalworking.",
            ItemType = new ItemType
            {
                MaximumBulk = 0.3f,
                FuelType = new FuelType
                {
                    FuelTags = new string[1] { "fuelForForge" }
                }
            },
            NonLivingType = new NonLivingType
            {
                Repairability = 0f,
                DegradeType = "stored dry"
            },
            TierOrArea = new TierOrArea
            {
                Tier = "basic"
            },
            CategoryKey = "rawMaterials",
            RenderableType = new RenderableType
            {
                DefaultClientState = new ClientStateInfo
                {
                    RenderAsBillboardType = new RenderAsBillboardType[1]
                    {
                        new RenderAsBillboardType
                        {
                            AssetName = "charcoal"
                        }
                    }
                }
            }
        };
        // Kept for RegisterAttainability: the peat route has to be attached to the charcoal the
        // game actually indexes, which is this replacement instance, not the one it replaced.
        charcoal = listOfEntityTypes[index];
    }

    /// <summary>
    /// Called at the end of ProcessLoader.InitProcessTypes. Adds the peat route, unless
    /// <c>unhidden.charcoalFromPeat</c> is off - which is the one switch in this mod that changes
    /// what a save contains, and so the one recorded in the save header.
    /// </summary>
    public static void AddProcesses(List<ProcessType> listOfProcessTypes)
    {
        AddPeatConstruction(listOfProcessTypes);

        if (!CharcoalFromPeat.On)
        {
            return;
        }

        // ------------------------------------------------------------------ charcoal from peat
        //
        // Peat as a source of REAL charcoal.
        //
        // Why the real item and not an item of its own. Much of the game matches charcoal by KEY
        // rather than by tag: gunpowder takes "item:charcoal" as a literal input
        // (ProcessLoader.cs:11565), and PricesProfileLoader and TradeGroupLoader both price and
        // trade "item:charcoal" while knowing nothing about any other kind. An earlier version of
        // this mod added item:peatCharcoal, which burned in a forge - the retagging above handles
        // that - but could never be turned into gunpowder or sold, and which put a brand new
        // EntityType key into saves. Producing the real item closes all of that at once, with no
        // edits to the tables that name it and no new key in the snapshot.
        //
        // Balance:
        //
        //   makeCharcoalFromPeat  2 dry peat -> 1 charcoal,      1/15 day   (peat route)
        //   makeCharcoal (stock)  1 firewood -> 1 charcoal,      1/30 day   (firewood route)
        //
        // 2:1 rather than the stock recipe's 1:1, and the reason is not the work upstream - dry
        // peat costs plenty of that already, gathered WET four at a time from a bank and dried ten
        // at a time through a peat stack with its own tool set. It is that a peat bank does not run
        // out. Firewood is limited by how fast trees grow back, so a colony's charcoal is capped by
        // its forest; peat is not capped by anything, which makes it the route for STABLE
        // production rather than the efficient one. Paying twice the input is what keeps it from
        // being simply better, and the 1/15 day against firewood's 1/30 keeps it slower as well.
        //
        // Kastuk asked for this, having played it: "peat reserve is endless, unlike long respawn of
        // firewood, so its useful for stable production even with less efficiency.
        //
        // What still separates them is time: 1/15 day against the firewood recipe's 1/30, so
        // peat is the slower fallback where trees are scarce - which is what the studio's own
        // wet-peat description says peat is for. Same kiln tool set and skill as the stock
        // recipe, so it appears alongside it with no new tools or structures required.
        makeCharcoalFromPeat = new ProcessType
        {
            Name = "Producing",
            KeyName = "makeCharcoalFromPeat",
            JobTypeKey = "craftingJobType",
            RequiredSkill = "bushcraft",
            PhysicalWorkFactor = 4f,
            WorkNeeded = WorkerNeededOptions.WorkerOnlyNeededToStart,
            Stances = ProcessLoader.GetToolMakingStances(),
            Inputs = new Input[1]
            {
                new Input
                {
                    Entity = "item:dryPeat",
                    IsConsumed = true,
                    Amount = new InputAmount
                    {
                        NoOfItems = 2
                    }
                }
            },
            Outputs = new Output[1]
            {
                new Output
                {
                    EntityTypeToCreate = "item:charcoal",
                    Amount = new OutputAmount
                    {
                        NoOfItems = 1
                    },
                    ToolContainerTagsToPlaceIn = new string[1] { "kiln" }
                }
            },
            WorkOrTimeNeeded = new WorkOrTime
            {
                DaysNeeded = 1f / 15f
            },
            ProcessToolSetKey = "toolSetKilnBigAndSmall",
            AgentAnimationStates = new AnimModifier[1] { AnimModifier.Improvised }
        };
        listOfProcessTypes.Add(makeCharcoalFromPeat);
    }

    /// <summary>
    /// The stock recipes that hand a structure its first load of fuel, and which name firewood by
    /// key to do it.
    /// </summary>
    private static readonly string[] FirewoodFuelledBuilds = new string[3]
    {
        "constructCampfire", "constructImprovisedKitchen", "constructMudbrickKitchen"
    };

    /// <summary>The peat variants added by <see cref="AddPeatConstruction"/>, for attainability.</summary>
    private static readonly List<ProcessType> peatBuilds = new List<ProcessType>();

    /// <summary>
    /// Adds a peat-fuelled variant of each build in <see cref="FirewoodFuelledBuilds"/>.
    ///
    /// WHY THESE THREE AND NOT A GENERAL RULE. In all three the firewood is not a material at all:
    /// it is consumed by the recipe and re-created as a waste product, which is the game's way of
    /// saying "the structure starts with one fuel in it". Every one of these structures burns the
    /// fuelForCampfire tag, and dry peat carries that tag with the same MaximumBulk as firewood -
    /// so a peat-loaded campfire burns exactly as long as a firewood-loaded one, and the variant
    /// is a fair swap rather than a discount. Nothing else in the stock tables has that shape.
    ///
    /// WHY 1 PEAT AND NOT 2, when the charcoal recipe charges two. There the peat is consumed and
    /// something else comes out; here the input IS the output. Charging two would burn a peat for
    /// nothing and read as a bug to anyone who looked at the numbers.
    ///
    /// WHY A CLONE RATHER THAN THREE COPIES OF THE STUDIO'S DATA. Their recipes carry tools, a
    /// skill, a work time, stances and animations, and the port has no business restating any of
    /// it - a restated copy is a copy that goes stale the next time the studio changes a number.
    /// The one field that differs is which item fills the fuel slot.
    /// </summary>
    private static void AddPeatConstruction(List<ProcessType> listOfProcessTypes)
    {
        peatBuilds.Clear();
        if (!PeatBuilding.On)
        {
            return;
        }

        foreach (string key in FirewoodFuelledBuilds)
        {
            ProcessType stock = listOfProcessTypes.Find(
                (ProcessType p) => p != null && p.KeyName == key && !p.DeleteRecord);
            if (stock == null || stock.Inputs == null || stock.Outputs == null)
            {
                // A scenario that deletes the recipe, or a game update that renamed it. Skipping is
                // right either way: a variant of a recipe that is not there would be a recipe the
                // scenario's designer removed, put back by the side door.
                continue;
            }

            Input[] inputs = SwapFirewoodForPeat(stock.Inputs);
            Output[] outputs = SwapFirewoodForPeat(stock.Outputs);
            if (inputs == null || outputs == null)
            {
                // No firewood in it after all - somebody else already changed this recipe. Leave it.
                continue;
            }

            ProcessType peat = new ProcessType(stock, key + "WithPeat", null)
            {
                Inputs = inputs,
                Outputs = outputs
            };
            listOfProcessTypes.Add(peat);
            peatBuilds.Add(peat);
        }
    }

    /// <summary>
    /// A copy of the inputs with firewood replaced by dry peat, or null if there was no firewood.
    /// Every element is copied rather than shared: the loader initialises these objects in place,
    /// and an object reachable from two ProcessTypes would be initialised twice.
    /// </summary>
    private static Input[] SwapFirewoodForPeat(Input[] original)
    {
        bool found = false;
        Input[] copy = new Input[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            Input from = original[i];
            bool isFirewood = from.Entity == "item:firewood";
            found |= isFirewood;
            copy[i] = new Input
            {
                Entity = isFirewood ? "item:dryPeat" : from.Entity,
                Tag = from.Tag,
                IsConsumed = from.IsConsumed,
                BecomesPartOfProduct = from.BecomesPartOfProduct,
                Amount = new InputAmount
                {
                    NoOfItems = from.Amount?.NoOfItems,
                    Substances = from.Amount?.Substances
                }
            };
        }
        return found ? copy : null;
    }

    /// <summary>The same for the outputs - the fuel the structure is handed when it is finished.</summary>
    private static Output[] SwapFirewoodForPeat(Output[] original)
    {
        bool found = false;
        Output[] copy = new Output[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            Output from = original[i];
            bool isFirewood = from.EntityTypeToCreate == "item:firewood";
            found |= isFirewood;
            copy[i] = new Output
            {
                EntityTypeToCreate = isFirewood ? "item:dryPeat" : from.EntityTypeToCreate,
                IsWasteProduct = from.IsWasteProduct,
                RelativePlacement = from.RelativePlacement,
                ToolContainerTagsToPlaceIn = from.ToolContainerTagsToPlaceIn,
                ToolContainerTypesToPlaceIn = from.ToolContainerTypesToPlaceIn,
                Amount = new OutputAmount
                {
                    NoOfItems = from.Amount?.NoOfItems,
                    Bulk = from.Amount?.Bulk
                }
            };
        }
        return found ? copy : null;
    }

    /// <summary>
    /// Called at the end of InventorySettings.EndRecomputeAttainability. Without this the new
    /// recipe works but is never offered, because attainability was computed from the tables as
    /// they stood before the addition.
    /// </summary>
    public static void RegisterAttainability(Dictionary<EntityType, Dictionary<ProcessType, AttainableInfo>> attainableInfo)
    {
        // Charcoal is a pre-existing item and already has an entry, from the stock firewood
        // recipe - so this adds the peat route INTO that entry rather than creating one. Without
        // it the recipe exists and works but is never offered in the production list, because
        // attainability was computed before the addition.
        if (charcoal != null && makeCharcoalFromPeat != null
            && attainableInfo.TryGetValue(charcoal, out Dictionary<ProcessType, AttainableInfo> charcoalRoutes))
        {
            if (!charcoalRoutes.ContainsKey(makeCharcoalFromPeat))
            {
                charcoalRoutes.Add(makeCharcoalFromPeat, Producable);
            }
        }

        // Same again for the peat-fuelled builds: the structures already have an entry, from the
        // stock recipe, and this adds the alternative route into it.
        foreach (ProcessType build in peatBuilds)
        {
            if (build.Outputs == null || build.Outputs.Length == 0)
            {
                continue;
            }
            EntityType structureType = build.Outputs[0].FinalEntityTypeToCreate;
            if (structureType != null
                && attainableInfo.TryGetValue(structureType, out Dictionary<ProcessType, AttainableInfo> routes)
                && !routes.ContainsKey(build))
            {
                routes.Add(build, Producable);
            }
        }
    }
}
