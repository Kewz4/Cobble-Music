using System.Globalization;
using System.Security.Cryptography;

namespace CobbleMusicUpdater;

internal static class HistoricalManifestPolicy
{
    // Immutable, already published manifests. Signature verification happens
    // before this exception is registered. It permits reading old manifests,
    // never installing their retired generated/private defaults.
    private static readonly HashSet<string> ManifestHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        "78890c0b6427c86bd3a5b59c88624bab78da09f08e1de160e307db9ef4580531",
        "1d152b3f33ca8d57b4d162433641a6ef6e77b6f91d970c2f08c7d723f2b50f8f",
        "6e00451c9c27ad0e5648bb9f350b7e8c7fc45ae9cceefa0bbc60ee9cdba1fa0c",
        "2999a69644ee1580fffb9a2d146f6755a13f7ea2242d8e5da78dd9867ec36f67",
        "485377958696874bc29df44f48b39bc37b54aa72d75e241425113fc0963fc5f1",
        "1631d00f895dc4b8b3a333f1dc107a6f1f0bc3f475ed54427d1120dc7e7ad0ac",
        "743574f76ea7962d60c734a31eb23991068c21c7a224d335e3446b4fa57fcd9b"
    };

    private static readonly HashSet<string> RetiredPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "config/cobbreeding/encryption",
        "config/defaultoptions-common.toml.bak1",
        "config/dreamdisplays/config.yml",
        "config/etf_warnings.json",
        "config/jade/usernamecache.json",
        "config/packed_packs/__version.json",
        "config/sodium-fingerprint.json",
        "config/spark/activity.json",
        "config/spark/tmp-client/about.txt",
        "config/spark/tmp/about.txt",
        "config/waystones-common.toml.bak1",
        "config/waystones-common.toml.bak2",
        "config/zoomify.json"
    };

    internal static string Identity(ManifestFile file) =>
        file.Path + "|" + file.Size.ToString(CultureInfo.InvariantCulture) + "|" + file.Sha256.ToLowerInvariant();

    internal static void RegisterVerifiedManifest(UpdateManifest manifest, byte[] bytes)
    {
        if (!ManifestHashes.Contains(Convert.ToHexString(SHA256.HashData(bytes)))) return;
        // Releases 1.0.7-1.0.12 accidentally managed Iris's mutable sidecars.
        // Honor the ownership correction while crossing those immutable releases,
        // not only after reaching 1.0.13. Missing defaults are offered by 1.0.13.
        // Registration is bound to exact signed manifest bytes AND file identity;
        // it cannot exempt arbitrary files in a newly published manifest.
        foreach (ManifestFile file in manifest.Files)
        {
            if (file.Path.StartsWith("shaderpacks/", StringComparison.Ordinal)
                && file.Path.EndsWith(".txt", StringComparison.Ordinal)
                && file.Path.Count(character => character == '/') == 1
                && PathSafety.IsSeedAllowed(file.Path))
                manifest.VerifiedPlayerOwnedIdentities.Add(Identity(file));
        }
        foreach (ManifestFile file in manifest.SeedFiles)
        {
            if (RetiredPaths.Contains(file.Path))
                manifest.VerifiedRetiredSeedIdentities.Add(Identity(file));
        }
    }

    internal static bool IsPlayerOwned(UpdateManifest manifest, ManifestFile file) =>
        manifest.VerifiedPlayerOwnedIdentities.Contains(Identity(file));

    // A ledger is metadata, not authority to read, copy, or delete a file.
    internal static bool IsLedgerPathAllowed(string path) =>
        PathSafety.IsSeedAllowed(path) || RetiredPaths.Contains(path);
}
