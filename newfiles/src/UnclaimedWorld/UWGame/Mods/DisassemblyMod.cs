using System;
using System.Collections.Generic;
using UWGame.SimSide;
using UWGame.SimSide.Entities;
using UWGame.SimSide.Processes;

namespace UWGame.Mods;

/// <summary>
/// Disassembly, generated from the recipes that made the thing.
///
/// THE REPORT. "I see that there is action to Disassemble the object, but its rarely available
/// (just for technical things, like rifles). How can you add Disassembly of the metal tools, like
/// to recover metal material?" - Kastuk. Then: "Add this disassembling action for all complex
/// tools\weapons, furniture. Make it as separated optional mod for now", "Complex spears must be
/// available for disassembling too", and "Textile can be disassembled into cotton strings (special
/// disassembling recipe)".
///
/// WHY IT IS RARE, IN THE STUDIO'S DATA. Every disassembly is hand-written. An item gets one by
/// naming a process in <see cref="NonLivingType.SalvageProcess"/>, which
/// <see cref="NonLivingType.PostLoadContentInitialize"/> resolves to the
/// <see cref="ProcessType"/> it keys, and that process is an ordinary recipe carrying
/// <see cref="ProcessType.IsSalvageProcess"/> - input the object, outputs the parts. Thirty-three
/// items declare one (the guns, the sentries, the workshop upgrades, three toolboxes, two tapping
/// buckets, beds and mats). No knife, no machete, no hammer. Nobody decided tools should not come
/// apart; somebody stopped writing recipes.
///
/// WHAT THIS DOES INSTEAD. It reads the recipe that PRODUCES an item and turns it round:
/// <c>makeHammer</c> is 1 wrought iron + 1 sticks, so a hammer comes apart into 1 wrought iron +
/// 1 sticks. Nothing is typed in twice, and when the studio changes a recipe the disassembly
/// changes with it. <see cref="AddDisassembly"/> is the whole generator and it runs inside the
/// data load, at the end of <c>BaseDataLoader.InitProcessTypes</c> - the same hook
/// <see cref="UnhiddenMod.AddProcesses"/> uses, and late enough that
/// <c>GameData.Instance.AllEntityTypes</c> is already populated (EntityTypes is step 58 of the
/// <c>DataLoaderQueueState</c> queue, ProcessTypes step 67).
///
/// WHAT COUNTS AS COMPLEX, and why that is not a list I wrote. Kastuk asked for "complex" tools
/// and weapons, and the tables answer it exactly: an item whose production consumed TWO OR MORE
/// materials was assembled, and an item whose production consumed one was transformed.
///
///     makeHammer       1 wroughtIron + 1 sticks  -> hammer      assembled: both parts survive
///     makeIronSpear    1 waterCaneStem + 1 wroughtIron -> spear assembled: Kastuk's "complex spear"
///     makeFile         1 wroughtIron             -> file        forged from one piece
///     makeTextile      1 cotton                  -> textile     spun
///     makeClayPot...   1 unfiredClayPot          -> pot         fired
///
/// A file is one piece of iron beaten into shape; handing back the iron would be melting it down,
/// which is not what the Disassemble action means. So the one-input recipes are left alone unless
/// the exception table below says what they degrade into - which is where Kastuk's textile goes.
///
/// THE THREE RULES, which are the whole design:
///
///   default        give back what the recipe consumed          knife -> blister steel + sticks
///   degrades to    a material comes back as something lesser   cotton -> item:cottonString
///   destroyed      a material does not come back at all        clay, salt, and the fuels
///
/// NO NEW ITEMS. Kastuk suggested a "Wood scrap" item for the wooden half of a tool. It already
/// exists and is called <c>item:sticks</c> - literally what the recipe consumed - and sticks are
/// an input to about 45 recipes, so recovered wood is worth having rather than being a token that
/// only burns. A new EntityType key is also the one thing that makes a save unloadable by a build
/// without the mod, which is why <c>item:peatCharcoal</c> was deleted (see
/// <see cref="UnhiddenMod"/>). This mod adds ProcessType keys only.
///
/// SWITCHABLE AND SEPARATE, as asked: everything here hangs off <see cref="Generate"/>
/// (<c>disassembly.generate</c> in user/ModSettings.xml and in the MODS section of the options
/// menu). Off is the stock game exactly - no recipe is built, no item is touched.
/// </summary>
public static class DisassemblyMod
{
    /// <summary>The prefix its settings carry in the file and in save signatures.</summary>
    public const string ModId = "disassembly";

