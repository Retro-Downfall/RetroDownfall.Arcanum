using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Rewrites a supported archive at this build's current container format.
/// </summary>
/// <remarks>
/// This is a container migration, not a schema migration: entry bytes are carried across verbatim
/// and the database inside keeps whatever schema it had, because schema convergence belongs to the
/// authoritative installer at restore time. Migrating ahead of a restore is useful when an archive's
/// envelope parameters are old but its contents are fine — and it never touches the source archive,
/// so a failed migration costs nothing.
/// </remarks>
internal static class BackupArchiveMigrator
{

    public static async Task<BackupMigrateResult> MigrateAsync(
        BackupArchiveCodec codec,
        BackupMigrateRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(codec);

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
        {

            return Failed(
                request,
                0,
                new BackupVerifyIssue(
                    "backup.restore_archive_missing",
                    "The named backup archive does not exist.",
                    request.ArchivePath));

        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {

            return Failed(
                request,
                0,
                new BackupVerifyIssue(
                    "backup.migrate_output_required",
                    "Migrating an archive requires an explicit --output path."));

        }

        string archivePath = Path.GetFullPath(request.ArchivePath);

        string outputPath = Path.GetFullPath(request.OutputPath);

        if (string.Equals(
                archivePath,
                outputPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {

            return Failed(
                request,
                0,
                new BackupVerifyIssue(
                    "backup.migrate_output_is_source",
                    "The migrated archive must be written to a different path than the source archive.",
                    outputPath));

        }

        if (!request.Overwrite && File.Exists(outputPath))
        {

            return Failed(
                request,
                0,
                new BackupVerifyIssue(
                    "backup.migrate_output_exists",
                    "The migrated archive destination already exists. Pass --overwrite to replace it.",
                    outputPath));

        }

        int sourceFormat = await BackupArchiveHeaderPeek
            .TryReadFormatVersionAsync(archivePath, cancellationToken)
            .ConfigureAwait(false);

        if (BackupRestoreFormatCatalog.Classify(sourceFormat) is BackupVerifyIssue unsupported)
        {

            return Failed(request, sourceFormat, unsupported);

        }

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The migrated archive path has no parent directory.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(outputDirectory);

        OwnedTemporaryDirectory staging = OwnedTemporaryDirectory.Create(
            Path.Combine(outputDirectory, ".arcanum-backup-migrate-" + Guid.NewGuid().ToString("N")));

        try
        {

            string extractRoot = Path.Combine(staging.Path, "extract");

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(extractRoot);

            BackupArchiveExtraction extraction = await codec.ExtractAsync(
                archivePath,
                recoveryPassphrase,
                extractRoot,
                staging.Path,
                cancellationToken).ConfigureAwait(false);

            if (extraction.Manifest is null)
            {

                return Failed(request, sourceFormat, extraction.Issues);

            }

            BackupArchiveSource[] sources =
            [
                .. extraction.Manifest.Entries.Select(entry => new BackupArchiveSource(
                    entry.Path,
                    Path.Combine(extractRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)))),
            ];

            BackupManifest written = await codec.WriteAsync(
                outputPath,
                extraction.Manifest with { FormatVersion = BackupArchiveFormat.CurrentVersion },
                sources,
                recoveryPassphrase,
                request.Overwrite,
                staging.Path,
                cancellationToken).ConfigureAwait(false);

            return new BackupMigrateResult(
                Migrated: true,
                archivePath,
                outputPath,
                sourceFormat,
                written.FormatVersion,
                new FileInfo(outputPath).Length,
                written,
                Issues: []);

        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            return Failed(
                request,
                sourceFormat,
                new BackupVerifyIssue(
                    "backup.migrate_failed",
                    "The archive could not be rewritten at the current format. The source archive is "
                    + "unchanged.",
                    archivePath));

        }
        finally
        {

            _ = staging.TryDelete();

        }

    }

    private static BackupMigrateResult Failed(
        BackupMigrateRequest request,
        int sourceFormat,
        params BackupVerifyIssue[] issues) =>
        new(
            Migrated: false,
            request.ArchivePath,
            OutputPath: null,
            sourceFormat,
            BackupArchiveFormat.CurrentVersion,
            OutputBytes: 0,
            Manifest: null,
            issues);

}
