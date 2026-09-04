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
///     * item:peatCharcoal is added as a second forge fuel
///     * makePeatCharcoal is added as a craftable process
///     * structure:simpleSmithy and structure:improvisedSmithy are redefined to require the
///       "fuelForForge" tag instead of the literal item:charcoal, so either fuel works
///
/// The second group adds EntityTypes and ProcessTypes to the tables the snapshot serializer
/// resolves against. A save made with the mod on may not load with it off, and one containing
/// peat charcoal certainly will not. That is why this is switchable rather than unconditional.
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

    private static EntityType peatCharcoal;

    private static EntityType charcoal;

    private static ProcessType makePeatCharcoal;

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

        peatCharcoal = new EntityType("item:peatCharcoal")
        {
            Name = "Charcoal (Peat)",
            SummaryDescription = "Less traditional fuel type made from heat treated dried peat",
            Description = "Charcoal can provide a more intense heat than firewood and is often required for primitive metalworking. Making it from peat is quite inefficient due to low percent of carbon in material.",
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
        listOfEntityTypes.Add(peatCharcoal);
    }

    /// <summary>Called at the end of ProcessLoader.InitProcessTypes.</summary>
    public static void AddProcesses(List<ProcessType> listOfProcessTypes)
    {
        makePeatCharcoal = new ProcessType
        {
            Name = "Producing",
            KeyName = "makePeatCharcoal",
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
                        NoOfItems = 4
                    }
                }
            },
            Outputs = new Output[1]
            {
                new Output
                {
                    EntityTypeToCreate = "item:peatCharcoal",
                    Amount = new OutputAmount
                    {
                        NoOfItems = 2
                    },
                    ToolContainerTagsToPlaceIn = new string[1] { "kiln" }
                }
            },
            // Deliberately slower than ordinary charcoal.
            WorkOrTimeNeeded = new WorkOrTime
            {
                DaysNeeded = 1f / 20f
            },
            ProcessToolSetKey = "toolSetKilnBigAndSmall",
            AgentAnimationStates = new AnimModifier[1] { AnimModifier.Improvised }
        };

        // The patch also inserted into GameData.Instance.AllProcessTypes here. That is left to
        // the game: InitProcessTypes' caller registers everything the returned list holds, and
        // adding it in both places throws on the duplicate key.
        listOfProcessTypes.Add(makePeatCharcoal);

        // ------------------------------------------------------------------ charcoal from peat
        //
        // Peat as a source of REAL charcoal, not only of the mod's own peat charcoal.
        //
        // The reason this is a separate recipe rather than a change to the one above: peat
        // charcoal is a distinct EntityType, and a good deal of the game matches charcoal by
        // KEY rather than by tag. Gunpowder takes "item:charcoal" as a literal input
        // (ProcessLoader.cs:11565), and PricesProfileLoader and TradeGroupLoader both price and
        // trade "item:charcoal" while knowing nothing about peat charcoal. So peat charcoal
        // burns in a forge - the mod's retagging handles that - but cannot be turned into
        // gunpowder or sold, and any future consumer that names charcoal directly will refuse it
        // too. Producing the real item closes all of that at once, with no edits to the tables
        // that name it.
        //
        // Balance:
        //
        //   makePeatCharcoal      4 dry peat -> 2 peat charcoal, 1/20 day   (cheap bulk fuel)
        //   makeCharcoalFromPeat  1 dry peat -> 1 charcoal,      1/15 day   (real charcoal)
        //   makeCharcoal (stock)  1 firewood -> 1 charcoal,      1/30 day   (firewood route)
        //
        // 1:1 on purpose, matching the stock firewood conversion, because the cost of this route
        // is upstream rather than here. Dry peat is not a raw material: it is gathered as WET
        // peat four at a time from a peat bank (makeWetPeatFromBank) and then dried ten at a
        // time through a peat stack structure with its own tool set (makeDryPeat, ~0.1 day, and
        // 1:1 - drying loses nothing). Firewood is one step from a tree. Charging a further
        // multiple here taxed the same scarcity twice and made the route not worth taking.
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
                        NoOfItems = 1
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
    /// Called at the end of InventorySettings.EndRecomputeAttainability. Without this the new
    /// item is producible but never offered, because attainability was computed from the tables
    /// as they stood before the addition.
    /// </summary>
    public static void RegisterAttainability(Dictionary<EntityType, Dictionary<ProcessType, AttainableInfo>> attainableInfo)
    {
        if (peatCharcoal != null && makePeatCharcoal != null && !attainableInfo.ContainsKey(peatCharcoal))
        {
            attainableInfo.Add(peatCharcoal, new Dictionary<ProcessType, AttainableInfo>
            {
                { makePeatCharcoal, Producable }
            });
        }

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
    }
}
