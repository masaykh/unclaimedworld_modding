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
| `40-items-and-recipes` | community modder | `item:peatCharcoal`, `makePeatCharcoal`, both smithies retagged to the `fuelForForge` tag |
| `50-charcoal-from-peat` | this port | `makeCharcoalFromPeat` — produces **real** `item:charcoal` from dry peat |

## Why 40 and 50 are separate

`40-` adds peat charcoal as a **distinct EntityType**, which burns in a forge but is not
interchangeable with charcoal: gunpowder takes `item:charcoal` as a literal input, and the price
and trade tables know nothing about peat charcoal. `50-` closes that by producing the real item,
1:1, and is a separate contribution by a separate author — hence a separate directory.

`50-` **requires** `40-`: it depends on `item:dryPeat` handling and the attainability
registration. Ordering is by filename prefix, so `40-` always applies first.

## Save compatibility

`40-` and `50-` add an `EntityType` and `ProcessType` to the tables the snapshot serializer
resolves against. **A save made with them may not load without them**, and one containing peat
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
