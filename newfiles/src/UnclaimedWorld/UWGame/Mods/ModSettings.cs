using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UWGame.Mods;

/// <summary>What kind of value a setting holds, which is also what control the options menu draws.</summary>
public enum ModSettingKind
{
    /// <summary>On or off. Drawn as a checkbox.</summary>
    Toggle,

    /// <summary>One of a fixed list of strings. Drawn as a combo box.</summary>
    Choice,

    /// <summary>Free text. Drawn as a text box.</summary>
    Text
}

/// <summary>
/// One switch belonging to one mod. Created through <see cref="ModSettings.Toggle"/>,
/// <see cref="ModSettings.Choice"/> or <see cref="ModSettings.Text"/> - never with `new`, because
/// registration is what binds the stored value and what puts it in the options menu.
/// </summary>
public sealed class ModSetting
{
    private string value;

    internal ModSetting(string modId, string key, ModSettingKind kind, string defaultValue)
    {
        ModId = modId;
        Key = key;
        Kind = kind;
        DefaultValue = defaultValue;
        StockValue = kind == ModSettingKind.Toggle ? "false" : defaultValue;
        value = defaultValue;
    }

    /// <summary>The mod this belongs to - "unhidden" for the bundled mod, your assembly name otherwise.</summary>
    public string ModId { get; }

    /// <summary>Unique within the mod.</summary>
    public string Key { get; }

    /// <summary>"&lt;modId&gt;.&lt;key&gt;" - what appears in the file, and what the save signature names.</summary>
    public string Id => ModId + "." + Key;

    public ModSettingKind Kind { get; }

    public string DefaultValue { get; }

    /// <summary>
    /// The value that means "this contributes no modded content". For a toggle that is "false";
    /// for anything else it is the default unless the mod says otherwise.
    ///
    /// It is NOT the same thing as the default. The bundled mod's peat recipe defaults to ON, so
    /// a save made with everything at its defaults still contains modded content - and a signature
    /// computed from "differs from default" would call that save stock and let it be loaded into a
    /// build that cannot resolve its recipe. Signatures are computed against the STOCK value.
    /// </summary>
    public string StockValue { get; internal set; }

    /// <summary>Shown in the options menu. Upper case, because the LCD panels are.</summary>
    public string Label { get; internal set; }

    /// <summary>Shown as the control's tooltip, and written as a comment above the entry in the file.</summary>
    public string ToolTip { get; internal set; }

    /// <summary>Allowed values, in menu order. <see cref="ModSettingKind.Choice"/> only.</summary>
    public string[] Choices { get; internal set; }

    /// <summary>
    /// Whether changing this changes the data tables or the simulation - which is to say, whether
    /// a save made with it on can be loaded with it off. These are the settings recorded in the
    /// save header, and the only ones a load-time mismatch can be raised about.
    /// </summary>
    public bool AffectsSimulation { get; internal set; }

    /// <summary>
    /// Whether the change only shows up the next time the data tables are built - which is every
    /// setting that shapes the tables, since they are built once when a game starts. The options
    /// menu says so beside the control rather than letting a player wonder.
    /// </summary>
    public bool TakesEffectOnNextLoad { get; internal set; }

    public string Value
    {
        get => value;
        set
        {
            string candidate = value ?? DefaultValue;
            if (Kind == ModSettingKind.Toggle)
            {
                candidate = ParseBool(candidate, ParseBool(DefaultValue, false)) ? "true" : "false";
            }
            else if (Kind == ModSettingKind.Choice && Choices != null && Array.IndexOf(Choices, candidate) < 0)
            {
                // An unrecognised choice - a hand-edited file, or a value from a newer build -
                // falls back rather than throwing. A config file cannot be allowed to stop the game.
                candidate = DefaultValue;
            }
            this.value = candidate;
        }
    }

    /// <summary>The value as a bool. Meaningful for <see cref="ModSettingKind.Toggle"/>.</summary>
    public bool On => ParseBool(value, false);

    public bool IsDefault => string.Equals(value, DefaultValue, StringComparison.Ordinal);

    /// <summary>Whether this is currently contributing nothing beyond the stock game.</summary>
    public bool IsStock => string.Equals(value, StockValue, StringComparison.Ordinal);

    internal static bool ParseBool(string s, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return fallback;
        }
        s = s.Trim();
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1"
            || s.Equals("on", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0"
            || s.Equals("off", StringComparison.OrdinalIgnoreCase)
            || s.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return fallback;
    }
}