    private static ModSetting generate;

    /// <summary>
    /// Whether the generated disassembly recipes are in the tables.
    ///
    /// Marked as affecting the simulation because it is: each generated recipe is a ProcessType
    /// key, and a save holding a disassembly job in progress names the key. That is what the save
    /// stamp (<see cref="ModSettings.Signature"/>) is for - loading such a save into a build with
    /// this switched off is caught and offered the fix rather than failing in the loader.
    /// </summary>
    public static ModSetting Generate =>
        generate ?? (generate = ModSettings.Toggle(
            ModId, "generate", "DISASSEMBLE TOOLS AND WEAPONS", defaultValue: true,
            toolTip: "Gives every assembled tool, weapon and spear a Disassemble action that " +
                     "returns the materials its recipe consumed - a steel machete for its blister " +
                     "steel and sticks. Generated from the production recipes, so it stays right " +
                     "when they change. Things forged or fired from a single material are left " +
                     "alone; textile comes apart into cotton string.",
            affectsSimulation: true, takesEffectOnNextLoad: true));

    public static void RegisterSettings()
    {
        _ = Generate;
    }

    // ------------------------------------------------------------------------ the exception table
    //
    // Three entries of data instead of a hand-written recipe per item. This is the part that
    // needs judgement rather than the tables, and the part a modder should be editing.

    /// <summary>
    /// The categories whose items may be taken apart: <c>EntityType.CategoryKey</c> of tools,
    /// weapons and equipment.
    ///
    /// Everything else is excluded on purpose. Food and ingredients are eaten, ammunition is
    /// fired, carcasses rot, waste is waste - and MATERIALS (the largest category, 181 items) are
    /// the output of smelting, firing, drying and mixing, where "giving the inputs back" would be
    /// unburning a fire. The materials that SHOULD come apart are named in
    /// <see cref="AlsoConsider"/> one by one.
    /// </summary>
    private static readonly string[] Categories = { "tools", "weapons", "equipment" };

    /// <summary>
    /// Items admitted regardless of their category or of the two-material rule, because somebody
    /// decided they should come apart.
    ///
    /// <c>item:textile</c> is Kastuk's: "Textile can be disassembled into cotton strings (special
    /// disassembling recipe) ... it's not just reversed crafting (originally made from pure
    /// cotton)". He is right that it is not reversed crafting - <c>makeTextile</c> is 1 cotton ->
    /// 1 textile, and handing back cotton would be unspinning cloth into fluff. It works here
    /// because <see cref="DegradesTo"/> answers what cotton comes back AS.
    /// </summary>
    private static readonly string[] AlsoConsider = { "item:textile" };

    /// <summary>
    /// Materials that come back as something lesser, keyed by the material the recipe consumed.
    ///
    /// Per MATERIAL rather than per item, so one line covers every recipe that consumes it -
    /// textile today, and anything added later that is made of cotton. <c>item:cottonString</c> is
    /// the right target for two reasons: it exists, so no new key reaches a save, and
    /// <c>makeFishingNet</c> is currently the only thing in the game that consumes it.
    /// </summary>
    private static readonly Dictionary<string, string> DegradesTo = new Dictionary<string, string>
    {
        { "item:cotton", "item:cottonString" }
    };

    /// <summary>
    /// Materials that do not come back at all: burned, fired, or dissolved into what they made.
    ///
    /// The fuels are here because a fuel is gone. Clay and the unfired precursors are here because
    /// firing is not reversible - without them a glazed clay jar would come apart into its salt
    /// glaze and an unfired jar, which is the generator being clever about a kiln.
    ///
    /// Only consulted for items that got this far, so the list is short on purpose: it names what
    /// the recipes in scope actually consume rather than every material in the game.
    /// </summary>
    private static readonly string[] Destroyed =
    {
        "item:clay", "item:salt", "item:acetylene",
        "item:firewood", "item:wetFirewood", "item:dryPeat", "item:wetPeat", "item:charcoal",
        "item:unfiredClayJar", "item:unfiredClayPot", "item:unfiredBulletMold",
        "item:unfinishedFirebricks", "item:wetMudBrick"
    };

    /// <summary>
    /// The studio's own item disassembly, used as the shape to clone. Everything a recipe carries
    /// beyond its inputs and outputs - the stances, the physical work factor, the animation, the
    /// job type - comes from this rather than from numbers written here, so it is whatever theirs
    /// currently is and there is nothing to go stale when the game updates.
    /// </summary>
    private const string TemplateKey = "salvageKnifeSpear";

