using System;
using System.IO;

namespace UWGame.Port;

/// <summary>
/// Checks at startup that the shader effects the game is about to load are the MGFX v10 that
/// MonoGame 3.8 requires, and produces an actionable message when they are not.
///
/// WHY THIS EXISTS. Without it, an incorrectly set up installation dies with MonoGame's own
/// wording:
///
///     This MGFX effect is for an older release of MonoGame and needs to be rebuilt.
///       at Microsoft.Xna.Framework.Graphics.Effect.ReadHeader(...)
///
/// which names no file, suggests no remedy, and is thrown deep inside content loading several
/// screens into startup - in practice from DisplayPanelRenderer.LoadContent. It is not a rare
/// mistake either: the binaries archive contains an UnclaimedWorld.exe, so copying the binaries
/// somewhere by hand and running that exe is an obvious thing to try, and it skips the setup
/// script that brings <c>port-content\</c> along.
///
/// WHAT IT ACTUALLY CHECKS - and this is the part that matters since PORT DEVIATION 18: it
/// resolves each candidate effect THE WAY THE GAME DOES, preferring <c>port-content\</c> over
/// <c>Content\</c>, and reports on whichever file would really be loaded.
///
/// Checking <c>Content\</c> directly would be wrong now. Those effects are deliberately left as
/// the shipped v8 and are never converted, so a check on them would fire on every correctly
/// installed copy - the exact false alarm this class would have produced if it had been wired up
/// as first written.
///
/// It is deliberately CONSERVATIVE: anything it cannot establish is treated as fine and startup
/// proceeds. A guard that blocks the game on its own uncertainty is worse than the error it
/// replaces.
/// </summary>
public static class EffectVersionCheck
{
    /// <summary>The container version MonoGame 3.8.5.1 accepts. It reads v10 and v11.</summary>
    private const int MinimumVersion = 10;

    /// <summary>
    /// Effects to sample, in order; the first one resolvable is used. Several are listed because
    /// a partial or modded installation may lack any given file, and because the studio removes
    /// effects between versions - 1.0.4.8 dropped skinFX_0.
    /// </summary>
    private static readonly string[] Candidates =
    {
        "multiTex.xnb",
        "skinFX.xnb",
        "water.xnb",
        "RoundLine.xnb",
        "Billboard.xnb",
    };

    /// <summary>
    /// Returns null when startup may proceed, otherwise the message to show the player.
    /// </summary>
    /// <param name="gameRoot">
    /// The installation to inspect. Defaults to the working directory, which is what the game
    /// passes. Tools pass a path so a folder can be checked without running the game in it -
    /// tools/ContentProbe uses this to prove the check stays silent on a correct install, which
    /// is the failure mode that matters: a false alarm here would block every good copy.
    /// </param>
    public static string Check(string gameRoot = null)
    {
        try
        {
            string root = gameRoot ?? Directory.GetCurrentDirectory();
            string contentDir = Path.Combine(root, "Content");
            string overrideDir = Path.Combine(root, UwContentManager.OverrideFolderName);

            if (!Directory.Exists(contentDir))
            {
                // No Content at all is a different problem, and content loading reports it far
                // better than this could.
                return null;
            }

            foreach (string candidate in Candidates)
            {
                // Resolve exactly as UwContentManager.OpenStream would: override wins.
                string overridePath = Path.Combine(overrideDir, candidate);
                string contentPath = Path.Combine(contentDir, candidate);
                string resolved = File.Exists(overridePath) ? overridePath
                    : File.Exists(contentPath) ? contentPath
                    : null;

                if (resolved == null)
                {
                    continue;
                }

                int version = ReadMgfxVersion(resolved);
                if (version < 0 || version >= MinimumVersion)
                {
                    // Either no MGFX header to read, or it is already fine. Neither is worth
                    // blocking startup over.
                    return null;
                }

                bool haveOverrideFolder = Directory.Exists(overrideDir);
                string diagnosis = haveOverrideFolder
                    ? $"{UwContentManager.OverrideFolderName}\\ exists but does not contain {candidate}, "
                      + "so the original unconverted shader is being used."
                    : $"the {UwContentManager.OverrideFolderName}\\ folder is missing entirely.";

                return
                    $"The shader effects this installation would load are MGFX v{version}, and this "
                    + $"build of the game requires v{MinimumVersion}." + Environment.NewLine + Environment.NewLine
                    + diagnosis + Environment.NewLine + Environment.NewLine
                    + "The converted shaders ship with the port in that folder. Set the game up "
                    + "with the script from the port archive:" + Environment.NewLine + Environment.NewLine
                    + "    setup.cmd" + Environment.NewLine + Environment.NewLine
                    + "which copies your game's Content and data here without modifying them, and "
                    + $"brings {UwContentManager.OverrideFolderName}\\ with it. Copying only the "
                    + "binaries by hand is not enough." + Environment.NewLine + Environment.NewLine
                    + "Your own Content folder is not the problem and does not need changing - the "
                    + $"port never modifies it, and its shaders staying at v{version} is correct.";
            }

            return null;
        }
        catch (Exception)
        {
            // Never block startup because this check itself failed.
            return null;
        }
    }

    /// <summary>
    /// The MGFX container version inside an .xnb, or -1 if no MGFX header is found.
    ///
    /// The effect .xnb wraps the MGFX container and the header is not at a fixed offset - the
    /// reader manifest's length varies with the asset name - so the magic is searched for. Only
    /// the first few KB are read; the header sits well inside that.
    /// </summary>
    private static int ReadMgfxVersion(string path)
    {
        byte[] head;
        using (FileStream stream = File.OpenRead(path))
        {
            head = new byte[(int)Math.Min(4096L, stream.Length)];
            int read = 0;
            while (read < head.Length)
            {
                int got = stream.Read(head, read, head.Length - read);
                if (got <= 0) break;
                read += got;
            }
            if (read < 6) return -1;
            if (read < head.Length) Array.Resize(ref head, read);
        }

        // 'M','G','F','X' then a version byte.
        for (int i = 0; i + 5 < head.Length; i++)
        {
            if (head[i] == (byte)'M' && head[i + 1] == (byte)'G'
                && head[i + 2] == (byte)'F' && head[i + 3] == (byte)'X')
            {
                return head[i + 4];
            }
        }
        return -1;
    }
}
