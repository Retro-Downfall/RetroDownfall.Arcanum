using System.Text.Json;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupRestoreStagingIndexRecord(
    int Version,
    string[] StagingRoots);

/// <summary>
/// The durable pointer to restore staging roots that do not sit beside the live installation.
/// </summary>
/// <remarks>
/// <see cref="BackupRestoreRecovery"/> sweeps the parent of the Grimoire directory, which is where
/// staging normally lives. A <c>new-profile-root</c> restore cannot stage there: commit must be a
/// same-volume rename onto the destination, so its staging root goes beside the *destination* and the
/// startup sweep can never reach it. This index is written before that staging root is created and
/// removed with it, so a process death still leaves a trail back to the decrypted contents.
/// <para>
/// It lives in the parent of the Grimoire directory for the same reason
/// <see cref="ArcanumMaintenanceLock"/> does: commit renames the Grimoire directory wholesale, so
/// anything stored inside it travels away with the displaced tree.
/// </para>
/// </remarks>
internal static class BackupRestoreStagingIndex
{

    private const int MaximumBytes = 1024 * 1024;

    private const int MaximumRoots = 1024;

    internal const int CurrentVersion = 1;

    internal const string FileNamePrefix = ".arcanum-staging-index";

    public static string PathFor(string grimoireDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(grimoireDirectory);

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(grimoireDirectory));

        string? parent = Path.GetDirectoryName(full);

        string name = Path.GetFileName(full);

