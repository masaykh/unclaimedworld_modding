using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace UWGame.Port;

/// <summary>
/// Plays the main-menu background animation from <c>MainMenuIntro.uwanim</c> in the game root -
/// a motion-JPEG frame sequence produced by <c>build/33-make-menu-animation.sh</c>.
///
/// This replaces MonoGame's <c>VideoPlayer</c> and the shipped <c>TauCetiMainMenu.wmv</c>
/// playback entirely. What that buys:
///
///   * the DesktopGL build gets a menu animation at all. MonoGame has no DesktopGL VideoPlayer -
///     even constructing one throws - so the GL build has always fallen back to a still image
///   * no MediaFoundation, so no Windows Media Player registry gate at startup
///   * three port deviations (5, 7 and 11) existed only to work around VideoPlayer, and go
///
/// WHY JPEG. MonoGame ILMerges StbImageSharp, which already decodes JPEG, so this needs no new
/// dependency and works on every backend. Measured against the alternatives on this clip:
/// WebP is smaller (1.6 MB vs 3.1 at 640x360) but needs ImageSharp and its split licence; APNG
/// needs no dependency but is 38 MB, four times the video, because lossless is the wrong tool
/// for photographic gradients; AVIF and JPEG XL have no managed decoder at all.
///
/// MEMORY. The compressed frames - about 4.7 MB - are held in memory, and exactly ONE decoded
/// texture exists at a time. Frames are decoded on demand as playback advances. Keeping all 140
/// decoded would be roughly 230 MB of VRAM, which is why this streams rather than preloading.
///
/// The animation is shared by every screen that shows it, which is what the old shared
/// VideoPlayer was for: several menu screens are on the stack at once (the menu plus Load Game,
/// say) and they must not each drive their own copy.
/// </summary>
public sealed class MenuAnimation : IDisposable
{
    private const string Magic = "UWANIM01";

    /// <summary>The animation, loaded once and shared. Null when there is no file to play.</summary>
    private static MenuAnimation shared;

    private static bool triedLoad;

    private readonly byte[][] frames;

    private readonly int frameDelayMs;

    private Texture2D currentTexture;

    private int currentFrame = -1;

    private double elapsedMs;

    /// <summary>
    /// The tick this instance last advanced on. Several BackgroundScreens share one animation
    /// and each calls <see cref="GetFrame"/> from its own Draw, so without this the animation
    /// would advance once per screen per frame and run at a multiple of its intended speed -
    /// two menu screens on the stack would play it at double rate. TotalGameTime is identical
    /// for every call within one tick, which makes it the natural guard.
    /// </summary>
    private TimeSpan lastAdvance = TimeSpan.MinValue;

    public int Width { get; }

    public int Height { get; }

    public int FrameCount => frames.Length;

    private MenuAnimation(byte[][] frames, int width, int height, int frameDelayMs)
    {
        this.frames = frames;
        this.frameDelayMs = Math.Max(1, frameDelayMs);
        Width = width;
        Height = height;
    }

    /// <summary>
    /// The shared animation, or null if <c>MainMenuIntro.uwanim</c> is absent or unreadable -
    /// in which case callers fall back to the still background image, exactly as they already do
    /// when the PlayVideo option is off. The file is optional by design: deleting it restores
    /// the still background with no other change.
    /// </summary>
    public static MenuAnimation Shared
    {
        get
        {
            if (!triedLoad)
            {
                triedLoad = true;
                shared = TryLoad();
            }
            return shared;
        }
    }

    /// <summary>The file name this looks for, in the game root.</summary>
    public const string FileName = "MainMenuIntro.uwanim";

    private static MenuAnimation TryLoad()
    {
        // The game root, resolved the same way Config.GetDataFolderPath resolves data/ and
        // user/ - against the working directory.
        string path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        MenuAnimation animation = LoadFrom(path, out string error);
        if (animation == null)
        {
            // Never fatal: the still background is a perfectly good menu.
            GameStateManagement.UnclaimedWorld.LogError(
                $"Could not load {FileName}, falling back to the still menu background: {error}",
                "Menu animation unavailable");
        }
        return animation;
    }

