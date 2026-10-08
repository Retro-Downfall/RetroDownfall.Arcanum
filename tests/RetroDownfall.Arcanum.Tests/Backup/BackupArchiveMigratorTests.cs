using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The "output is not the source" guard of <c>backup migrate</c>, from the cases the service-level
/// tests cannot reach: a symbolic link to the source, and an output whose identity cannot be read.
/// </summary>
/// <remarks>
/// In the process-global-seam collection because the unreadable-identity case fakes the no-follow
/// metadata lookup, which every other test in the process also reads.
/// </remarks>
[Collection(ProcessGlobalSeamCollectionName.Value)]
public sealed class BackupArchiveMigratorTests : IDisposable
{
    private const string Passphrase = "migrator test passphrase";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-migrator-" + Guid.NewGuid().ToString("N"));

    public BackupArchiveMigratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_different_output_is_migrated()
    {
        string archive = await WriteArchiveAsync("control-source.arcbackup");

        string output = Path.Combine(_root, "control-output.arcbackup");

        BackupMigrateResult result = await MigrateAsync(archive, output, overwrite: false);

        Assert.True(result.Migrated, string.Join(", ", result.Issues.Select(static issue => issue.Code)));

        Assert.True(File.Exists(output));
    }

    /// <summary>
    /// A symbolic link to the source is the source: <c>--overwrite</c> must not turn it into
    /// permission to replace the archive being migrated.
    /// </summary>
    [SkippableFact]
    public async Task An_output_that_is_a_symbolic_link_to_the_source_is_refused()
    {
        string archive = await WriteArchiveAsync("link-source.arcbackup");

        byte[] before = await File.ReadAllBytesAsync(archive);

        string link = Path.Combine(_root, "link-output.arcbackup");

        try
        {
            File.CreateSymbolicLink(link, archive);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, "Creating a symbolic link needs a privilege this lane does not have.");
        }

        BackupMigrateResult result = await MigrateAsync(archive, link, overwrite: true);

        Assert.False(result.Migrated);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.migrate_output_is_source");

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));

        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    /// <summary>
    /// An output that exists but whose volume and file identity cannot be read is refused rather than
    /// assumed to be a different file, because "assumed different" is how a case variant or a link to
    /// the source would be written over with <c>--overwrite</c>.
    /// </summary>
    [Fact]
    public async Task An_existing_output_whose_identity_cannot_be_read_is_refused_not_assumed_different()
    {
        string archive = await WriteArchiveAsync("unreadable-source.arcbackup");

        string output = Path.Combine(_root, "unreadable-output.arcbackup");

        await File.WriteAllTextAsync(output, "an archive that is not the source");

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            string.Equals(path, output, StringComparison.Ordinal)
                ? null
                : FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
                    path,
                    out FileHandleMetadata metadata)
                    ? metadata
                    : null;

        BackupMigrateResult result = await MigrateAsync(archive, output, overwrite: true);

        Assert.False(result.Migrated);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.migrate_output_unverifiable");

        Assert.Equal(
            "an archive that is not the source",
            await File.ReadAllTextAsync(output));
    }

    private static Task<BackupMigrateResult> MigrateAsync(
        string archive,
        string output,
        bool overwrite) =>
        BackupArchiveMigrator.MigrateAsync(
            new BackupArchiveCodec(new BackupArchiveCodecOptions
            {
                KdfIterations = 10_000,
            }),
            new BackupMigrateRequest(archive, output, overwrite),
            Passphrase.AsMemory(),
            CancellationToken.None);

    private async Task<string> WriteArchiveAsync(string name)
    {
        byte[] content = "migrator payload"u8.ToArray();

        BackupManifestEntry entry = new(
            "content/payload.txt",
            content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            BackupComponent.Configuration);

        string archive = Path.Combine(_root, name);

        _ = await new BackupArchiveCodec(new BackupArchiveCodecOptions
        {
            KdfIterations = 10_000,
        }).WriteAsync(
            archive,
            Manifest(entry),
            [BackupArchiveSource.FromMemory(entry.Path, content)],
            Passphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        return archive;
    }

    private static BackupManifest Manifest(BackupManifestEntry entry) =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "1.0.0-test",
            "test-build",
            "20260730040000_AddBlobEncryptionMigrationState",
            DateTimeOffset.UnixEpoch,
            "test-platform",
            new BackupEnvelopeDescriptor(
                "PBKDF2",
                "HMAC-SHA256",
                10_000,
                string.Empty,
                "AES-256-GCM",
                256,
                12,
                16,
                1024 * 1024),
            BackupScope.Full,
            SessionId: null,
            RequestedIncludes: [],
            RequestedExcludes: [],
            SecurityWarnings: [],
            Components: Enum
                .GetValues<BackupComponent>()
                .Select(component => component == BackupComponent.Configuration
                    ? new BackupManifestComponent(
                        component,
                        BackupComponentStatus.Complete,
                        "included",
                        1,
                        entry.Size)
                    : new BackupManifestComponent(
                        component,
                        BackupComponentStatus.OmittedByPolicy,
                        "not selected",
                        0,
                        0))
                .ToArray(),
            Entries: [entry]);
}
