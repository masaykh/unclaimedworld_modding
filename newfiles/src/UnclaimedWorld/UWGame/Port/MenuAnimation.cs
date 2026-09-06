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

    /// <summary>
    /// Why the menu is showing a still image rather than the animation, or null when it is not.
    ///
    /// Kept because "the menu background is not moving" is otherwise unanswerable from outside:
    /// the file is optional, so its absence was silent, and a player cannot tell "you deleted it"
    /// from "the build never made one" from "it is there and will not parse". Those have
    /// different answers and the game is the only thing in a position to know which it is.
    /// </summary>
    public static string UnavailableReason { get; private set; }

    private static MenuAnimation TryLoad()
    {
        // The game root, resolved the same way Config.GetDataFolderPath resolves data/ and
        // user/ - against the working directory.
        string path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        if (!File.Exists(path))
        {
            UnavailableReason = FileName + " is not in " + Directory.GetCurrentDirectory()
                + " - the still background image is used instead.";
            GameStateManagement.UnclaimedWorld.LogError(
                UnavailableReason + Environment.NewLine
                + "  This is not a fault if you deleted it: doing so is the documented way to get"
                + " the still background." + Environment.NewLine
                + "  If you did not, the build step that creates it was skipped - it needs ffmpeg"
                + " and your own copy of Content\\MainMenu\\TauCetiMainMenu.wmv."
                + " build/33-make-menu-animation.sh is the step.",
                "Menu animation: using the still background");
            return null;
        }

        MenuAnimation animation = LoadFrom(path, out string error);
        if (animation == null)
        {
            // Never fatal: the still background is a perfectly good menu. But say how big the file
            // was as well as what went wrong with it - a zero-byte or truncated file is a
            // different story from a well-formed one this build cannot read, and the length is
            // the cheapest way to tell them apart.
            long length = -1L;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception)
            {
            }
            UnavailableReason = error;
            GameStateManagement.UnclaimedWorld.LogError(
                $"Could not load {FileName} ({(length >= 0 ? length + " bytes" : "size unknown")}),"
                + $" falling back to the still menu background: {error}",
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

    /// <summary>
    /// Set once a frame has failed to decode. Every later frame would fail the same way - they
    /// came out of one encoder in one run - so the animation gives up rather than trying again
    /// twelve times a second.
    ///
    /// It logged each failure, which is how a modder ended up with a 42 KB Errors.txt of the same
    /// sentence and a black menu background. Once is information; six hundred times is a fault of
    /// its own.
    /// </summary>
    private bool decodeFailed;

    /// <summary>Whether the frames in this file can be decoded at all. See <see cref="TryPrepare"/>.</summary>
    public bool Usable => !decodeFailed;

    /// <summary>
    /// Decodes the first frame, so a caller can find out BEFORE drawing anything whether this
    /// animation works - and fall back to the still background image if it does not.
    ///
    /// Needed because a .uwanim can parse perfectly and still be undecodable: the container is
    /// eight bytes of magic, a header and JPEG blobs, and nothing in reading it says whether
    /// MonoGame's decoder accepts the encoding those blobs are in. Progressive JPEG is the usual
    /// case - StbImageSharp, which MonoGame uses, reads baseline and extended sequential only,
    /// and some ffmpeg builds will produce progressive without being asked.
    /// </summary>
    public bool TryPrepare(GraphicsDevice device)
    {
        return Decode(device, 0) != null;
    }

    private Texture2D Decode(GraphicsDevice device, int index)
    {
        if (decodeFailed)
        {
            return null;
        }
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
            decodeFailed = true;
            UnavailableReason = ex.Message;
            GameStateManagement.UnclaimedWorld.LogError(
                $"Could not decode menu animation frame {index}: {ex.Message}" + Environment.NewLine
                + $"  the frame is {DescribeJpeg(frames[index])}" + Environment.NewLine
                + "  MonoGame decodes baseline and extended-sequential JPEG only. If this says"
                + " progressive, the ffmpeg that built the file chose an encoding it cannot read -"
                + " rebuild the animation, or delete " + FileName + " to use the still background."
                + Environment.NewLine
                + "  The still background is being used, and this is not reported again.",
                "Menu animation unavailable");
            currentTexture?.Dispose();
            currentTexture = null;
            currentFrame = index;
        }

        return currentTexture;
    }

    /// <summary>
    /// What kind of JPEG a frame is, read from its SOF marker.
    ///
    /// The exception MonoGame raises says "This image format is not supported" and nothing more,
    /// which leaves the two people looking at it guessing. The marker is four bytes into the
    /// stream and says exactly which encoding it is, so there is no reason to guess.
    /// </summary>
    private static string DescribeJpeg(byte[] frame)
    {
        if (frame == null || frame.Length < 4)
        {
            return "empty";
        }
        if (frame[0] != 0xFF || frame[1] != 0xD8)
        {
            return $"not a JPEG at all (starts {frame[0]:X2} {frame[1]:X2})";
        }

        // Walk the segment chain looking for a start-of-frame marker. Every segment after SOI is
        // 0xFF, a marker byte, then a big-endian length that includes the length bytes.
        int at = 2;
        while (at + 3 < frame.Length)
        {
            if (frame[at] != 0xFF)
            {
                return "malformed - segment marker expected at byte " + at;
            }
            byte marker = frame[at + 1];
            switch (marker)
            {
            case 0xC0:
                return "baseline JPEG, which MonoGame can read";
            case 0xC1:
                return "extended sequential JPEG, which MonoGame can read";
            case 0xC2:
                return "PROGRESSIVE JPEG, which MonoGame cannot read";
            case 0xC3:
                return "lossless JPEG, which MonoGame cannot read";
            case 0xC9:
            case 0xCA:
            case 0xCB:
                return "arithmetic-coded JPEG, which MonoGame cannot read";
            case 0xD8:
            case 0xD9:
                return "truncated before any frame header";
            }
            int length = (frame[at + 2] << 8) | frame[at + 3];
            if (length < 2)
            {
                return "malformed - segment length " + length;
            }
            at += 2 + length;
        }
        return "JPEG with no frame header - truncated at " + frame.Length + " bytes";
    }

    public void Dispose()
    {
        currentTexture?.Dispose();
        currentTexture = null;
        currentFrame = -1;
    }
}
