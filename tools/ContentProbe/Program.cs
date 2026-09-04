using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Media;
using SpriteSheetRuntime;

namespace UW.Tools.ContentProbe;

/// <summary>
/// Loads game assets through a real MonoGame ContentManager and reports pass/fail for each,
/// so content problems can be diagnosed without driving the game's UI.
///
/// Usage:
///   contentprobe &lt;ContentDir&gt;                 probe the default set (the identity-critical
///                                             and structurally interesting assets)
///   contentprobe &lt;ContentDir&gt; --all-effects   also probe every effect
///   contentprobe &lt;ContentDir&gt; asset:Type ...  probe specific assets, e.g.
///                                             "LightSources:LightSourceSpriteSheet"
///
/// Exit code is the number of failures, so it can gate a build.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The assets worth probing by default. The six sprite-sheet assets are the ones bound to
    /// SpriteSheetRuntime by assembly-qualified name via ReflectiveReader, and the models are
    /// bound to Xclna.Xna.Animationx86 the same way - between them they cover all 53
    /// identity-critical assets' code paths. Each entry is "assetName:typeName", loaded with
    /// exactly the type the game asks for (see GameData.LoadContent / Client.LoadContent).
    /// </summary>
    private static readonly string[] DefaultProbes =
    {
        // ReflectiveReader over SpriteSheetRuntime types - the interesting inheritance cases.
        "LightSources:SpriteSheet",                  // GameData.cs:1054 - base type since game 1.0.4.8
        "BuildingsAndTrees:ExtendedSpriteSheet",     // GameData.cs:1051
        "FlatSprites:SpriteSheet",                   // Client.cs:1138
        "GhostedBuildings:SpriteSheet",              // GameWorldRenderer.cs:404
        "GUI/GUISprites:SpriteSheet",
        "GUI/GUI_CRT_Sprites:SpriteSheet",

        // Xclna model readers (AnimationReader + SkinInfoCollectionReader + ArrayReader).
        "Models/skinnedtest_idle:Model",
        "Models/demonTree_idle:Model",

        // A font, a sound and a texture, for breadth.
        "Arial:SpriteFont",
        "Sounds/BIP2:SoundEffect",
        "MainMenu/panorama_1920px:Texture2D",

        // Media and effects, which are the two things that differ between the WindowsDX and
        // DesktopGL packages - and so the two ways a content tree can belong to the wrong build.
        //
        // build/60-package-gl.sh converts the music to Ogg Vorbis, DELETES the .wma and .wmv, and
        // overwrites the effects with profile=0 (OpenGL) builds. Launch the DirectX binaries
        // against that tree and MediaFoundation dies on the first song with
        // "The byte stream type of the given URL is unsupported" (0xC00D36C4) after logging a
        // missing background video (0x80070002) - and had it survived that, every effect would
        // have been wrong too. None of the probes above notice: they are all format-neutral.
        //
        // A Song and a Video pin the media, and two effects pin the shader profile, because
        // loading a profile=0 effect on the DirectX runtime throws.
        "Music/Martin Hasseldam - Settle:Song",
        "multiTex:Effect",
        "skinFX:Effect",

        // NOTE: "MainMenu/TauCetiMainMenu:Video" was probed here until PORT DEVIATION 17. It is
        // gone deliberately, not by oversight. The port no longer contains a VideoPlayer, and
        // loading that asset would construct a MonoGame Video through VideoReader - i.e. this
        // tool would be the last thing in the repository depending on the video stack, and would
        // fail on DesktopGL where no VideoPlayer exists. The menu background is now checked by
        // ProbeMenuAnimation instead, and build/81-verify-no-videoplayer.sh asserts that nothing
        // shipped references the video types at all.
        //
        // The .wmv and its .xnb still sit in Content/ untouched; the game simply never reads them.
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: contentprobe <ContentDir> [--all-effects] [asset:Type ...]");
            return 2;
        }

        string contentDir = Path.GetFullPath(args[0]);
        if (!Directory.Exists(contentDir))
        {
            Console.Error.WriteLine($"no such directory: {contentDir}");
            return 2;
        }

        bool allEffects = args.Contains("--all-effects");

        // --override <dir>: load through that folder in preference to <ContentDir>, exactly as
        // the game does with port-content (PORT DEVIATION 18). This is what lets the build gate
        // probe the PRISTINE game content against a freshly built override - the combination that
        // actually ships. Probing a content folder that was converted in place proves nothing
        // about the override.
        string overrideDir = null;
        int ov = Array.IndexOf(args, "--override");
        if (ov >= 0 && ov + 1 < args.Length) overrideDir = Path.GetFullPath(args[ov + 1]);

        var explicitProbes = args.Skip(1)
            .Where(a => !a.StartsWith("--"))
            .Where(a => overrideDir == null || a != args[ov + 1])
            .ToArray();

        var probes = new List<string>(explicitProbes.Length > 0 ? explicitProbes : DefaultProbes);
        if (allEffects)
        {
            foreach (string path in Directory.EnumerateFiles(contentDir, "*.xnb", SearchOption.AllDirectories)
                                             .OrderBy(p => p, StringComparer.Ordinal))
            {
                // Cheap identification: the reader name is plain text near the top of the XNB.
                // Match the FULL name - "EffectReader" is a substring of "SoundEffectReader",
                // and matching loosely classifies all 220 sound effects as shaders.
                byte[] head = ReadHead(path, 4096);
                if (System.Text.Encoding.ASCII.GetString(head)
                        .Contains("Microsoft.Xna.Framework.Content.EffectReader"))
                {
                    string asset = Path.ChangeExtension(Path.GetRelativePath(contentDir, path), null)!
                        .Replace('\\', '/');
                    probes.Add(asset + ":Effect");
                }
            }
        }

        using var probe = new Probe(contentDir, probes, overrideDir);
        probe.Run();
        return probe.Failures;
    }

    private static byte[] ReadHead(string path, int count)
    {
        using FileStream fs = File.OpenRead(path);
        byte[] buffer = new byte[Math.Min(count, (int)fs.Length)];
        _ = fs.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    private sealed class Probe : Game
    {
        private readonly GraphicsDeviceManager _gdm;
        private readonly string _contentDir;
        private readonly List<string> _probes;
        private readonly string _overrideDir;

        public int Failures { get; private set; }

        public Probe(string contentDir, List<string> probes, string overrideDir = null)
        {
            _contentDir = contentDir;
            _probes = probes;
            _overrideDir = overrideDir;
            _gdm = new GraphicsDeviceManager(this)
            {
                // HiDef matches the game, which matters: the shipped content is HiDef profile
                // and some of it will not load under Reach.
                GraphicsProfile = GraphicsProfile.HiDef,
                PreferredBackBufferWidth = 64,
                PreferredBackBufferHeight = 64,
            };
#if UW_HAVE_GAME
            // Load through the GAME'S content manager, so the probe sees the port-content
            // override exactly as the game does (PORT DEVIATION 18). Without this the probe
            // would read the shipped MGFX v8 effects straight out of Content\ and fail on a
            // correctly packaged installation - and, worse, would have passed on one where the
            // override was missing.
            Content = new UWGame.Port.UwContentManager(Services, contentDir)
            {
                OverrideFolder = overrideDir,
            };
#else
            Content.RootDirectory = contentDir;
#endif
        }

        protected override void Initialize()
        {
            base.Initialize();

            Console.WriteLine($"content root: {_contentDir}");
            Console.WriteLine($"graphics:     {GraphicsDevice.Adapter.Description} ({_gdm.GraphicsProfile})");
            if (_overrideDir != null)
            {
                Console.WriteLine($"override:     {_overrideDir}");
            }
            ProbeMediaSubsystem();
            ProbeMenuAnimation();
            ProbeEffectVersionCheck();
            Console.WriteLine();

            int ok = 0;
            foreach (string spec in _probes)
            {
                int split = spec.LastIndexOf(':');
                string asset = split < 0 ? spec : spec[..split];
                string typeName = split < 0 ? "Object" : spec[(split + 1)..];

                try
                {
                    object loaded = Load(asset, typeName);
                    Console.WriteLine($"  OK      {asset,-42} -> {Describe(loaded)}");
                    ok++;
                }
                catch (Exception ex)
                {
                    Exception root = ex;
                    while (root.InnerException != null) root = root.InnerException;
                    Console.WriteLine($"  FAIL    {asset,-42} -> {typeName}");
                    Console.WriteLine($"          {root.GetType().Name}: {root.Message}");
                    Failures++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{ok} loaded, {Failures} failed, of {_probes.Count} probed.");
            Exit();
        }

        /// <summary>
        /// Forces MediaPlayer's static initialiser, which is the first thing the real game does
        /// with the media stack (AudioManager's constructor reads MediaPlayer.State).
        ///
        /// This exists because a bad transitive dependency version is invisible to every other
        /// check. Raising SharpDX from the 4.0.1 MonoGame was compiled against to 4.2.0 built
        /// cleanly, passed the whole asset probe, and then killed the game on launch with
        /// "Method 'QueryInterface' in type 'Callback' ... does not have an implementation" -
        /// MonoGame's precompiled Callback no longer satisfied an interface SharpDX had changed.
        /// The game does not reference SharpDX itself, so there is no compile-time check, and
        /// loading content never touches MediaFoundation. One property read does.
        /// </summary>
        private void ProbeMediaSubsystem()
        {
            try
            {
                MediaState state = MediaPlayer.State;
                Console.WriteLine($"media:        MediaPlayer OK (state {state})");
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                Console.WriteLine($"media:        MediaPlayer FAILED - {root.GetType().Name}: {root.Message}");
                Console.WriteLine("              The game constructs AudioManager at startup and will not run.");
                Failures++;
            }
        }

        /// <summary>
        /// Checks the menu animation that replaced the WMV video player (PORT DEVIATION 17):
        /// that the container parses, and that MonoGame's decoder actually accepts its frames.
        ///
        /// The second half is the point. The container is our own format, so a header check only
        /// proves we can read what we wrote; what could genuinely fail is StbImageSharp refusing
        /// the JPEGs ffmpeg produced, and the only way to know is to decode some. First, middle
        /// and last are probed - last especially, because a truncated file is the likely
        /// packaging accident and it is the final frame that reveals it.
        ///
        /// Uses the game's own UWGame.Port.MenuAnimation rather than a reimplementation, so this
        /// cannot pass while the game fails on the same file.
        /// </summary>
        private void ProbeMenuAnimation()
        {
#if UW_HAVE_GAME
            // The game root, which is where both port-content\ and the animation live. When an
            // override folder is given that is the more reliable anchor: the content directory
            // may be somebody else's installation (the build gate points it at the pristine
            // Steam content, whose root has no animation in it).
            string gameRoot = _overrideDir != null
                ? Directory.GetParent(_overrideDir)?.FullName
                : Directory.GetParent(_contentDir)?.FullName;
            string path = Path.Combine(gameRoot ?? _contentDir, UWGame.Port.MenuAnimation.FileName);

            if (!File.Exists(path))
            {
                // Optional by design: the game falls back to the still background.
                Console.WriteLine($"animation:    none ({UWGame.Port.MenuAnimation.FileName} not in the game root)");
                return;
            }

            UWGame.Port.MenuAnimation animation = UWGame.Port.MenuAnimation.LoadFrom(path, out string error);
            if (animation == null)
            {
                Console.WriteLine($"animation:    FAILED to parse - {error}");
                Failures++;
                return;
            }

            int[] probes = animation.FrameCount >= 3
                ? new[] { 0, animation.FrameCount / 2, animation.FrameCount - 1 }
                : new[] { 0 };
            foreach (int index in probes)
            {
                try
                {
                    using Texture2D frame = animation.DecodeFrameForValidation(GraphicsDevice, index);
                    if (frame.Width != animation.Width || frame.Height != animation.Height)
                    {
                        Console.WriteLine($"animation:    FAILED - frame {index} is {frame.Width}x{frame.Height}, " +
                                          $"header says {animation.Width}x{animation.Height}");
                        Failures++;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"animation:    FAILED to decode frame {index} - {ex.GetType().Name}: {ex.Message}");
                    Failures++;
                    return;
                }
            }

            Console.WriteLine($"animation:    OK ({animation.FrameCount} frames, " +
                              $"{animation.Width}x{animation.Height}, frames {string.Join("/", probes)} decoded)");
            animation.Dispose();
#else
            Console.WriteLine("animation:    skipped (built without UnclaimedWorld.dll)");
#endif
        }

        /// <summary>
        /// Runs the game's own startup shader check against the installation under test.
        ///
        /// The failure mode worth guarding is the FALSE ALARM: since PORT DEVIATION 18 the
        /// shaders in Content\ are deliberately left at the shipped MGFX v8, so a check written
        /// against Content\ rather than against what the override resolves to would refuse to
        /// start every correctly installed copy. This asserts it stays silent on a good install.
        /// </summary>
        private void ProbeEffectVersionCheck()
        {
#if UW_HAVE_GAME
            string gameRoot = _overrideDir != null
                ? Directory.GetParent(_overrideDir)?.FullName
                : Directory.GetParent(_contentDir)?.FullName;
            if (gameRoot == null)
            {
                return;
            }

            string message = UWGame.Port.EffectVersionCheck.Check(gameRoot);
            if (message == null)
            {
                Console.WriteLine("shaders:      OK (startup check silent - the game would run)");
            }
            else
            {
                Console.WriteLine("shaders:      WOULD BLOCK STARTUP:");
                foreach (string line in message.Split('\n'))
                {
                    Console.WriteLine("              " + line.TrimEnd('\r'));
                }
                Failures++;
            }
#endif
        }

        private object Load(string asset, string typeName) => typeName switch
        {
            "LightSourceSpriteSheet" => Content.Load<LightSourceSpriteSheet>(asset),
            "ExtendedSpriteSheet" => Content.Load<ExtendedSpriteSheet>(asset),
            "SpriteSheet" => Content.Load<SpriteSheet>(asset),
            "Model" => Content.Load<Model>(asset),
            "Effect" => Content.Load<Effect>(asset),
            "SpriteFont" => Content.Load<SpriteFont>(asset),
            "SoundEffect" => Content.Load<Microsoft.Xna.Framework.Audio.SoundEffect>(asset),
            "Song" => Content.Load<Song>(asset),
            "Texture2D" => Content.Load<Texture2D>(asset),
            _ => Content.Load<object>(asset),
        };

        /// <summary>
        /// Every distinct vertex layout a model uses, as usage/index/format triples.
        ///
        /// This is the ground truth the effects have to match. MonoGame's OpenGL backend resolves
        /// a vertex element to a shader attribute by USAGE and INDEX, and a miss returns -1 and
        /// silently feeds the shader (0,0,0,1) rather than failing - so a layout that disagrees
        /// with the shader's input signature corrupts geometry with no error anywhere. Comparing
        /// these against `mgfxdxtogl signatures` is the only way to see it.
        /// </summary>
        private static string DescribeVertexLayouts(Model m)
        {
            var seen = new List<string>();
            foreach (ModelMesh mesh in m.Meshes)
            {
                foreach (ModelMeshPart part in mesh.MeshParts)
                {
                    VertexDeclaration declaration = part.VertexBuffer?.VertexDeclaration;
                    if (declaration == null) continue;
                    string text = $"stride={declaration.VertexStride} " + string.Join(" ",
                        declaration.GetVertexElements().Select(e =>
                            $"{e.VertexElementUsage}{e.UsageIndex}:{e.VertexElementFormat}@{e.Offset}"));
                    if (!seen.Contains(text)) seen.Add(text);
                }
            }
            return seen.Count == 0
                ? ""
                : Environment.NewLine + string.Join("", seen.Select(s => "            " + s + Environment.NewLine)).TrimEnd();
        }

        private static string Describe(object o) => o switch
        {
            LightSourceSpriteSheet l =>
                $"LightSourceSpriteSheet, {l.spriteRectangles?.Count ?? -1} rects, " +
                $"{l.AllLightSourceData?.Count ?? -1} light types, texture={(l.Texture != null ? $"{l.Texture.Width}x{l.Texture.Height}" : "null")}",
            ExtendedSpriteSheet e =>
                $"ExtendedSpriteSheet, {e.spriteRectangles?.Count ?? -1} rects, " +
                $"normal={(e.NormalTexture != null ? "yes" : "no")}",
            SpriteSheet s => $"SpriteSheet, {s.spriteRectangles?.Count ?? -1} rects",
            Model m => $"Model, {m.Meshes.Count} meshes, {m.Bones.Count} bones" + DescribeVertexLayouts(m),
            Effect fx => $"Effect, {fx.Techniques.Count} techniques, {fx.Parameters.Count} params",
            SpriteFont f => $"SpriteFont, lineSpacing={f.LineSpacing}",
            Texture2D t => $"Texture2D {t.Width}x{t.Height} {t.Format}",
            _ => o.GetType().Name,
        };

        protected override void Draw(GameTime gameTime) { }
    }
}
