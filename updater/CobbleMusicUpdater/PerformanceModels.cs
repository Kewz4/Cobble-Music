namespace CobbleMusicUpdater;

// Updater 1.2.22 lite mode. Everything a lite PC changes is listed in the SIGNED release manifest
// (performanceProfiles.lite), so Kewz can change the lists per release without an updater release.
// The updater itself only knows HOW to apply each kind of change, and which files it may ever touch.

internal sealed class PerformanceProfiles
{
    public LitePerformanceProfile? Lite { get; set; }
}

internal sealed class LitePerformanceProfile
{
    // Stable id of this list revision (same syntax as a player-setting migration id). Informational:
    // every change is still applied once and reversed from the local ledger, never from this id alone.
    public string RevisionId { get; set; } = "";
    public PerformanceDetectionRules Detection { get; set; } = new();
    // Exact managed paths (mods/<name>.jar) that a lite PC keeps as mods/<name>.jar.disabled.
    public List<string> DisabledMods { get; set; } = [];
    // Resource pack ids removed from the two official Packed Packs profiles on a lite PC.
    public List<string> RemovedPackIds { get; set; } = [];
    // One-time, reversible edits of player-owned settings (allow-listed files only).
    public List<PerformanceSetting> Settings { get; set; } = [];
}

internal sealed class PerformanceDetectionRules
{
    // Lines for the built-in hardware table (Kewz, 2026-09-24: "build an entire list of cpus/gpus with score"). A PC
    // is lite when its processor's PassMark single-thread rating is below CpuSingleThreadBelow, or its best real graphics
    // card's PassMark G3D Mark is below GpuScoreBelow. Hardware that is not in the table does not vote.
    // Recommended by the hwdb track: 2500 / 13000 (Jim LITE on both, DONGLORD9000 FULL on both, Kewz's PC FULL).
    public int CpuSingleThreadBelow { get; set; }
    public int GpuScoreBelow { get; set; }
}

internal sealed class PerformanceSetting
{
    public string Path { get; set; } = "";
    // "json" (dotted key path to a scalar), "options" (options.txt key:value), "properties" (key = value).
    public string Format { get; set; } = "";
    public string Key { get; set; } = "";
    // The exact text written as the value (for json: a JSON scalar literal such as "\"FAST\"" or false).
    public string Value { get; set; } = "";
}

internal enum PerformanceMode
{
    Full,
    Lite
}

// Local, per-instance record of every change lite made, so full can undo exactly those changes.
// Stored at <minecraft>/cobble-music-updater/performance-state.json and written only inside a journaled
// transaction together with the files it describes.
internal sealed class PerformanceLedger
{
    public int SchemaVersion { get; set; } = 1;
    public List<PerformanceModEntry> Mods { get; set; } = [];
    public List<PerformanceProfileEntry> Profiles { get; set; } = [];
    public List<PerformanceSettingEntry> Settings { get; set; } = [];
    // Round 2: result of the last dependency check of the lite mod list ("ok:<sha256>" or "refused:<sha256>" over the
    // release's mod jars and the list), so the jars are read once per release and list, not every launch.
    public string ModCheck { get; set; } = "";

    public bool IsEmpty => Mods.Count == 0 && Profiles.Count == 0 && Settings.Count == 0;
}

internal sealed class PerformanceModEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    // "disabled": lite renamed it to .jar.disabled. "playerEnabled": the player turned it back on by hand
    // while lite was active, so lite leaves it alone until the list or the mode changes.
    public string State { get; set; } = "";
}

internal sealed class PerformanceProfileEntry
{
    public string Path { get; set; } = "";
    // The signed revision (managed SHA-256) this filter was applied on top of.
    public string SignedSha256 { get; set; } = "";
    // The exact bytes before lite touched this revision, for a byte-exact restore.
    public string OriginalBase64 { get; set; } = "";
    // The exact bytes lite last wrote (empty when nothing had to be removed).
    public string FilteredSha256 { get; set; } = "";
    public List<string> ListedIds { get; set; } = [];
    public List<PerformanceRemovedPack> Removed { get; set; } = [];
}

internal sealed class PerformanceRemovedPack
{
    public string Id { get; set; } = "";
    public int Index { get; set; }
}

internal sealed class PerformanceSettingEntry
{
    public string Path { get; set; } = "";
    public string Format { get; set; } = "";
    public string Key { get; set; } = "";
    public string PreviousValue { get; set; } = "";
    public string AppliedValue { get; set; } = "";
}

// Machine-wide (per Windows user) record of the last automatic decision and the facts it was made from.
internal sealed class MachinePerformanceRecord
{
    public int SchemaVersion { get; set; } = MachinePerformanceStore.SchemaVersion;
    public DateTimeOffset DecidedAtUtc { get; set; }
    public string UpdaterVersion { get; set; } = "";
    // The inputs: re-decided only when one of these changes.
    public string Fingerprint { get; set; } = "";
    public string DatabaseVersion { get; set; } = "";
    public int CpuSingleThreadBelow { get; set; }
    public int GpuScoreBelow { get; set; }
    // "lite" or "full".
    public string Verdict { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CpuName { get; set; } = "";
    public string CpuKey { get; set; } = "";
    public int? CpuScore { get; set; }
    public List<GpuAdapterInfo> Gpus { get; set; } = [];
}

internal sealed class GpuAdapterInfo
{
    public string Name { get; set; } = "";
    // MatchingDeviceId from the display-class key ("pcien_10de&dev_1e91&subsys_12b41462").
    public string DeviceId { get; set; } = "";
    // First hardware id of the PRESENT device behind this class entry (SetupAPI), when it could be read.
    public string HardwareId { get; set; } = "";
    public long MemoryBytes { get; set; }
    // Display-class subkey ("0001").
    public string RegistryKey { get; set; } = "";
    // True/false from SetupAPI's present-device list; null when that list could not be read.
    public bool? Present { get; set; }
    // Filled when the record is saved: the table key and score this adapter resolved to, or why it was not counted.
    public string TableKey { get; set; } = "";
    public int? Score { get; set; }
    public string Note { get; set; } = "";
}
