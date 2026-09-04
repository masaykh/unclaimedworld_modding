using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace UWGame.Mods;

/// <summary>
/// Loads third-party mods from <c>user/Mods/*.dll</c> and applies their HarmonyLib patches.
///
/// The game already looked for a <c>user/Mods</c> folder - <see cref="Config.DataType.UserMods"/>
/// has always resolved to it - but nothing ever read from it. This is the other half.
///
/// WHY A LOADER IN THE GAME RATHER THAN BEPINEX. BepInEx 5 targets .NET Framework / Mono and
/// cannot inject into a .NET 8 process at all; BepInEx 6's CoreCLR loader is pre-release. Both
/// exist to solve a problem this port does not have: getting code into a process you cannot
/// rebuild. We can rebuild, so the loader is thirty lines in the game's own startup and needs no
/// external injector, no doorstop, and no patched runtime configuration.
///
/// WHAT A MOD LOOKS LIKE. A class library targeting <c>net8.0-windows</c> that references
/// <c>UnclaimedWorld.dll</c> and <c>0Harmony.dll</c> from the game folder, containing ordinary
/// Harmony patch classes:
///
///     [HarmonyPatch(typeof(ItemLoader), "Init")]
///     internal static class MyPatch
///     {
///         private static void Postfix(List&lt;EntityType&gt; listOfEntityTypes) { ... }
///     }
///
/// Reference Harmony but do NOT ship 0Harmony.dll: the game ships exactly one copy. Two Harmony
/// assemblies in one process keep separate patch state over the same methods, and the resulting
/// misbehaviour is close to undebuggable.
///
/// ORDERING. Mods load in filename order, before any game data is read, so patches on the data
/// loaders take effect on the first load. Where two mods patch the same method, Harmony's own
/// priority attributes decide - this loader does not attempt to order them beyond the filename.
///
/// FAILURE IS PER-MOD. A mod that throws while loading or patching is skipped, reported to
/// Errors.txt, and does not prevent the others or the game from starting. A broken mod should
/// cost the player that mod, not their session.
/// </summary>
public static class ModLoader
{
    private static readonly List<string> LoadedMods = new List<string>();

    private static readonly List<string> FailedMods = new List<string>();

    private static bool hasRun;

    /// <summary>Mod assembly names that loaded and patched successfully, in load order.</summary>
    public static IReadOnlyList<string> Loaded => LoadedMods;

    /// <summary>Mod file names that failed, with the reason appended.</summary>
    public static IReadOnlyList<string> Failed => FailedMods;

    /// <summary>
    /// Whether third-party mods are loaded at all. Cleared by <c>-nomods</c>, which is documented
    /// as giving the stock game and would be a lie if it left external patches running.
    /// </summary>
    public static bool Enabled { get; private set; } = true;

    public static void Disable()
    {
        Enabled = false;
    }

    /// <summary>
    /// Scans the mods folder and applies every mod's patches. Safe to call more than once; only
    /// the first call does anything, because Harmony would otherwise apply each patch twice.
    /// </summary>
    /// <param name="log">
    /// Where to report progress and failures. The game passes
    /// <c>GameStateManagement.UnclaimedWorld.LogError</c> so problems land in Errors.txt, which
    /// is where a player already looks; tools pass their own console writer.
    /// </param>
    public static void LoadAll(Action<string, string> log = null)
    {
        if (hasRun || !Enabled)
        {
            return;
        }
        hasRun = true;

        string modsFolder;
        try
        {
            modsFolder = Config.GetDataFolderPath(Config.DataType.UserMods);
        }
        catch (Exception ex)
        {
            Report(log, "Mod loader", "Could not resolve the mods folder: " + ex.Message);
            return;
        }

        if (!Directory.Exists(modsFolder))
        {
            // Not an error, and deliberately not created: an empty folder in every install
            // suggests the game wants something put in it.
            return;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(modsFolder, "*.dll", SearchOption.TopDirectoryOnly);
            Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Report(log, "Mod loader", "Could not list " + modsFolder + ": " + ex.Message);
            return;
        }

        foreach (string path in candidates)
        {
            string fileName = Path.GetFileName(path);

            // 0Harmony.dll dropped in alongside a mod is the single most common packaging
            // mistake, and loading a second copy of it is worse than doing nothing. Skip it
            // quietly rather than trying to patch through it.
            if (string.Equals(fileName, "0Harmony.dll", StringComparison.OrdinalIgnoreCase))
            {
                Report(log, "Mod loader",
                    "Skipped " + fileName + " in the mods folder. The game already ships Harmony; " +
                    "a mod should reference it without copying it. Delete this file.");
                continue;
            }

            try
            {
                // LoadFrom, not LoadFile: the mod's references to UnclaimedWorld.dll,
                // MonoGame.Framework.dll and 0Harmony.dll must resolve to the copies already
                // loaded in this process, not to second instances. LoadFrom uses the default
                // load context, which is what makes that happen.
                Assembly assembly = Assembly.LoadFrom(path);

                // PatchAll(assembly) rather than PatchAll(): the parameterless overload takes the
                // CALLING assembly, which here is the game, so it would scan the game for patch
                // attributes and find none of the mod's.
                new Harmony(assembly.GetName().Name).PatchAll(assembly);

                LoadedMods.Add(assembly.GetName().Name);
                Report(log, "Mod loader", "Loaded mod: " + assembly.GetName().Name);
            }
            catch (Exception ex)
            {
                // A mod whose patch targets no longer exist throws here, and that is the common
                // case after a game update: the message needs to name the mod, or the player
                // cannot tell which one to remove.
                Exception root = ex.GetBaseException();
                string detail = root.GetType().Name + ": " + root.Message;

                // ReflectionTypeLoadException hides the real cause in LoaderExceptions, and
                // without this it reports only "Unable to load one or more of the requested
                // types", which tells nobody anything.
                if (ex is ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
                {
                    string[] inner = rtle.LoaderExceptions
                        .Where(e => e != null)
                        .Select(e => e.Message)
                        .Distinct()
                        .Take(5)
                        .ToArray();
                    if (inner.Length > 0)
                    {
                        detail += Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", inner);
                    }
                }

                FailedMods.Add(fileName + " - " + detail);
                Report(log, "Mod failed to load: " + fileName,
                    detail + Environment.NewLine +
                    "The game will run without it. Remove it from user/Mods to silence this.");
            }
        }
    }

    private static void Report(Action<string, string> log, string title, string message)
    {
        log?.Invoke(message, title);
    }
}
