using System.Text.Json;
using System.Text.Json.Serialization;

namespace CobbleMusicUpdater;

internal sealed class UpdaterChannelDescriptor
{
    public int SchemaVersion { get; set; }
    public string ProductId { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Channel { get; set; } = "";
    public string UpdaterVersion { get; set; } = "";
    public string ReleaseTag { get; set; } = "";
    public UpdaterChannelAsset? Updater { get; set; }
}

internal sealed class UpdaterChannelAsset
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal static class UpdaterChannelParser
{
    internal const string ProductId = "cobble-music-updater";
    internal const string StableChannel = "stable";
    internal const string UpdaterAssetName = "CobbleMusicUpdater.exe";
    internal const long MinimumUpdaterBytes = 1024 * 1024;
    internal const long MaximumUpdaterBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static UpdaterChannelDescriptor VerifyAndParse(byte[] rawDescriptor, byte[] rawSignature)
    {
        DetachedSignature? detachedSignature;
        try
        {
            detachedSignature = JsonSerializer.Deserialize<DetachedSignature>(rawSignature, StrictJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Updater channel signature document is invalid.", exception);
        }

        if (detachedSignature is null || !ManifestSecurity.Verify(rawDescriptor, detachedSignature))
        {
            throw new InvalidDataException("Updater channel signature is invalid. No executable was trusted.");
        }

        UpdaterChannelDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<UpdaterChannelDescriptor>(rawDescriptor, StrictJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Signed updater channel document is invalid.", exception);
        }

        Validate(descriptor);
        return descriptor!;
    }

    public static void Validate(UpdaterChannelDescriptor? descriptor)
    {
        if (descriptor is null
            || descriptor.SchemaVersion != 1
            || !string.Equals(descriptor.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Repository, BuildInfo.DefaultRepository, StringComparison.Ordinal)
            || !string.Equals(descriptor.Channel, StableChannel, StringComparison.Ordinal)
            || descriptor.Updater is null)
        {
            throw new InvalidDataException("Signed updater channel does not match this updater.");
        }

        Version parsedVersion = ParseCanonicalVersion(descriptor.UpdaterVersion, "updaterVersion");
        if (!string.Equals(descriptor.ReleaseTag, $"updater-v{parsedVersion.ToString(3)}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed updater channel releaseTag does not match updaterVersion.");
        }

        UpdaterChannelAsset asset = descriptor.Updater;
        if (!string.Equals(asset.Name, UpdaterAssetName, StringComparison.Ordinal)
            || asset.Size < MinimumUpdaterBytes
            || asset.Size > MaximumUpdaterBytes
            || asset.Sha256.Length != 64
            || asset.Sha256.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new InvalidDataException("Signed updater channel contains invalid executable metadata.");
        }
    }

    public static byte[] SerializeCanonical(UpdaterChannelDescriptor descriptor)
    {
        Validate(descriptor);
        return JsonSerializer.SerializeToUtf8Bytes(descriptor, CanonicalJsonOptions);
    }

    private static Version ParseCanonicalVersion(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Version.TryParse(value, out Version? parsed)
            || parsed.Major < 0
            || parsed.Minor < 0
            || parsed.Build < 0
            || parsed.Revision >= 0
            || !string.Equals(parsed.ToString(3), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Signed updater channel {field} is not a canonical three-part version.");
        }
        return parsed;
    }
}
