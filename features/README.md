# Features

Everything in `patches/` is the **platform port**: what it takes to make the game compile and run
on .NET 8 with MonoGame 3.8.5.1. It changes no gameplay.

Everything here is **optional on top of that** — one directory per feature, each independently
selectable, each attributed to whoever wrote it.

| Feature | Author | What it does |
|---|---|---|
| `10-mainmenu-fixes` | community modder | EDIT/TEST buttons for the map editor, user scenarios listed, saves sorted newest-first, `en-GB` culture |
| `20-more-controls` | community modder | `O` toggles shadows, mouse wheel scrolls data sheets |
| `30-ui-fixes` | community modder | stockpile categories sorted ascending |
| `40-items-and-recipes` | community modder | both smithies retagged to accept the `fuelForForge` tag rather than `item:charcoal` by key |
| `50-charcoal-from-peat` | this port | `makeCharcoalFromPeat` — **real** `item:charcoal` from dry peat — and peat-fuelled variants of the three builds that start with a fuel load |

## Why 40 and 50 are separate

`40-` was contributed by a community modder and `50-` written for this port, so they are separate
directories even though both are about peat.

`40-` originally added peat charcoal as a **distinct EntityType** alongside the retag. That item
has since been **removed** (2026-09-05): it burned in a forge but could never be made into
gunpowder or sold, because much of the game matches charcoal by key rather than by tag — and, worse,
a new EntityType key is named by saves, so a save containing one could not be loaded by a build
without the mod at all. `50-` produces the **real** item, which closes both problems, so the
separate item earned nothing and cost save compatibility. What survives in `40-` is the retag.

`50-` **requires** `40-`: it depends on `item:dryPeat` handling and the attainability
registration. Ordering is by filename prefix, so `40-` always applies first.

## Runtime switches

Since 2026-09-05 the content features are also switchable **without rebuilding**, in
`user/ModSettings.xml` or the MODS section of the in-game options menu:

| setting | what it controls |
|---|---|
| `unhidden.charcoalFromPeat` | the `makeCharcoalFromPeat` recipe (`50-`) |
| `unhidden.peatBuilding` | the peat-fuelled build variants (`50-`) |
| `unhidden.experimental` | a spare switch, wired to nothing |

A save records which of these were on when it was written, is marked **MODDED** in the save list,
and offers to load with the settings it was made with. See the main `README.md`.

## Save compatibility

`50-` adds `ProcessType`s to the tables the snapshot serializer resolves against. **A save made
with them may not load without them**, and one containing peat
charcoal certainly will not. `10-`, `20-` and `30-` are interface-only and carry no such risk.

## Structure

    features/<NN-name>/
      feature.json     id, order, requires, author
      README.md        what it does and why
      src/             the feature's own source files

The core patch series is unaffected by which features you select. The game's call sites into them
are one-line dispatches that compile away when a feature is absent — so features are added and
removed without touching `patches/` at all.

## Attribution

The four community features are translations of BepInEx / HarmonyLib patches contributed by a
modder. The original patch files are theirs; whether they appear here alongside the ported source
is their decision, and this layout leaves room for both.