        return string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? Path.Combine(full, FileNamePrefix + ".json")
            : Path.Combine(parent, $"{FileNamePrefix}-{name}.json");

    }

    /// <summary>Records a staging root, which must happen before that root is created.</summary>
    public static void Add(string grimoireDirectory, string stagingRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        string full = Normalize(stagingRoot);

        List<string> roots = [.. Read(grimoireDirectory)];

        if (roots.Contains(full, StringComparer.Ordinal))
        {

            return;

        }

        roots.Add(full);

        Write(grimoireDirectory, roots);

    }

    /// <summary>
    /// Forgets a staging root. Best-effort: this runs on the cleanup path, where an unwritable index
    /// costs a stale entry that the next sweep prunes rather than a failed restore.
    /// </summary>
    public static void Remove(string grimoireDirectory, string stagingRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        string full = Normalize(stagingRoot);

        IReadOnlyList<string> roots = Read(grimoireDirectory);

        if (!roots.Contains(full, StringComparer.Ordinal))
        {

            return;

        }

        try
        {

            Write(
                grimoireDirectory,
                [.. roots.Where(candidate => !string.Equals(candidate, full, StringComparison.Ordinal))]);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    public static IReadOnlyList<string> Read(string grimoireDirectory)
    {

        string path = PathFor(grimoireDirectory);

        try
        {

            if (!File.Exists(path))
            {

                return [];

            }

            BackupRestoreStagingIndexRecord? record = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                BackupJsonContext.Default.BackupRestoreStagingIndexRecord);

            return record is null || record.Version != CurrentVersion
                ? []
                : record.StagingRoots;

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {

            return [];

        }

    }

    internal static async Task<Result<BackupRestoreStagingIndexRecord?>>
        InspectAsync(
            string grimoireDirectory,
            CancellationToken cancellationToken)
    {

        string path = PathFor(grimoireDirectory);

        Result<NoFollowPathTopologyKind> topology =
            NoFollowPathTopology.Classify(path);

        if (topology.IsFailure)
        {

            return Failure("The restore staging index topology is indeterminate.");

        }

        if (topology.Value is NoFollowPathTopologyKind.Absent)
        {

            return Result<BackupRestoreStagingIndexRecord?>.Success(null);

        }

        if (topology.Value is not NoFollowPathTopologyKind.RegularFile
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata metadata)
            || metadata.Kind is not FileSystemObjectKind.RegularFile
            || metadata.HardLinkCount != 1
            || !SecureFilePermissions.HasOwnerOnlyPosture(
                path,
                isDirectory: false))
        {

            return Failure(
                "The restore staging index identity or owner-only permissions are unsafe.");

        }

        try
        {

            using SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                    path,
                    MaximumBytes,
                    cancellationToken,
                    metadata.Identity)
                .ConfigureAwait(false);

            if (read.Status is not SecureFileReadStatus.Success)
            {

                return Failure("The restore staging index could not be read safely.");

            }

            BackupRestoreStagingIndexRecord? record = JsonSerializer.Deserialize(
                read.Bytes.Span,
                BackupJsonContext.Default.BackupRestoreStagingIndexRecord);

            if (record is null
                || record.Version != CurrentVersion
                || record.StagingRoots is null
                || record.StagingRoots.Length > MaximumRoots
                || !HasExactCanonicalRootSet(record.StagingRoots))
            {

                return Failure("The restore staging index is malformed.");

            }

            return record;

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {

            return Failure("The restore staging index could not be read safely.");

        }

    }

    /// <summary>Replaces the recorded set, deleting the index once nothing is left to point at.</summary>
    /// <remarks>
    /// The set is bounded here to the same <see cref="MaximumRoots"/> the reader enforces. A writer
    /// with no bound and a reader that refuses anything over one is a file this installation can
    /// produce and then be unable to read — and that refusal is fail-closed all the way out to the
    /// client mutation boundary, so it would not degrade one restore but refuse every mutation on the
    /// installation.
    /// </remarks>
    public static void Write(string grimoireDirectory, IReadOnlyList<string> stagingRoots)
    {

        ArgumentNullException.ThrowIfNull(stagingRoots);

        string path = PathFor(grimoireDirectory);

        IReadOnlyList<string> bounded = WithinBound(stagingRoots);

        if (bounded.Count == 0)
        {

            Delete(grimoireDirectory);

            return;

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new BackupRestoreStagingIndexRecord(CurrentVersion, [.. bounded]),
            BackupJsonContext.Default.BackupRestoreStagingIndexRecord);

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
        {

            stream.Write(payload);

            stream.Flush(flushToDisk: true);

        }

        // Forced, not merely renamed: this file is the only pointer back to a staging root that does
        // not sit beside the live installation, and a rename left in the page cache loses the trail to
        // a decrypted tree rather than only a label.
        BackupRestoreDurablePublication.Publish(temporaryPath, path);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

    }

    public static void Delete(string grimoireDirectory)
    {

        try
        {

            File.Delete(PathFor(grimoireDirectory));

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    /// <summary>
    /// The recorded set trimmed to the bound the reader accepts, newest entries kept.
    /// </summary>
    /// <remarks>
    /// Entries are appended, so the front of the list is the least recent and the back is the root the
    /// caller is about to create — the one entry that must survive, because it is the only pointer to
    /// a tree that does not exist yet.
    ///
    /// <para>Roots whose directory is gone are dropped first, since they are the ones a prune would
    /// have taken anyway — but the newest entry is exempt from that pass, and the exemption is the
    /// whole point rather than a refinement of it. <c>Add</c> writes before its own staging root
    /// exists, so the newest entry is <em>always</em> the absent one: a pass that took the excess from
    /// whatever was absent would take exactly the pointer this index exists to keep, and would do it
    /// on the one installation where every earlier root is still live. Whatever survives that pass is
    /// then cut from the front, which is the least recent end.</para>
    /// </remarks>
    private static List<string> WithinBound(IReadOnlyList<string> stagingRoots)
    {

        if (stagingRoots.Count <= MaximumRoots)
        {

            return [.. stagingRoots];

        }

        int excess = stagingRoots.Count - MaximumRoots;

        int newest = stagingRoots.Count - 1;

        List<string> kept = [];

        for (int index = 0; index < stagingRoots.Count; index++)
        {

            string root = stagingRoots[index];

            if (index != newest && excess > 0 && !Directory.Exists(root))
            {

                excess--;

                continue;

            }

            kept.Add(root);

        }

        if (kept.Count > MaximumRoots)
        {

            // From the front, so the newest entry survives this cut too: it is the last one appended
            // and the only one naming a tree nothing else can find.
            kept.RemoveRange(0, kept.Count - MaximumRoots);

        }

        return kept;

    }

    private static string Normalize(string stagingRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));

    private static bool HasExactCanonicalRootSet(
        IReadOnlyList<string> stagingRoots)
    {

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        HashSet<string> unique = new(comparer);

        foreach (string value in stagingRoots)
        {

            if (string.IsNullOrWhiteSpace(value)
                || !Path.IsPathFullyQualified(value))
            {

                return false;

            }

            string normalized;

            try
            {

                normalized = Normalize(value);

            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {

                return false;

            }

            if (!comparer.Equals(value, normalized)
                || !BackupRestoreJournal.IsCanonicalStagingName(
                    Path.GetFileName(normalized))
                || !unique.Add(normalized))
            {

                return false;

            }

        }

        return true;

    }

    private static Result<BackupRestoreStagingIndexRecord?> Failure(
        string message) =>
        Result<BackupRestoreStagingIndexRecord?>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            message));

}