/// <summary>
/// The configuration store for mods: <c>user/ModSettings.xml</c> in the game folder, beside
/// <c>user/Mods</c>.
///
/// WHY NOT Options.xml. <see cref="UWGame.ClientSide.Options"/> is a fixed set of public fields
/// that XmlSerializer round-trips by reflection; a third-party mod cannot add a field to a class
/// compiled into the game. So mods get their own file and their own registry, and the options
/// dialog renders whatever is in the registry rather than a hard-coded list.
///
/// WHY user/ AND NOT Documents\Unclaimed World. Options.xml is per-player: resolution, volume,
/// keys. Mod settings are per-INSTALLATION - they describe which code is running, they have to sit
/// with the DLLs they configure, and two installs of the port with different mods must not fight
/// over one file. It also means the whole thing is testable offline in a throwaway directory,
/// which is what build/80-verify-modloader.sh does.
///
/// LIFECYCLE. <see cref="Load"/> once at startup, before mods are loaded and before any data
/// table is built. Mods then call <see cref="Toggle"/> / <see cref="Choice"/> / <see cref="Text"/>
/// to register, which binds each to the value read from the file; a value in the file for a mod
/// that is not installed is kept untouched and written back on save, so switching a mod off for a
/// while does not lose its configuration.
///
/// THREAD SAFETY. None, and none needed: registration happens on the startup thread before the
/// game object exists, and the options dialog runs on the UI thread.
/// </summary>
public static class ModSettings
{
    public const string FileName = "ModSettings.xml";

    private static readonly List<ModSetting> registered = new List<ModSetting>();

    private static readonly Dictionary<string, ModSetting> byId =
        new Dictionary<string, ModSetting>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw id/value pairs as read from the file, including entries nothing has claimed.</summary>
    private static readonly Dictionary<string, string> stored =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>File order of <see cref="stored"/>, so a hand-edited file keeps its shape.</summary>
    private static readonly List<string> storedOrder = new List<string>();

    private static bool loaded;

    /// <summary>Every registered setting, in registration order - which is the menu order.</summary>
    public static IReadOnlyList<ModSetting> All => registered;

    /// <summary>Where the file is, or would be.</summary>
    public static string FilePath =>
        Config.GetDataFolderPath(Config.DataType.UserModSettings, "", FileName);

