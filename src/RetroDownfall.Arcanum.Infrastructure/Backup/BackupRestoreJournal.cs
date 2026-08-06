using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The durable commit journal for one restore. It records the identity of the live root, the staged
/// root, the displaced rollback artifact, and the phase reached, so a process that dies mid-restore
/// leaves behind enough to deterministically finish, roll back, or demand reconciliation — never a
/// mixture of old and new trees.
/// </summary>
/// <remarks>
/// This deliberately does not live in the Grimoire's <c>LongRunningOperations</c> table: a restore
/// replaces that database, so a durable record inside it would vanish exactly when it matters. The
/// journal is a plain owner-only file beside the staging root it describes.
/// </remarks>
internal sealed record BackupRestoreJournalRecord(
    int Version,
    Guid OperationId,
    BackupRestoreConflictMode ConflictMode,
    BackupRestorePhase Phase,
    string LiveRoot,
    string StagedRoot,
    string DisplacedRoot,
    string? SafetyBackupPath,
    string ArchivePath,
    ulong StagingVolumeId,
    ulong StagingFileId);

internal static class BackupRestoreJournal
{

    internal const int CurrentVersion = 1;

    internal const string FileName = "restore-journal.json";

    internal const string StagingPrefix = ".arcanum-restore-";

    internal const string StagedDirectoryName = "staged";

    internal const string DisplacedDirectoryName = "previous";

    internal const string WorkDirectoryName = "work";

    public static BackupRestoreJournalRecord Write(
        string stagingRoot,
        BackupRestoreJournalRecord record)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        ArgumentNullException.ThrowIfNull(record);

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            BackupJsonContext.Default.BackupRestoreJournalRecord);

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
        {

            stream.Write(payload);

            stream.Flush(flushToDisk: true);

        }

        File.Move(temporaryPath, path, overwrite: true);

        return record;

    }

    public static BackupRestoreJournalRecord Advance(
        string stagingRoot,
        BackupRestoreJournalRecord record,
        BackupRestorePhase phase) =>
        Write(stagingRoot, record with { Phase = phase });

    public static BackupRestoreJournalRecord? TryRead(string stagingRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        try
        {

            if (!File.Exists(path))
            {

                return null;

            }

            BackupRestoreJournalRecord? record = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                BackupJsonContext.Default.BackupRestoreJournalRecord);

            return record is null || record.Version != CurrentVersion
                ? null
                : record;

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {

            return null;

        }

    }

    public static bool Exists(string stagingRoot) =>
        TryRead(stagingRoot) is not null;

    public static void Delete(string stagingRoot)
    {

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        try
        {

            File.Delete(path);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

        }

    }

    /// <summary>
    /// Lists the canonically named restore staging roots directly beneath <paramref name="parent"/>
    /// that still hold a readable journal. A directory that merely looks like staging without a
    /// journal is not ours to touch.
    /// </summary>
    public static IReadOnlyList<string> Discover(string parent)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(parent);

        string fullParent = Path.GetFullPath(parent);

        if (!Directory.Exists(fullParent))
        {

            return [];

        }

        List<string> roots = [];

        try
        {

            foreach (string candidate in Directory.EnumerateDirectories(
                         fullParent,
                         StagingPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {

                string full = Path.GetFullPath(candidate);

                if (IsCanonicalStagingName(Path.GetFileName(full)) && Exists(full))
                {

                    roots.Add(full);

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return [.. roots.Order(StringComparer.Ordinal)];

        }

        return [.. roots.Order(StringComparer.Ordinal)];

    }

    public static string CreateStagingName() =>
        StagingPrefix + Guid.NewGuid().ToString("N");

    internal static bool IsCanonicalStagingName(string name)
    {

        if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal))
        {

            return false;

        }

        string suffix = name[StagingPrefix.Length..];

        if (suffix.Length != 32)
        {

            return false;

        }

        foreach (char character in suffix)
        {

            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {

                return false;

            }

        }

        return true;

    }

}