    /// <summary>The recipes this generated, kept for <see cref="Generated"/>.</summary>
    private static readonly List<ProcessType> generated = new List<ProcessType>();

    /// <summary>What was generated on the last data load, in key order. For tools and tests.</summary>
    public static IReadOnlyList<ProcessType> Generated => generated;

    /// <summary>The entity table, as the loaders built it. See <see cref="CaptureItems"/>.</summary>
    private static List<EntityType> entityTypes;

    /// <summary>
    /// Called at the end of <c>BaseDataLoader.InitEntityTypes</c>, to hold on to the list the
    /// entity loaders just built.
    ///
    /// WHY NOT READ <c>GameData.Instance.AllEntityTypes</c> INSTEAD. That collection is filled by
    /// <c>DataLoader.SerializeAndDeserializeTypeList</c>, which serializes the table BEFORE it
    /// calls <c>InitTypeList</c> to fill it - and entityTypes.xml is one of the 13 tables that
    /// cannot serialize (see MODDING.md), so in an exporting run the collection is still empty
    /// when the process table is built. Reading it would make this mod work in the game and
    /// produce nothing under tools/DataExport, which is the one place it can be checked without
    /// launching. The list the loaders built is the same objects in both modes.
    /// </summary>
    public static void CaptureItems(List<EntityType> listOfEntityTypes)
    {
        entityTypes = listOfEntityTypes;
    }

    /// <summary>
    /// Called at the end of <c>BaseDataLoader.InitProcessTypes</c>, with the process table the
    /// game is about to use and the entity table it already built.
    ///
    /// Adds a disassembly recipe per qualifying item and points the item at it through
    /// <see cref="NonLivingType.SalvageProcess"/> - the studio's own mechanism, resolved later by
    /// <see cref="NonLivingType.PostLoadContentInitialize"/>, which is what makes the DISASSEMBLE
    /// button appear on the object (<c>EntityType.CanBeSalvagedDirectly</c>, read by
    /// <c>HUDEntityContextMenu.RefreshEntityContent</c>).
    /// </summary>
    public static void AddDisassembly(List<ProcessType> listOfProcessTypes)
    {
        generated.Clear();
        if (!Generate.On || listOfProcessTypes == null || entityTypes == null)
        {
            return;
        }

        ProcessType template = FindTemplate(listOfProcessTypes);
        if (template == null)
        {
            // The studio's item disassembly is gone - renamed, or deleted by a scenario. Inventing
            // a shape for it here would be inventing stances and work factors nobody asked for, so
            // the mod does nothing instead. Every guard in this file is the same decision.
            return;
        }

        Dictionary<string, ProcessType> madeBy = MapProductionRecipes(listOfProcessTypes);

        // Sorted so that two runs of the same build produce the same table in the same order,
        // which is what makes an exported processTypes.xml diffable.
        List<EntityType> candidates = new List<EntityType>();
        foreach (EntityType item in entityTypes)
        {
            if (Qualifies(item))
            {
                candidates.Add(item);
            }
        }
        candidates.Sort((EntityType a, EntityType b) => string.CompareOrdinal(a.KeyName, b.KeyName));

        foreach (EntityType item in candidates)
        {
            if (!madeBy.TryGetValue(item.KeyName, out ProcessType production))
            {
                continue;
            }

            ProcessType disassembly = Build(item, production, template);
            if (disassembly == null)
            {
                continue;
            }

            listOfProcessTypes.Add(disassembly);
            generated.Add(disassembly);

            // The link the game reads. Set as a KEY rather than as the resolved ProcessType,
            // because that is what the studio's own data does and it means one code path resolves
            // both - PostLoadContentInitialize looks it up in AllProcessTypes, which this recipe
            // is now in.
            item.NonLivingType.SalvageProcess = disassembly.KeyName;
        }
    }

    /// <summary>
    /// Whether an item is a candidate at all - before its recipe is looked at.
    ///
    /// The <see cref="NonLivingType.SalvageProcess"/> test is the important one: an item the
    /// studio already gave a disassembly keeps theirs. A generated recipe is a fallback for what
    /// nobody wrote, never a replacement for what somebody did.
    /// </summary>
    private static bool Qualifies(EntityType item)
    {
        if (item == null || item.ItemType == null || item.NonLivingType == null
            || item.StructureType != null || item.Upgrader != null || item.DeleteRecord)
        {
            // Structures are excluded because the studio already covers them - 111 salvage recipes,
            // one per building - and an Upgrader is installed into something else rather than
            // standing on its own, which is why CanBeSalvagedDirectly refuses it too.
            return false;
        }
        if (!string.IsNullOrEmpty(item.NonLivingType.SalvageProcess))
        {
            return false;
        }
        return Contains(Categories, item.CategoryKey) || Contains(AlsoConsider, item.KeyName);
    }