    /// <summary>
    /// Reads the file. Missing is normal and silent - it means every setting is at its default,
    /// and the file appears the first time something is changed in the options menu.
    ///
    /// A malformed file is reported and then ignored, rather than thrown: a config file is the
    /// one thing in an installation a player edits by hand, and a stray character in it must cost
    /// them their settings, not their game.
    /// </summary>
    public static void Load(Action<string, string> log = null)
    {
        loaded = true;
        stored.Clear();
        storedOrder.Clear();

        string path;
        try
        {
            path = FilePath;
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not resolve " + FileName + ": " + ex.Message, "Mod settings");
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            XDocument doc = XDocument.Load(path);
            foreach (XElement e in doc.Root?.Elements("Setting") ?? Array.Empty<XElement>())
            {
                string id = (string)e.Attribute("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                id = id.Trim();
                string v = (string)e.Attribute("value") ?? e.Value ?? "";
                if (!stored.ContainsKey(id))
                {
                    storedOrder.Add(id);
                }
                stored[id] = v.Trim();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(FileName + " could not be read and is being ignored (" + ex.Message +
                        "). Delete it to start again from the defaults.", "Mod settings");
            stored.Clear();
            storedOrder.Clear();
            return;
        }

        // Anything registered before Load - there should be nothing, but a mod that registers from
        // a static constructor can get in first - is rebound now.
        foreach (ModSetting s in registered)
        {
            if (stored.TryGetValue(s.Id, out string v))
            {
                s.Value = v;
            }
        }
    }

    /// <summary>
    /// Writes the file, preserving entries belonging to mods that are not currently installed and
    /// putting a comment above each known one. Returns false and reports if it cannot be written -
    /// a read-only game folder is a real installation, not a crash.
    /// </summary>
    public static bool Save(Action<string, string> log = null)
    {
        string path;
        try
        {
            path = FilePath;
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not resolve " + FileName + ": " + ex.Message, "Mod settings");
            return false;
        }

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            XElement root = new XElement("ModSettings");
            root.Add(new XComment(
                " Settings for the mods this installation runs. Hand-editable; the game rewrites it" +
                " when you press OK in the options menu, keeping your entries and your order." +
                " Delete an entry to go back to its default, or delete the file for all of them. "));

            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // File order first, so a file someone has arranged by hand keeps its arrangement.
            foreach (string id in storedOrder)
            {
                if (byId.TryGetValue(id, out ModSetting known))
                {
                    Append(root, known);
                }
                else
                {
                    // An unclaimed entry: a mod that is not installed right now, or a typo. Either
                    // way it is someone's configuration and not ours to drop.
                    root.Add(new XElement("Setting",
                        new XAttribute("id", id),
                        new XAttribute("value", stored[id])));
                }
                written.Add(id);
            }

            foreach (ModSetting s in registered)
            {
                if (!written.Contains(s.Id))
                {
                    Append(root, s);
                }
            }

            XmlWriterSettings xws = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                NewLineChars = "\r\n"
            };
            using (XmlWriter w = XmlWriter.Create(path, xws))
            {
                new XDocument(root).Save(w);
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not write " + path + ": " + ex.Message, "Mod settings");
            return false;
        }
    }

    private static void Append(XElement root, ModSetting s)
    {
        string comment = " " + s.Label;
        if (!string.IsNullOrEmpty(s.ToolTip))
        {
            comment += " - " + s.ToolTip;
        }
        if (s.Kind == ModSettingKind.Choice && s.Choices != null)
        {
            comment += "  [" + string.Join(" | ", s.Choices) + "]";
        }
        comment += "  (default: " + s.DefaultValue + ")";
        if (s.AffectsSimulation)
        {
            comment += "  AFFECTS SAVES";
        }
        root.Add(new XComment(comment.Replace("--", "-") + " "));
        root.Add(new XElement("Setting",
            new XAttribute("id", s.Id),
            new XAttribute("value", s.Value)));
    }

    /// <summary>Registers an on/off switch and returns it bound to the stored value.</summary>
    public static ModSetting Toggle(string modId, string key, string label, bool defaultValue,
                                    string toolTip = null, bool affectsSimulation = false,
                                    bool takesEffectOnNextLoad = false)
    {
        ModSetting s = new ModSetting(modId, key, ModSettingKind.Toggle, defaultValue ? "true" : "false")
        {
            Label = label,
            ToolTip = toolTip,
            AffectsSimulation = affectsSimulation,
            TakesEffectOnNextLoad = takesEffectOnNextLoad
        };
        return Add(s);
    }

    /// <summary>Registers a one-of-several setting and returns it bound to the stored value.</summary>
    public static ModSetting Choice(string modId, string key, string label, string[] choices,
                                    string defaultValue, string toolTip = null,
                                    bool affectsSimulation = false, bool takesEffectOnNextLoad = false,
                                    string stockValue = null)
    {
        ModSetting s = new ModSetting(modId, key, ModSettingKind.Choice, defaultValue)
        {
            Label = label,
            ToolTip = toolTip,
            Choices = choices,
            AffectsSimulation = affectsSimulation,
            TakesEffectOnNextLoad = takesEffectOnNextLoad
        };
        if (stockValue != null)
        {
            s.StockValue = stockValue;
        }
        return Add(s);
    }

    /// <summary>Registers a free-text setting and returns it bound to the stored value.</summary>
    public static ModSetting Text(string modId, string key, string label, string defaultValue,
                                  string toolTip = null, bool affectsSimulation = false,
                                  bool takesEffectOnNextLoad = false)
    {
        ModSetting s = new ModSetting(modId, key, ModSettingKind.Text, defaultValue)
        {
            Label = label,
            ToolTip = toolTip,
            AffectsSimulation = affectsSimulation,
            TakesEffectOnNextLoad = takesEffectOnNextLoad
        };
        return Add(s);
    }

    private static ModSetting Add(ModSetting s)
    {
        if (byId.TryGetValue(s.Id, out ModSetting existing))
        {
            // Two mods claiming one id, or one mod registering twice. The first registration wins
            // and keeps its value: silently replacing it would change behaviour under the mod that
            // is already reading it.
            return existing;
        }
        if (stored.TryGetValue(s.Id, out string v))
        {
            s.Value = v;
        }
        registered.Add(s);
        byId[s.Id] = s;
        return s;
    }

    public static ModSetting Find(string id)
    {
        byId.TryGetValue(id ?? "", out ModSetting s);
        return s;
    }

    public static bool IsOn(string id)
    {
        ModSetting s = Find(id);
        return s != null && s.On;
    }

    public static string ValueOf(string id, string fallback = null)
    {
        ModSetting s = Find(id);
        return s != null ? s.Value : fallback;
    }

    /// <summary>Whether <see cref="Load"/> has run. Used only to keep the diagnostics honest.</summary>
    public static bool Loaded => loaded;

    /// <summary>
    /// The signature the data tables CURRENTLY IN MEMORY were built with, or null when no tables
    /// are built - at the main menu, and before the first game starts.
    ///
    /// This is not the same thing as <see cref="Signature"/>, and the difference is the whole
    /// point of it. The tables are assembled once, when a game starts; changing a switch
    /// afterwards changes what the NEXT load will build, not what is loaded now. So the question
    /// "can this save be loaded straight into the running session?" has to be asked against what
    /// was built, not against what is currently ticked - otherwise applying a save's settings and
    /// then loading it directly would put a save that needs a recipe into a session whose tables
    /// do not have one.
    /// </summary>
    public static string DataSignature { get; private set; }

    /// <summary>
    /// Called when the data tables have finished being built, from
    /// GameData.PostDataCompleteInitialize. Freezes what they were built from.
    /// </summary>
    public static void MarkDataBuilt()
    {
        DataSignature = Signature();
    }

    /// <summary>Called when the tables are thrown away, from GameData.UnloadAllData.</summary>
    public static void MarkDataUnloaded()
    {
        DataSignature = null;
    }

    /// <summary>
    /// What the running session's content is, for comparing a save against: what the tables were
    /// built with if there are tables, and what is currently switched on if there are not.
    /// </summary>
    public static string EffectiveSignature => DataSignature ?? Signature();

    /// <summary>
    /// What this session's modded content amounts to, as one line: the third-party mods that
    /// loaded, and every simulation-affecting setting that is not at its default.
    ///
    /// A setting counts when it differs from its STOCK value, not from its default: a mod whose
    /// content is on by default still puts content in the save.
    ///
    /// This is what goes in the save header, so its FORM is a compatibility surface - a save
    /// written today is compared against a signature computed by a later build. Keep it stable:
    /// "mod:&lt;AssemblyName&gt;" for a loaded mod, "&lt;id&gt;=&lt;value&gt;" for a setting,
    /// separated by "; ", in that order. Empty string means "nothing modded", which is what an
    /// unmodded build and a pre-signature save both report.
    /// </summary>
    public static string Signature()
    {
        List<string> parts = new List<string>();
        foreach (string name in ModLoader.Loaded)
        {
            parts.Add("mod:" + name);
        }
        foreach (ModSetting s in registered)
        {
            if (s.AffectsSimulation && !s.IsStock)
            {
                parts.Add(s.Id + "=" + s.Value);
            }
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Applies a signature produced by <see cref="Signature"/> to the settings this build knows,
    /// for "load this save with the mods it was made with". Settings named in the signature take
    /// its value; simulation-affecting settings NOT named take their default, because absence in a
    /// signature means "was at its default when this was saved".
    ///
    /// It cannot load or unload a third-party DLL - that is the process's business, not a
    /// setting's - so "mod:" entries are ignored here and reported to the player instead.
    /// </summary>
    public static void ApplySignature(string signature)
    {
        Dictionary<string, string> wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (signature ?? "").Split(';'))
        {
            string part = raw.Trim();
            if (part.Length == 0 || part.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                wanted[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
        }

        foreach (ModSetting s in registered)
        {
            if (!s.AffectsSimulation)
            {
                continue;
            }
            s.Value = wanted.TryGetValue(s.Id, out string v) ? v : s.StockValue;
        }
    }

    /// <summary>
    /// The mods named by a signature, one per line, for showing a player what a save needs.
    /// Returns null for a signature that describes nothing modded.
    /// </summary>
    public static string Describe(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }
        List<string> lines = new List<string>();
        foreach (string raw in signature.Split(';'))
        {
            string part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            if (part.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(part.Substring(4) + " (mod)");
                continue;
            }
            int eq = part.IndexOf('=');
            string id = eq > 0 ? part.Substring(0, eq) : part;
            string value = eq > 0 ? part.Substring(eq + 1) : "";
            ModSetting known = Find(id);
            lines.Add((known != null ? known.Label : id) +
                      (value.Length > 0 ? ": " + value : ""));
        }
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    /// <summary>
    /// For the tools and the tests: forget everything, as if the process had just started. The
    /// game never calls this - it loads once - but a headless run that wants to try two
    /// configurations in one process does.
    /// </summary>
    public static void Reset()
    {
        registered.Clear();
        byId.Clear();
        stored.Clear();
        storedOrder.Clear();
        loaded = false;
        DataSignature = null;
    }

    /// <summary>
    /// Formats a timestamp with a mod-supplied format string, always in the invariant culture.
    ///
    /// The invariant culture is the point of it. Config.Culture is not a display setting - it is
    /// also the culture used for ToUpper and for the "N" number formats, and it sits next to the
    /// number handling on the XML data path. Choosing a culture to get a date format therefore
    /// chooses a DECIMAL SEPARATOR too, and a comma there is how a data file that says 1.5 gets
    /// read as fifteen. So dates are formatted from an explicit format string with the invariant
    /// culture, and no date preference can ever reach number parsing.
    /// </summary>
    public static string FormatDate(DateTime when, string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        try
        {
            return when.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
