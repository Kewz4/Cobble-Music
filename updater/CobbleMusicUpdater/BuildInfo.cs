namespace CobbleMusicUpdater;

internal static class BuildInfo
{
    public const string ProductName = "Kewz's Cobblemon Updater";
    public const string Version = "1.2.2";
    public const string UserAgent = "CobbleMusicUpdater/" + Version;
    public const string DefaultRepository = "Kewz4/Cobble-Music";
    public const string DefaultModpackId = "cobble-music";

    // This is intentionally compiled into the executable. A local JSON
    // configuration may narrow the managed area, but it can never broaden the
    // updater into player data such as saves, logs, or options.txt.
    public static readonly IReadOnlySet<string> SupportedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mods",
        "resourcepacks",
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts"
    };
}