    /// <summary>
    /// The recipe that makes each item: the FIRST production recipe that yields it as a
    /// non-waste output.
    ///
    /// First rather than best, deliberately. Where an item has two routes the studio's own comes
    /// first in the table, so the disassembly describes the way the game intends the thing to be
    /// made, and a mod adding an alternative route later cannot silently change what taking it
    /// apart gives back.
    /// </summary>
    private static Dictionary<string, ProcessType> MapProductionRecipes(List<ProcessType> processTypes)
    {
        Dictionary<string, ProcessType> madeBy = new Dictionary<string, ProcessType>();
        foreach (ProcessType process in processTypes)
        {
            if (process == null || process.DeleteRecord || process.Outputs == null
                || process.Inputs == null || process.IsGathering || !process.IsPartOfProductionChain())
            {
                // IsPartOfProductionChain excludes salvage, consuming, extraction, replenishing and
                // repair in one test - all the ways a process can name an output without being the
                // recipe that assembles it.
                continue;
            }
            foreach (Output output in process.Outputs)
            {
                if (output == null || output.IsWasteProduct || output.EntityTypeToCreate == null)
                {
                    continue;
                }
                if (!madeBy.ContainsKey(output.EntityTypeToCreate))
                {
                    madeBy.Add(output.EntityTypeToCreate, process);
                }
            }
        }
        return madeBy;
    }

    /// <summary>
    /// Turns one production recipe round, or returns null if there is nothing honest to hand back.
    ///
    /// THE BATCH. A recipe's output amount is how many the batch makes, and the disassembly takes
    /// ONE object - the salvage machinery is built around a single entity
    /// (<c>Salvage.CreateSalvageJob</c> assigns exactly one as the job's immovable input), so the
    /// recovered amounts are divided by the batch and rounded DOWN. <c>makeSteelKnife</c> is the
    /// one recipe in scope where that bites: 1 blister steel + 1 sticks makes TWO knives, so one
    /// knife is half of each, and half of a blister steel is not a thing to hand anybody. It gets
    /// no recipe rather than a rounded-up one, because rounding up would let a colony make two
    /// knives from one steel and take back two steels.
    ///
    /// THE TWO-MATERIAL RULE. A recipe that consumed one material was a transformation, not an
    /// assembly, so it only yields a recipe when <see cref="DegradesTo"/> says what that material
    /// comes back as. That is the difference between a hammer and a file, and it is read out of
    /// the data rather than out of a list of item names.
    /// </summary>
    private static ProcessType Build(EntityType item, ProcessType production, ProcessType template)
    {
        int batch = OutputAmountOf(production, item.KeyName);
        if (batch < 1)
        {
            return null;
        }

        List<Output> recovered = new List<Output>();
        int materials = 0;
        bool degraded = false;
        bool lossy = false;

        foreach (Input input in production.Inputs)
        {
            if (input == null || input.Entity == null || !input.IsConsumed)
            {
                // A tool, or the stones a campfire stands on: not consumed, so it was never
                // destroyed and there is nothing to recover.
                continue;
            }
            materials++;

            if (Contains(Destroyed, input.Entity))
            {
                lossy = true;
                continue;
            }

            string entity = input.Entity;
            if (DegradesTo.TryGetValue(entity, out string lesser))
            {
                entity = lesser;
                degraded = true;
                lossy = true;
            }
            if (entity == item.KeyName)
            {
                // The recipe that turns a thing into more of itself - makeCottonString is 3 cotton
                // -> 3 cotton string, and cotton degrades to cotton string. Handing the item back
                // for itself is a free action, so there is no recipe here.
                continue;
            }

            int amount = (input.Amount?.NoOfItems ?? 1) / batch;
            if (amount < 1)
            {
                lossy = true;
                continue;
            }
            recovered.Add(new Output
            {
                EntityTypeToCreate = entity,
                IsWasteProduct = false,
                Amount = new OutputAmount { NoOfItems = amount }
            });
        }

        if (recovered.Count == 0 || (materials < 2 && !degraded))
        {
            return null;
        }

        // A clone of the studio's item disassembly with the inputs, outputs, skill and time
        // replaced. Everything not named here - stances, physical work factor, animation, whether
        // a worker must stay for it - is theirs.
        ProcessType disassembly = new ProcessType(template, item.KeyName + "_disassemble", null)
        {
            Name = "Disassemble",
            SummaryDescription = lossy
                ? "When breaking this object apart, some parts will be retrieved and some may be lost."
                : "This object can be disassembled without losing any parts.",
            RequiredSkill = production.RequiredSkill ?? template.RequiredSkill,
            IsSalvageProcess = true,
            Inputs = new Input[1]
            {
                // No IsConsumed, matching the studio's salvage recipes: the object is destroyed by
                // the salvage job rather than consumed as a material.
                new Input
                {
                    Entity = item.KeyName,
                    Amount = new InputAmount { NoOfItems = 1 }
                }
            },
            Outputs = recovered.ToArray(),
            WorkOrTimeNeeded = new WorkOrTime { DaysNeeded = TimeToTakeApart(production, batch, template) }
        };
        return disassembly;
    }

