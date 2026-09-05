# 50-charcoal-from-peat

**Author:** this port. **Requires:** `40-items-and-recipes`. **Affects save compatibility.**

Adds `makeCharcoalFromPeat`: **2 dry peat -> 1 real `item:charcoal`**, 1/15 day, bushcraft, in a
kiln — and peat-fuelled variants of the three stock recipes that hand a structure its first load
of fuel. Both are switchable at runtime: `unhidden.charcoalFromPeat` and `unhidden.peatBuilding`.

## Why it exists separately from 40

`40-` retags both smithies to accept the `fuelForForge` tag, so any tagged fuel burns in a forge.
It used to add `item:peatCharcoal` as a distinct EntityType as well; that item was **removed** on
2026-09-05, because much of the game matches charcoal by **key**, not by tag:

* gunpowder takes `item:charcoal` as a literal input (`ProcessLoader.cs:11565`)
* `PricesProfileLoader` and `TradeGroupLoader` price and trade `item:charcoal` and know nothing
  about peat charcoal

So peat charcoal could not be made into gunpowder or sold, and any future consumer naming charcoal
directly would have refused it too — and a new EntityType key is named by saves, so a save holding
one could not be loaded by a build without the mod. This produces the real item, which closes all
of that without editing a single table that names charcoal, and made the separate item redundant.

## Balance

| recipe | in | out | time |
|---|---|---|---|
| `makeCharcoal` (stock) | 1 firewood | 1 charcoal | 1/30 day |
| `makeCharcoalFromPeat` | 2 dry peat | 1 **charcoal** | 1/15 day |

**2:1**, at the request of the modder who plays it: peat costs plenty of work upstream — gathered
*wet* four at a time from a bank (`makeWetPeatFromBank`), dried ten at a time through a peat stack
with its own tool set (`makeDryPeat`, ~0.1 day, itself 1:1) — but **a peat bank does not run out**.
Firewood is capped by how fast trees grow back, so a colony's charcoal is capped by its forest;
peat is capped by nothing. That makes it the route for *stable* production rather than the
efficient one, and paying double is what stops it being simply better. The 1/15 day against
firewood's 1/30 keeps it slower as well.

Earlier versions charged 6 peat, then 1. Six taxed the same scarcity twice and left the route not
worth taking; one made it strictly better than firewood on an unlimited resource.

### Building with peat

The three stock recipes that give a structure its first fuel — `constructCampfire`,
`constructImprovisedKitchen`, `constructMudbrickKitchen` — each consume 1 firewood and re-create it
as a waste product. That is a fuel load, not a material cost. All three burn the `fuelForCampfire`
tag, and `item:dryPeat` carries that tag with the same `MaximumBulk` as firewood, so
`constructCampfireWithPeat` and its two siblings take **1 dry peat** and burn for exactly as long.
They are cloned from the studio's recipes at load time with only the fuel slot swapped, so tools,
skill, work time and animations stay whatever the studio's are.

## Verified

Read back out of the game's own exported tables via `tools/DataExport`:

    makeCharcoalFromPeat   item:dryPeat x2 -> item:charcoal x1   days=0.06666667

Exporting with and without it shows `processTypes.xml` as the only differing table, and the count
of tables that fail to export is unchanged — so it introduces no new failures.

`build/80-verify-modloader.sh` pins all of it offline, with no launch: the 2, the three build
variants compared against the recipes they were cloned from, that switching each setting off
removes only its own content, and that `item:peatCharcoal` and `makePeatCharcoal` are gone from
both the tables and the compiled assembly.
