namespace CobbleMusicUpdater;

/// <summary>The result of one instance.cfg edit attempt.</summary>
internal sealed record InstanceCfgEditResult(bool Written, string? BackupPath, string Detail);

/// <summary>
/// Section 9: the atomic instance.cfg edit in Prism's own format. The new text is produced by line-level edits of the parsed
/// document, re-parsed with the same reader and checked before anything is written: every other line is unchanged, our keys
/// decode to exactly the intended values, splitArgs(new JvmArgs) is splitArgs(old) plus our flags, the line count grew only
/// by the inserted keys, and the merge run on the new file finds nothing missing.
/// </summary>
internal static class InstanceCfgEditor
{
    public const string BackupPrefix = ".cobble-music-jvm-";
    public const int BackupsKept = 3;

    /// <summary>Builds and verifies the edited bytes. Throws <see cref="InvalidDataException"/> when any assertion fails.</summary>
    public static byte[] BuildEditedBytes(
        PrismIniDocument original,
        PrismIniDocument global,
        JvmMergeResult merge,
        JvmDesiredState desired,
        ulong totalPhysicalBytes)
    {
        IReadOnlyList<KeyValuePair<string, string>> edits = merge.EncodedEdits();
        if (edits.Count == 0)
        {
            throw new InvalidDataException("There is nothing to write.");
        }
        PrismIniDocument edited = original.WithGeneralValues(edits, out int inserted);
        byte[] bytes = edited.Serialize();
        PrismIniDocument reparsed = PrismIniDocument.Parse(bytes);

        Assert(reparsed.Lines.Count == original.Lines.Count + inserted, "the line count changed by more than the inserted keys");
        Assert(reparsed.HasBom == original.HasBom && reparsed.LineEnding == original.LineEnding
            && reparsed.EndsWithLineEnding == original.EndsWithLineEnding, "the BOM or line endings changed");

        var ourKeys = new HashSet<string>(edits.Select(edit => edit.Key), StringComparer.OrdinalIgnoreCase);
        Assert(OtherLines(original, ourKeys).SequenceEqual(OtherLines(reparsed, ourKeys), StringComparer.Ordinal),
            "a line other than ours changed");

        foreach ((string key, string encoded) in edits)
        {
            string expected = PrismIni.DecodeValue(encoded).Text;
            Assert(reparsed.GetGeneralString(key) == expected, $"{key} does not read back as {expected}");
        }
        if (merge.WriteJvmArgs)
        {
            IReadOnlyList<string> expectedTokens = [.. PrismArgs.Split(merge.OldJvmArgs), .. merge.AddedTokens];
            Assert(PrismArgs.Split(reparsed.GetGeneralString("JvmArgs") ?? string.Empty)
                .SequenceEqual(expectedTokens, StringComparer.Ordinal), "splitArgs(new JvmArgs) is not the old tokens plus ours");
        }

        PrismEffectiveSettings after = PrismEffectiveSettings.Resolve(reparsed, global, totalPhysicalBytes);
        JvmMergeResult again = JvmMerge.Compute(after, desired, totalPhysicalBytes);
        Assert(!again.IsMissing, "the edited file would still be missing: " + string.Join("; ", again.Reasons));
        return bytes;
    }

    /// <summary>
    /// Writes <paramref name="newBytes"/> over <paramref name="configPath"/> atomically: backup (last 3 kept), temp file in the
    /// same folder flushed to disk, a last check that Prism is still not running and the file is still the one we parsed,
    /// File.Replace, then a re-read that must return exactly the new bytes.
    /// </summary>
    public static InstanceCfgEditResult Apply(
        string configPath,
        byte[] originalBytes,
        byte[] newBytes,
        Func<bool> prismIsRunning,
        DateTime localNow)
    {
        string directory = Path.GetDirectoryName(configPath)!;
        string backup = WriteBackup(configPath, originalBytes, localNow);
        PruneBackups(configPath);
        string temporary = Path.Combine(directory, Path.GetFileName(configPath) + BackupPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(newBytes);
                output.Flush(flushToDisk: true);
            }
            if (prismIsRunning())
            {
                return new InstanceCfgEditResult(false, backup, "Prism started again before the write; instance.cfg was not changed");
            }
            if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(originalBytes))
            {
                return new InstanceCfgEditResult(false, backup, "instance.cfg changed while the edit was prepared; it was not changed");
            }
            File.Replace(temporary, configPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(newBytes))
        {
            return new InstanceCfgEditResult(false, backup, "instance.cfg did not read back as written");
        }
        return new InstanceCfgEditResult(true, backup, "instance.cfg updated and verified");
    }

    private static IEnumerable<string> OtherLines(PrismIniDocument document, HashSet<string> ourKeys)
    {
        var ourLineIndexes = new HashSet<int>(document.GeneralEntries
            .Where(entry => ourKeys.Contains(entry.Key))
            .Select(entry => entry.LineIndex));
        return document.Lines.Where((_, index) => !ourLineIndexes.Contains(index));
    }

    private static string WriteBackup(string configPath, byte[] originalBytes, DateTime localNow)
    {
        string stem = configPath + BackupPrefix + localNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (int suffix = 0; ; suffix++)
        {
            string candidate = suffix == 0 ? stem + ".bak" : $"{stem}-{suffix}.bak";
            try
            {
                using var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(originalBytes);
                output.Flush(flushToDisk: true);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && suffix < 100)
            {
                // Two edits in the same second: take the next free name.
            }
        }
    }

    /// <summary>Keeps the newest <see cref="BackupsKept"/> backups; the timestamped names sort chronologically.</summary>
    public static void PruneBackups(string configPath)
    {
        string directory = Path.GetDirectoryName(configPath)!;
        string pattern = Path.GetFileName(configPath) + BackupPrefix + "*.bak";
        foreach (string old in Directory.EnumerateFiles(directory, pattern)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(BackupsKept))
        {
            File.Delete(old);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException("instance.cfg edit check failed: " + message);
        }
    }
}