    /// <summary>
    /// Half the time one of them took to make.
    ///
    /// Not a number out of the air: the studio's own generated processes are scaled from the
    /// production recipe the same way (<c>RepairType.CreateRepairProcess</c> divides the
    /// production time by the parts count, or by three for integrity), and scaling means the
    /// disassembly of an expensive thing stays expensive when the studio reprices it. Halved
    /// because taking apart is quicker than making, and divided by the batch because the recipe's
    /// time is for the whole batch while this takes one object apart.
    ///
    /// Falls back to the template's own time if the recipe measures itself in seconds rather than
    /// days - WorkOrTime.Initialize converts one to the other, and it has not run yet at data-load
    /// time.
    /// </summary>
    private static float TimeToTakeApart(ProcessType production, int batch, ProcessType template)
    {
        float? days = production.WorkOrTimeNeeded?.DaysNeeded;
        if (!days.HasValue)
        {
            float? seconds = production.WorkOrTimeNeeded?.TimeInSecondsNeeded;
            if (seconds.HasValue)
            {
                days = (float)(seconds.Value / DateAndTime.secondsPerDay);
            }
        }
        if (!days.HasValue || days.Value <= 0f)
        {
            return template.WorkOrTimeNeeded?.DaysNeeded ?? (1f / 150f);
        }
        return days.Value / (float)batch / 2f;
    }

    /// <summary>
    /// How many of one item a recipe's batch yields, ignoring waste products - or 0 if there is
    /// nothing safe to divide by.
    ///
    /// A recipe with a SECOND non-waste product is refused, and that is not fussiness: its
    /// materials paid for both products, so handing all of them back for one of them would be a
    /// duplicator. No stock recipe in scope has one - every one of the 21 makes a single thing -
    /// but a mod's recipe easily could, and the generator reads whatever is in the table.
    /// </summary>
    private static int OutputAmountOf(ProcessType production, string itemKey)
    {
        int amount = 0;
        int products = 0;
        foreach (Output output in production.Outputs)
        {
            if (output == null || output.IsWasteProduct)
            {
                continue;
            }
            products++;
            if (output.EntityTypeToCreate == itemKey)
            {
                amount = output.Amount?.NoOfItems ?? 1;
            }
        }
        return products == 1 ? amount : 0;
    }

    /// <summary>
    /// The studio's item disassembly to clone, by key, falling back to any of theirs that takes an
    /// item apart. A structure's salvage is not a substitute - those carry digging and
    /// packing-down stances for a building being taken down on its site.
    /// </summary>
    private static ProcessType FindTemplate(List<ProcessType> processTypes)
    {
        ProcessType fallback = null;
        foreach (ProcessType process in processTypes)
        {
            if (process == null || process.DeleteRecord || !process.IsSalvageProcess
                || process.Inputs == null || process.Inputs.Length == 0
                || process.Inputs[0]?.Entity == null
                || !process.Inputs[0].Entity.StartsWith("item:", StringComparison.Ordinal))
            {
                continue;
            }
            if (process.KeyName == TemplateKey)
            {
                return process;
            }
            if (fallback == null)
            {
                fallback = process;
            }
        }
        return fallback;
    }

    private static bool Contains(string[] keys, string key)
    {
        if (key == null)
        {
            return false;
        }
        foreach (string candidate in keys)
        {
            if (candidate == key)
            {
                return true;
            }
        }
        return false;
    }
}
