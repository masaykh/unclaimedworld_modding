# Splitting the combined mod into per-feature source

The five features above are currently one file: `newfiles/src/UnclaimedWorld/UWGame/Mods/
UnhiddenMod.cs` (plus `UnhiddenModSmithies.cs`), selected by a single `UwUnhiddenMod` build flag.
This is the plan for separating them, recorded so it can be picked up cleanly.

## The key finding: the patch series does not change at all

`UnhiddenMod.cs` is a **new file**, shipped in `newfiles/`, not a patch. And the game's eleven
call sites into it are one-line dispatches already guarded by `if (UnhiddenMod.Enabled)`, with
`UnhiddenMod.Absent.cs` supplying a `const bool Enabled = false` stub so the guarded blocks
compile away entirely.

So splitting the mod is a **source split, not a diff split**. `patches/UnclaimedWorld.patch` and
the eleven dispatch hunks inside it are untouched. That removes the failure mode this restructure
was originally going to risk: five patches that each apply alone but not in combination.

## What to do

1. Turn `UnhiddenMod` into a **dispatcher** that is always compiled, with each method guarded by
   `#if UW_FEATURE_<NAME>` and delegating to the feature's own class.

2. Move each feature's logic into `features/<NN-name>/src/UWGame/Mods/<Name>.cs`.

3. The one method needing care is `AllScenarioHeaders()`, because it **returns a value** and the
   caller cannot handle null. When `10-mainmenu-fixes` is absent the dispatcher must return the
   stock list itself:

   ```csharp
   public static List<Scenario> AllScenarioHeaders()
   {
   #if UW_FEATURE_MAINMENUFIXES
       return MainMenuFixes.AllScenarioHeaders();
   #else
       return (from s in RGScenarioLoader.LoadAllScenarioHeaders()
               orderby s.SortOrder select s).ToList();
   #endif
   }
   ```

   Every other dispatch (`ApplyCulture`, `AddItems`, `AddProcesses`, `RegisterAttainability`) is
   void and can simply no-op. `ShadowsDisabled` stays on the dispatcher: nothing sets it when
   `20-more-controls` is absent, so it stays false.

4. Five build flags in `UnclaimedWorld.csproj`, one per feature, replacing `UwUnhiddenMod`. Same
   two-boolean-per-flag interface as `UwHarmony` — **not** a list property, because the dotnet
   CLI claims both `;` and `,` as its own separators and fails with `MSB1006`.

5. Feature discovery in the orchestrator: `uwkit` reads `features/*/feature.json`, honours
   `requires`, and passes the flags. Same in `scripts/build-port.ps1`.

6. Verify the combinations that matter rather than all 2^5: none, all, each alone, and `40-`+`50-`
   together (the only pair with a real dependency).

## Ordering and dependencies

`50-charcoal-from-peat` requires `40-items-and-recipes` — it depends on `item:dryPeat` handling
and the attainability registration. `feature.json` declares that so applying `50-` alone fails
with a sentence rather than a compile error. Filename prefixes give the apply order.
