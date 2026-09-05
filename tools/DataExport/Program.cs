using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UWGame.SimSide;
using UWGame.SimSide.AllGameData;

namespace UW.Tools.DataExport;

internal static class Program
{
    private static int Main(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool readBack = args.Contains("--read-back");
        bool settingsSelfTest = args.Contains("--settings-selftest");

        // The bundled Unhidden Mod is on by default and its content hooks run inside the data
        // load, so an export made without this reflects the MODDED tables. That is usually what
        // you want - it is what the game will run - but exporting the stock tables has to be
        // possible too, both for diffing the mod's effect and for a data modder who wants the
        // studio's own numbers as a starting point. Mirrors UnclaimedWorld.exe -nomods.
        if (args.Contains("--nomods") || args.Contains("-nomods"))
        {
            UWGame.Mods.UnhiddenMod.Disable();
            UWGame.Mods.ModLoader.Disable();
        }
        Console.WriteLine("==> Unhidden Mod: " + (UWGame.Mods.UnhiddenMod.Enabled ? "ON" : "off"));

        if (target == null)
        {
            Console.Error.WriteLine("usage: dataexport <game-dir> [--read-back] [--nomods]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Runs the game's own data export against <game-dir>, writing");
            Console.Error.WriteLine("  data/BaseData/*.xml and reading each file straight back - the same thing");
            Console.Error.WriteLine("  UnclaimedWorld.exe --export-data does, but with no window.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  --read-back  afterwards, load again in Read mode (no built-in defaults),");
            Console.Error.WriteLine("               which is what UnclaimedWorld.exe --data-from-xml does. This is");
            Console.Error.WriteLine("               the check that the exported XML is actually sufficient to run");
            Console.Error.WriteLine("               from, rather than merely well-formed.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  --nomods     export the stock tables, with the bundled Unhidden Mod off.");
            Console.Error.WriteLine("               Without this the export includes the mod's content.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  --settings-selftest");
            Console.Error.WriteLine("               write, re-read and verify user/ModSettings.xml in <game-dir>,");
            Console.Error.WriteLine("               and load nothing else. Used by build/80-verify-modloader.sh.");
            return 2;
        }

        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine($"FATAL: no such directory: {target}");
            return 1;
        }

        if (settingsSelfTest)
        {
            Directory.SetCurrentDirectory(target);
            return SettingsSelfTest();
        }

        // Config.GetDataFolderPath returns paths relative to the working directory ("data/BaseData"),
        // so the working directory IS the choice of which installation to write into. Set it
        // explicitly and report it, rather than depending on how the tool was launched.
        Directory.SetCurrentDirectory(target);
        Console.WriteLine("==> target: " + Directory.GetCurrentDirectory());

        // Load third-party mods from the target's user/Mods, exactly as the game does, and after
        // the working directory is set because that is what user/Mods resolves against.
        //
        // This is the whole reason a data mod can be developed without launching: a Harmony patch
        // on ItemLoader.Init or ProcessLoader.InitProcessTypes shows up in the exported XML, so
        // "did my patch apply, and what did it produce" is answerable in a second from a console.
        // Mod configuration, from the target's user/ModSettings.xml - after the working directory
        // is set, because that is what it resolves against. The switches shape the tables, so an
        // export made without reading them would describe a game nobody is running.
        UWGame.Mods.ModSettings.Load((message, title) => Console.WriteLine("    " + title + ": " + message));
        UWGame.Mods.PortSettings.RegisterSettings();
        if (UWGame.Mods.UnhiddenMod.Enabled)
        {
            UWGame.Mods.UnhiddenMod.RegisterSettings();
        }
        foreach (UWGame.Mods.ModSetting setting in UWGame.Mods.ModSettings.All)
        {
            if (!setting.IsDefault)
            {
                Console.WriteLine("==> setting: " + setting.Id + " = " + setting.Value +
                                  " (default " + setting.DefaultValue + ")");
            }
        }

        UWGame.Mods.ModLoader.LoadAll((message, title) => Console.WriteLine("    " + title + ": " + message));
        if (UWGame.Mods.ModLoader.Loaded.Count > 0 || UWGame.Mods.ModLoader.Failed.Count > 0)
        {
            Console.WriteLine($"==> mods: {UWGame.Mods.ModLoader.Loaded.Count} loaded, " +
                              $"{UWGame.Mods.ModLoader.Failed.Count} failed");
        }

        int rc = Run(Sim.SerializeMode.WriteAndRead, "export (write, then read each file back)");
        if (rc != 0) return rc;

        Report();

