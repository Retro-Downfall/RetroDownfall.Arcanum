using System.Globalization;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupRestoreCapacity(
    long RequiredBytes,
    long AvailableBytes,
    BackupVerifyIssue? Issue);

/// <summary>
/// Decides, before staging begins, whether the destination volume can hold everything a restore
/// materializes at its peak.
/// </summary>
/// <remarks>
/// A restore writes the archive's contents to the volume <em>twice at once</em>: the decrypted
/// payload temporary grows alongside the extraction tree, and the extraction tree is then copied
/// entry by entry into the staged tree and kept until commit. Nothing is compressed, so each copy is
/// the archive's restored size. The displaced installation costs nothing new — commit renames it into
/// staging on the same volume — but the pre-restore safety backup is a fresh uncompressed archive of
/// it, so the measured installation size stays in the reservation as that allowance.
/// </remarks>
internal static class BackupRestoreCapacityPlanner
{

    /// <summary>Working room for the journal, staging metadata, and filesystem overhead.</summary>
    internal const long HeadroomBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Concurrent materializations of the archive's contents: decrypted payload plus extraction
    /// tree during extraction, then extraction tree plus staged tree through commit.
    /// </summary>
    private const long ConcurrentArchiveCopies = 2;

    public static BackupRestoreCapacity Plan(
        string destinationRoot,
        long restoredBytes,
        long displacedBytes,
        long? availableBytesOverride)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        long required;

        try
        {

            required = checked(
                (restoredBytes * ConcurrentArchiveCopies) + displacedBytes + HeadroomBytes);

        }
        catch (OverflowException)
        {

            required = long.MaxValue;

        }

        long available = availableBytesOverride ?? MeasureAvailableBytes(destinationRoot);

        return available >= required
            ? new BackupRestoreCapacity(required, available, Issue: null)
            : new BackupRestoreCapacity(
                required,
                available,
                new BackupVerifyIssue(
                    "backup.restore_insufficient_disk",
                    $"The destination volume needs {required.ToString(CultureInfo.InvariantCulture)} bytes "
                    + "to extract and stage the restored generation alongside the current installation, "
                    + $"but only {available.ToString(CultureInfo.InvariantCulture)} bytes are free. The "
                    + "current installation was not modified.",
                    destinationRoot));

    }

    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>, walking up to the nearest existing
    /// ancestor so a destination that does not exist yet still reports its future volume.
    /// </summary>
    internal static long MeasureAvailableBytes(string path)
    {

        string? candidate = Path.GetFullPath(path);

        while (!string.IsNullOrEmpty(candidate))
        {

            if (Directory.Exists(candidate))
            {

                try
                {

                    return new DriveInfo(candidate).AvailableFreeSpace;

                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or IOException
                        or UnauthorizedAccessException)
                {

                    return 0;

                }

            }

            candidate = Path.GetDirectoryName(candidate);

        }

        return 0;

    }

    /// <summary>Total bytes of the regular files beneath <paramref name="root"/>.</summary>
    internal static long MeasureDirectoryBytes(string root)
    {

        string full = Path.GetFullPath(root);

        if (!Directory.Exists(full))
        {

            return 0;

        }

        long total = 0;

        try
        {

            foreach (string file in Directory.EnumerateFiles(
                         full,
                         "*",
                         new EnumerationOptions
                         {

                             RecurseSubdirectories = true,

                             IgnoreInaccessible = true,

                             AttributesToSkip = FileAttributes.ReparsePoint,

                         }))
            {

                try
                {

                    total = checked(total + new FileInfo(file).Length);

                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or OverflowException)
                {

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

        }

        return total;

    }

}
