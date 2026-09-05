# 40-items-and-recipes

**Author:** community modder. Contributed as BepInEx / HarmonyLib patches and ported to direct
source edits for this build.

> **Note (2026-09-05):** `item:peatCharcoal` and its `makePeatCharcoal` recipe, originally part of
> this contribution, have been removed. A new `EntityType` key is named by saves, so a save holding
> one could not be loaded by a build without the mod — and the item could never be made into
> gunpowder or sold, because much of the game matches charcoal by key rather than by tag.
> `50-charcoal-from-peat` produces the real item instead, which does everything the separate one
> did without either cost. The smithy retag, which is the rest of this contribution, is unchanged.

> **This directory holds the port of that work; the source split is not finished yet.**
> The feature currently still lives in `newfiles/src/UnclaimedWorld/UWGame/Mods/UnhiddenMod.cs`
> as part of one combined file. See `features/README.md` for the target layout and
> `SPLIT-PLAN.md` for what remains.

Attribution and licensing for this feature are the contributor's to set. If they would like their
original `.cs` patch files included here alongside the ported source, this directory is where
they go.
