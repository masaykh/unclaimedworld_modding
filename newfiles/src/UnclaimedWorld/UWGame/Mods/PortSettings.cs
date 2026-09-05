namespace UWGame.Mods;

/// <summary>
/// The port's own entries in <c>user/ModSettings.xml</c> - the ones that belong to the modding
/// framework rather than to any one mod, and which therefore exist in every build, including one
/// compiled without the bundled Unhidden Mod and without the Harmony loader.
///
/// They live in the mod settings file rather than in <c>Options.xml</c> because they are about
/// mods: how modded saves are treated, and a date format that only the save list uses. Keeping
/// them here also means the MODS section of the options menu is never empty, so the machinery
/// that renders it is exercised in every build rather than only when a mod is installed.
/// </summary>
public static class PortSettings
{
    /// <summary>The prefix these carry in the file. Not a mod id: nothing can claim it.</summary>
    public const string ModId = "port";

    private static ModSetting saveDateFormat;

    private static ModSetting askAboutModsOnLoad;

    /// <summary>
    /// How a saved game's timestamp is written in the save and load lists.
    ///
    /// Deliberately a FORMAT STRING rather than a culture. <see cref="Config.Culture"/> is not a
    /// display setting - it is also <c>ToUpper</c>, the "N" number formats, and the number
    /// handling next to the XML data path - so choosing a culture to get a date order chooses a
    /// DECIMAL SEPARATOR too, and a comma there is how a data file that says 1.5 gets read as
    /// fifteen. These are formatted with the invariant culture through
    /// <see cref="ModSettings.FormatDate"/>, so the decimal separator stays a period whatever is
    /// chosen here.
    /// </summary>
    public static ModSetting SaveDateFormat =>
        saveDateFormat ?? (saveDateFormat = ModSettings.Choice(
            ModId, "saveDateFormat", "SAVE DATE FORMAT",
            new string[3] { "yyyy-MM-dd HH:mm", "dd-MM-yyyy HH:mm", "MM/dd/yyyy hh:mm tt" },
            defaultValue: "yyyy-MM-dd HH:mm",
            toolTip: "How the date is written in the save and load lists. ISO (yyyy-MM-dd) sorts " +
                     "correctly and cannot be misread. The decimal separator used everywhere " +
                     "else stays a period whatever is chosen here."));

    /// <summary>
    /// Whether loading a save whose modded content differs from this session's asks what to do.
    ///
    /// On, the player is shown what the save was made with and can load it with those settings -
    /// which is the only reliable answer, since a save can name a recipe that a differently
    /// configured build has never built. Off, the current settings are used and the mismatch is
    /// only reported, which is what someone deliberately testing a save against a different
    /// configuration wants.
    /// </summary>
    public static ModSetting AskAboutModsOnLoad =>
        askAboutModsOnLoad ?? (askAboutModsOnLoad = ModSettings.Toggle(
            ModId, "askAboutModsOnLoad", "ASK ABOUT MODS WHEN LOADING", defaultValue: true,
            toolTip: "When a save was made with different modded content, ask whether to load it " +
                     "with the settings it was made with. Off loads with your current settings " +
                     "and only reports the difference."));

    /// <summary>
    /// Registers all of them. Call once at startup, right after <see cref="ModSettings.Load"/>.
    /// Touching each property is what registers it.
    /// </summary>
    public static void RegisterSettings()
    {
        _ = SaveDateFormat;
        _ = AskAboutModsOnLoad;
    }
}
