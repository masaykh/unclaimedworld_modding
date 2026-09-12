# 60-disassembly

**Author:** this port. **Requires:** nothing. **Affects save compatibility.**
**Switch:** `disassembly.generate` (on by default).

Gives every *assembled* tool, weapon and spear a **Disassemble** action that returns the materials
its own recipe consumed — 21 recipes in the stock tables, none of them written down.

Asked for by Kastuk: *"there is action to Disassemble the object, but its rarely available (just
for technical things, like rifles). How can you add Disassembly of the metal tools, like to recover
metal material?"*

## Why it was rare

Every disassembly in the game is **hand-written**. An item gets one by naming a process in
`NonLivingType.SalvageProcess`, which `NonLivingType.PostLoadContentInitialize` resolves against
`GameData.AllProcessTypes`, and that process is an ordinary `ProcessType` carrying
`IsSalvageProcess`. Exactly **33 items** declare one — the guns, the sentries, the workshop
upgrades, three toolboxes, two tapping buckets, beds and mats. No knife, no machete, no hammer.
Nobody decided tools should not come apart; somebody stopped writing recipes.

## How this works instead

`DisassemblyMod.AddDisassembly` (in `UWGame/Mods/DisassemblyMod.cs`, hooked at the end of
`BaseDataLoader.InitProcessTypes`) reads the recipe that **produces** an item and turns it round:

    makeHammer        1 wroughtIron + 1 sticks   -> hammer
    hammer disassembly    hammer -> 1 wroughtIron + 1 sticks

So no amount is typed in twice, and a recipe the studio reprices takes its disassembly with it.
Each generated recipe is a **clone** of the studio's own `salvageKnifeSpear`, so the stances, the
physical work factor and the animation stay theirs; only the inputs, outputs, skill (inherited from
the production recipe — smithing for a forged tool) and time (half the per-item production time)
are set. The link the player sees is the studio's own path: `EntityType.CanBeSalvagedDirectly`,
read by `HUDEntityContextMenu.RefreshEntityContent`.

## What "complex" means, read out of the data

An item whose production consumed **two or more materials** was assembled. An item whose production
consumed one was **transformed** — forged, fired, dried or spun — and handing the material back
would be melting it down rather than taking it apart.

    makeHammer      1 wroughtIron + 1 sticks       -> assembled, both parts survive
    makeIronSpear   1 waterCaneStem + 1 wroughtIron -> assembled, a "complex spear"
    makeFile        1 wroughtIron                   -> one piece of iron, beaten to shape
    makeTextile     1 cotton                        -> spun

Three rules, and the last two are one line of data each:

| rule | meaning | example |
|---|---|---|
| default | give back what the recipe consumed | `item:steelMachete` -> 1 blister steel + 1 sticks |
| *degrades to* | a material comes back as something lesser, per **material** | `item:cotton` -> `item:cottonString` |
| *destroyed* | burned, fired or dissolved; does not come back | clay, salt, the fuels, the unfired precursors |

**Textile** is Kastuk's own example — *"it's not just reversed crafting (originally made from pure
cotton)"* — and it is exactly right: unspinning cloth into fluff is not disassembly. It comes apart
into **1 cotton string**, via the degradation rule, and `item:cottonString` was chosen because it
already exists (a new EntityType key is the one thing that makes a save unloadable without the mod)
and because `makeFishingNet` is currently the only thing in the game that consumes it.

The *destroyed* row is what keeps a glazed clay jar out: `makeGlazedClayJar` has two inputs, so the
complexity rule alone would have taken a fired jar apart into its salt glaze and its unfired self.

## The three deliberate refusals

* An item the studio already gave a disassembly keeps **theirs**. This is a fallback for what
  nobody wrote, never a replacement.
* **`item:steelKnife` gets none.** `makeSteelKnife` yields *two* knives for 1 blister steel + 1
  sticks, so one knife is half of each; recoveries divide by the batch and round **down**, because
  rounding up would let a colony make two knives from one steel and take two steels back. A salvage
  job takes one object at a time (`Salvage.CreateSalvageJob` assigns exactly one as the job's
  immovable input), so "disassemble two at once" is not available as a fix.
* A production recipe with a **second non-waste product** is refused, for the same reason: its
  materials paid for both. No stock recipe in scope has one; a mod's easily could.

**No "wood scrap" item.** The wooden half of a tool is `item:sticks` — literally what the recipe
consumed, and an input to about 45 recipes, so recovered wood is worth having rather than being a
token that only burns.

## Two port fixes come with it - the same bug, in two places

The studio reads a salvaged item's `Parts` list without checking it for null. All 33 items they
gave a disassembly to also declare their `PartKeys`, so neither of these can fire in the stock
game, and both fire on the first generated recipe.

* `ProcessType.GetIsSalvageWithoutWaste` answers `false` for an item with no parts list now;
  callers only choose a caption by it (PACKING DOWN against BEGIN).
* `ProcessType.PostDataCompleteValidate` walked the same list to check each output against it, and
  this one was **fatal** - the tables built correctly and the game died on the next screen, inside
  the validation pass `Sim.QueueGameDataAndSimInit` runs. Reported by Kastuk on 2026-09-12, fixed
  the same day. The check is skipped when the item declares no parts: the rule asks whether the
  outputs match the declared parts, and an item with none is outside the rule rather than failing
  it. What a generated disassembly gives back is decided by the recipe that made the item.

The second one is why `dataexport --disassembly` now runs `GameData.PostDataCompleteInitialize` -
the pass that crashed - before it prints anything. Building the tables and surviving them are
different things, and the tool used to stop at the first.

## Verified, with no launch

`tools/dataexport <game-dir> --disassembly` loads the tables the way the *game* does and prints
every generated recipe:

    item:hammer_disassemble        from=item:hammer        skill=smithing  link=ok  salvage=yes  out=item:wroughtIron x1, item:sticks x1
    item:ironSpear_disassemble     from=item:ironSpear     skill=smithing  link=ok  salvage=yes  out=item:waterCaneStem x1, item:wroughtIron x1
    item:steelMachete_disassemble  from=item:steelMachete  skill=smithing  link=ok  salvage=yes  out=item:blisterSteel x1, item:sticks x1
    item:textile_disassemble       from=item:textile       skill=weaving   link=ok  salvage=yes  out=item:cottonString x1

It loads **without** exporting on purpose: the recipes are computed from the entity table, and
`entityTypes.xml` is one of the tables that cannot serialize, so an exporting run sees it
half-built. `build/80-verify-modloader.sh` case 12 asserts against that output — every recipe
linked from its item and marked as salvage, each recovery compared against the *production recipe
it came from* rather than against numbers in the test, the three exclusions above, and that the
switch off leaves the studio's tables untouched.
