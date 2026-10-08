namespace DungeonsModLoader.App.ViewModels.Setup;

/// <summary>The pages of the first-run setup wizard, in order.</summary>
public enum SetupStep
{
    GameFolder = 0,
    ExistingMods = 1,

    /// <summary>Match the mods found in ~mods with their Nexus Mods pages (skipped when there are none).</summary>
    LinkMods = 2,
    Nexus = 3,
    Done = 4,
}
