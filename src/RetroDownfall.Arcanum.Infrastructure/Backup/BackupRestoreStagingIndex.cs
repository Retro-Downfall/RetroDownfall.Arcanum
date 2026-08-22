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
    public static void Write(string grimoireDirectory, IReadOnlyList<string> stagingRoots)
    {

        ArgumentNullException.ThrowIfNull(stagingRoots);

        string path = PathFor(grimoireDirectory);

        if (stagingRoots.Count == 0)
        {

            Delete(grimoireDirectory);

            return;

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new BackupRestoreStagingIndexRecord(CurrentVersion, [.. stagingRoots]),
            BackupJsonContext.Default.BackupRestoreStagingIndexRecord);

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
        {

            stream.Write(payload);

            stream.Flush(flushToDisk: true);

        }

        File.Move(temporaryPath, path, overwrite: true);

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