    /// <summary>
    /// Parses a .uwanim container from an explicit path, returning null and a reason on failure
    /// rather than throwing.
    ///
    /// Public and path-taking so the container can be validated outside the game -
    /// tools/ContentProbe uses this to check a packaged animation parses AND that MonoGame's
    /// decoder actually accepts its frames, which is the part no amount of header checking
    /// would catch.
    /// </summary>
    public static MenuAnimation LoadFrom(string path, out string error)
    {
        error = null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new BinaryReader(stream);

            string magic = new string(reader.ReadChars(8));
            if (magic != Magic)
            {
                throw new InvalidDataException($"not a UWANIM file (magic '{magic}')");
            }
            int version = reader.ReadInt32();
            if (version != 1)
            {
                throw new InvalidDataException($"unsupported UWANIM version {version}");
            }

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int count = reader.ReadInt32();
            int delay = reader.ReadInt32();

            if (count <= 0 || width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"implausible header: {width}x{height}, {count} frames");
            }

            int[] lengths = new int[count];
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                lengths[i] = reader.ReadInt32();
                if (lengths[i] <= 0)
                {
                    throw new InvalidDataException($"frame {i} has length {lengths[i]}");
                }
                total += lengths[i];
            }

            // Checked before allocating, so a corrupt length table cannot make this try to read
            // gigabytes.
            long remaining = stream.Length - stream.Position;
            if (total != remaining)
            {
                throw new InvalidDataException(
                    $"frame table totals {total} bytes but {remaining} remain in the file");
            }

            byte[][] frames = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                frames[i] = reader.ReadBytes(lengths[i]);
                if (frames[i].Length != lengths[i])
                {
                    throw new EndOfStreamException($"frame {i} truncated");
                }
            }

            return new MenuAnimation(frames, width, height, delay);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Decodes one frame by index without advancing playback. For validation only - normal
    /// playback goes through <see cref="GetFrame"/>.
    /// </summary>
    public Texture2D DecodeFrameForValidation(GraphicsDevice device, int index)
    {
        using MemoryStream jpeg = new MemoryStream(frames[index], writable: false);
        return Texture2D.FromStream(device, jpeg);
    }

    /// <summary>
    /// Advances playback and returns the texture for the current frame, decoding it if the frame
    /// changed. Loops forever. Returns null only if decoding failed.
    ///
    /// Safe to call several times per tick: only the first call in a tick advances, so screens
    /// sharing this instance all see the same frame and the animation runs at its real speed.
    /// </summary>
    public Texture2D GetFrame(GraphicsDevice device, GameTime gameTime)
    {
        if (currentFrame < 0)
        {
            // First call: show frame 0 rather than waiting a frame delay for it.
            elapsedMs = 0.0;
            lastAdvance = gameTime.TotalGameTime;
            return Decode(device, 0);
        }

        if (gameTime.TotalGameTime == lastAdvance)
        {
            return currentTexture;
        }
        lastAdvance = gameTime.TotalGameTime;

        elapsedMs += gameTime.ElapsedGameTime.TotalMilliseconds;
        if (elapsedMs < frameDelayMs)
        {
            return currentTexture;
        }

        // A long stall (alt-tab, a slow load) must not walk the animation forward frame by frame
        // on the next tick, so jump by however many frames actually elapsed.
        int advance = (int)(elapsedMs / frameDelayMs);
        elapsedMs -= (double)advance * frameDelayMs;
        return Decode(device, (currentFrame + advance) % frames.Length);
    }

    private Texture2D Decode(GraphicsDevice device, int index)
    {
        if (index == currentFrame && currentTexture != null)
        {
            return currentTexture;
        }

        try
        {
            using MemoryStream jpeg = new MemoryStream(frames[index], writable: false);
            Texture2D decoded = Texture2D.FromStream(device, jpeg);

            // One decoded frame at a time - see the memory note on the class.
            currentTexture?.Dispose();
            currentTexture = decoded;
            currentFrame = index;
        }
        catch (Exception ex)
        {
            GameStateManagement.UnclaimedWorld.LogError(
                $"Could not decode menu animation frame {index}: {ex.Message}",
                "Menu animation unavailable");
            currentTexture?.Dispose();
            currentTexture = null;
            currentFrame = index;
        }

        return currentTexture;
    }

    public void Dispose()
    {
        currentTexture?.Dispose();
        currentTexture = null;
        currentFrame = -1;
    }
}
