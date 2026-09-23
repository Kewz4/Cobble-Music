namespace CobbleMusicUpdater;

/// <summary>
/// Player-facing status card texts for the memory-settings feature (plain words, no setting names). The card shows the
/// status line from <see cref="UpdateProgress.Message"/> and asks <see cref="DetailFor"/> for the line under it.
/// </summary>
internal static class JvmSettingsText
{
    // Pre-launch card: the three texts of section 10.
    public const string Restarting = "Applying recommended memory settings";
    public const string Deferred = "Memory settings will be updated on a later launch";
    public const string CouldNotApply = "Recommended memory settings could not be applied automatically";

    // Helper card.
    public const string ClosingPrism = "Closing Prism to apply memory settings";
    public const string Reopening = "Memory settings applied - reopening Prism";
    public const string PressPlayAgain = "Press Play again to start Kewz's Cobblemon";
    public const string PrismReopened = "Prism was reopened - press Play again";
    public const string ReopenFailed = "Open Prism and press Play";
    public const string NextPrismStart = "Settings apply next time you start Prism";

    private static readonly IReadOnlyDictionary<string, string> Details = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Restarting] = "Prism will restart and launch Kewz's Cobblemon",
        [Deferred] = "Starting Minecraft with your current settings",
        [CouldNotApply] = "Starting Minecraft with your current settings",
        [ClosingPrism] = "Prism will reopen and launch Kewz's Cobblemon",
        [Reopening] = "Kewz's Cobblemon will start in a moment",
        [PressPlayAgain] = "Memory settings were not changed this time",
        [PrismReopened] = "Memory settings will be checked on the next launch",
        [ReopenFailed] = "Memory settings were applied, but Prism could not be reopened",
        [NextPrismStart] = "A game is still running from Prism"
    };

    public static string DetailFor(string message) => Details.TryGetValue(message, out string? detail) ? detail : string.Empty;

    public static UpdateProgress Progress(string message) => new(UpdatePhase.MemorySettings, message);
}
