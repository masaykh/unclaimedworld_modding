# 50-charcoal-from-peat

**Author:** this port. **Requires:** `40-items-and-recipes`. **Affects save compatibility.**

Adds `makeCharcoalFromPeat`: **1 dry peat -> 1 real `item:charcoal`**, 1/15 day, bushcraft, in a
kiln.

## Why it exists separately from 40

`40-` adds `item:peatCharcoal` as a distinct EntityType and retags both smithies to accept the
`fuelForForge` tag, so peat charcoal burns in a forge. But much of the game matches charcoal by
**key**, not by tag:

* gunpowder takes `item:charcoal` as a literal input (`ProcessLoader.cs:11565`)
* `PricesProfileLoader` and `TradeGroupLoader` price and trade `item:charcoal` and know nothing
  about peat charcoal

So under `40-` alone, peat charcoal cannot be made into gunpowder or sold, and any future
consumer that names charcoal directly refuses it too. This produces the real item, which closes
all of that without editing a single table that names charcoal.

## Balance

| recipe | in | out | time |
|---|---|---|---|
| `makeCharcoal` (stock) | 1 firewood | 1 charcoal | 1/30 day |
| `makePeatCharcoal` (40-) | 4 dry peat | 2 peat charcoal | 1/20 day |
| `makeCharcoalFromPeat` | 1 dry peat | 1 **charcoal** | 1/15 day |

1:1 on purpose, matching the stock firewood conversion, because peat's cost is **upstream**: dry
peat is gathered as *wet* peat four at a time from a bank (`makeWetPeatFromBank`), then dried ten
at a time through a peat stack structure with its own tool set (`makeDryPeat`, ~0.1 day, itself
1:1). Firewood is one step from a tree.

An earlier version charged 6 peat per charcoal. That taxed the same scarcity twice and left the
route not worth taking. What still separates the two is time — 1/15 day against firewood's 1/30 —
so peat remains the slower fallback where trees are scarce, which is what the studio's own
wet-peat description says peat is for.

## Verified

Read back out of the game's own exported tables via `tools/DataExport`:

    makeCharcoalFromPeat   item:dryPeat x1 -> item:charcoal x1   days=0.06666667

Exporting with and without it shows `processTypes.xml` as the only differing table, and the count
of tables that fail to export is unchanged — so it introduces no new failures.