        if (readBack)
        {
            Console.WriteLine();
            rc = Run(Sim.SerializeMode.Read, "read-back (from the exported XML only)");
            if (rc != 0) return rc;
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
        return 0;
    }

    /// <summary>
    /// Round-trips user/ModSettings.xml: register, change, write, forget, read back.
    ///
    /// This exists because the WRITE side of the settings file has no other offline witness. A
    /// player's configuration surviving is the whole promise of the file, and the two ways it
    /// could quietly break - a value that does not come back, and an entry belonging to a mod
    /// that is not installed being dropped on the next save - are both invisible until someone
    /// loses their settings. Neither needs the game to be running to check.
    ///
    /// It writes into the target installation's user/ folder, which is why the verify script
    /// gives it a throwaway one.
    /// </summary>
    private static int SettingsSelfTest()
    {
        Console.WriteLine("==> settings self-test in " + Directory.GetCurrentDirectory());
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
            if (!ok)
            {
                failures++;
            }
        }

        string path = UWGame.Mods.ModSettings.FilePath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // A file written by hand, holding an entry nothing will claim.
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path,
            "<ModSettings>" + Environment.NewLine +
            "  <Setting id=\"someOtherMod.itsSetting\" value=\"keep me\" />" + Environment.NewLine +
            "</ModSettings>" + Environment.NewLine);

        UWGame.Mods.ModSettings.Reset();
        UWGame.Mods.ModSettings.Load((m, t) => Console.WriteLine("    " + t + ": " + m));
        UWGame.Mods.PortSettings.RegisterSettings();
        UWGame.Mods.ModSetting dateFormat = UWGame.Mods.PortSettings.SaveDateFormat;

        Check(dateFormat.Value == dateFormat.DefaultValue, "an unset setting reads as its default");

        dateFormat.Value = "dd-MM-yyyy HH:mm";
        Check(UWGame.Mods.ModSettings.Save((m, t) => Console.WriteLine("    " + t + ": " + m)),
              "the file was written");

        UWGame.Mods.ModSettings.Reset();
        UWGame.Mods.ModSettings.Load((m, t) => Console.WriteLine("    " + t + ": " + m));
        UWGame.Mods.PortSettings.RegisterSettings();
        Check(UWGame.Mods.PortSettings.SaveDateFormat.Value == "dd-MM-yyyy HH:mm",
              "the changed value came back");

        string written = File.ReadAllText(path);
        Check(written.Contains("someOtherMod.itsSetting") && written.Contains("keep me"),
              "an entry for a mod that is not installed survived the rewrite");

        // A value outside the allowed set falls back rather than being taken.
        UWGame.Mods.PortSettings.SaveDateFormat.Value = "not a format this build offers";
        Check(UWGame.Mods.PortSettings.SaveDateFormat.Value == dateFormat.DefaultValue,
              "an unrecognised choice falls back to the default");

        // Signature and ApplySignature are what a save is stamped with and matched against.
        // Signature and ApplySignature are what a save is stamped with and matched against.
        UWGame.Mods.ModSetting sim = UWGame.Mods.ModSettings.Toggle(
            "selftest", "content", "SELF TEST CONTENT", defaultValue: true, toolTip: null,
            affectsSimulation: true);
        Check(UWGame.Mods.ModSettings.Signature().Contains("selftest.content=true"),
              "a setting that is ON is named in the signature even though it is also the default");

        // The player's own choice, which everything below has to come back to.
        sim.Value = "false";
        Check(UWGame.Mods.ModSettings.Signature() == "", "a stock configuration signs as stock");

        UWGame.Mods.ModSettings.ApplySignature("selftest.content=true");
        Check(sim.On, "opening a save applies the content it was made with");
        Check(UWGame.Mods.ModSettings.SignatureApplied, "the applied state is remembered");

        // Scoped to the session that opened the save: the main menu puts the player's own back.
        // Without this, opening one modded save would silently change what every later NEW game is
        // played with.
        UWGame.Mods.ModSettings.RestoreAfterSaveApplied();
        Check(!sim.On, "returning to the main menu restores the setting the player had");
        Check(!UWGame.Mods.ModSettings.SignatureApplied, "and stops claiming a save's settings are in force");

        sim.Value = "true";
        UWGame.Mods.ModSettings.ApplySignature("");
        Check(!sim.On, "applying an empty signature returns to stock");
        UWGame.Mods.ModSettings.RestoreAfterSaveApplied();

        // Explain() is what the MODDED tooltip colours by: present or missing, per entry.
        var explained = UWGame.Mods.ModSettings.Explain("selftest.content=true; mod:NotInstalledMod");
        Check(explained.Count == 2, "a signature explains one line per thing it names");
        Check(explained[0].Value, "a setting this session has is marked present");
        Check(!explained[1].Value, "a mod this session does not have is marked missing");

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("settings self-test OK");
            return 0;
        }
        Console.WriteLine($"settings self-test FAILED - {failures} check(s)");
        return 1;
    }

    private static int Run(Sim.SerializeMode mode, string what)
    {
        Console.WriteLine("==> " + what);
        Sim.CurrentSerializeMode = mode;

        // The load screen is the data layer's only dependency on the UI, and it is null here.
        // DataLoader.UpdateProgress tolerates that (PORT DEVIATION 13).
        var loader = new BaseDataLoader();
        var sw = Stopwatch.StartNew();
        int steps = 0;
        var failures = new List<(DataLoaderQueueState State, string Error)>();

        // The queueState field only advances after a table's Handle* call returns, so a table
        // that throws would be retried forever. Stepping the field past it turns one run into a
        // complete list of what does and does not export, instead of a report on the first
        // problem only. Reflection into a private field is not something the game should do -
        // it is something a diagnostic tool should, and the alternative is 80 separate runs.
        FieldInfo queueStateField = typeof(DataLoader).GetField(
            "queueState", BindingFlags.Instance | BindingFlags.NonPublic);
        if (queueStateField == null)
        {
            Console.Error.WriteLine("FATAL: DataLoader.queueState not found; the field was renamed.");
            return 1;
        }

        // QueueInitGameData advances ONE table per call and returns false while there is more to
        // do; it returns true exactly once, on the DONE step. Note the polarity - it is a
        // "may I move on?", not a "keep going". Sim.QUEUESTATE.LOAD_BASE_DATA drives it the
        // same way, one call per frame.
        //
        // The cap guards against a state machine that never terminates. It is well above the
        // ~60 real steps, so hitting it means something is wrong, not that the data grew.
        while (true)
        {
            if (++steps > 500)
            {
                Console.Error.WriteLine("FATAL: QueueInitGameData did not report completion within 500 steps.");
                return 1;
            }

            var before = (DataLoaderQueueState)queueStateField.GetValue(loader);
            try
            {
                // scenario: null exports the BASE data tables only, which is what this tool is
                // for. Game 1.0.4.8 added the parameter (Sim.QueueGameDataAndSimInit passes the
                // scenario from StartGameParams, or null when there is none); scenario overlays
                // are loaded by a second, per-scenario DataLoader that this tool does not drive.
                if (loader.QueueInitGameData(null)) break;
            }
            catch (Exception ex)
            {
                Exception root = ex.GetBaseException();
                failures.Add((before, root.GetType().Name + ": " + root.Message));
                Console.WriteLine($"    FAIL {before}");
                Console.WriteLine($"         {root.GetType().Name}: {Truncate(root.Message, 150)}");

                var after = (DataLoaderQueueState)queueStateField.GetValue(loader);
                if (after != before)
                {
                    // It got far enough to advance itself; nothing to force.
                    continue;
                }
                if (before == DataLoaderQueueState.DONE)
                {
                    break;
                }
                queueStateField.SetValue(loader, before + 1);
            }
        }

        Console.WriteLine($"    {steps} step(s) in {sw.ElapsedMilliseconds} ms, " +
                          $"{failures.Count} table(s) failed");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("    Tables that cannot be exported:");
            foreach (var (state, error) in failures)
                Console.WriteLine($"      {state,-32} {Truncate(error, 110)}");
        }

        // Non-zero only when nothing worked. A partial export is the honest current state of
        // the game's data hooks, not a tool failure - see PORTING-NOTES.md.
        return steps > 1 ? 0 : 1;
    }

    private static string Truncate(string s, int max) =>
        s == null ? "" : (s.Length <= max ? s : s.Substring(0, max - 3) + "...");

    private static void Report()
    {
        string dir = Path.Combine("data", "BaseData");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("    (no data/BaseData directory was created)");
            return;
        }

        var files = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long total = files.Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"    {files.Count} XML file(s), {total / 1024} KB in {dir}");

        // An empty or single-element file usually means the table's Init() returned nothing,
        // which is worth seeing rather than counting as success.
        var suspicious = files.Where(f => new FileInfo(f).Length < 200).ToList();
        foreach (string f in suspicious)
            Console.WriteLine($"    ? {Path.GetFileName(f)} is only {new FileInfo(f).Length} bytes");

        foreach (string f in files.OrderByDescending(f => new FileInfo(f).Length).Take(10))
            Console.WriteLine($"      {new FileInfo(f).Length,9:N0}  {Path.GetRelativePath(dir, f)}");
    }
}
