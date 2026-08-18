using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The importer merges into the <em>live</em> installation, so its failure paths are the interesting
/// ones: whatever it wrote outside the destination transaction has to come back out with it.
/// </summary>
public sealed class BackupSessionImporterTests : IDisposable
{

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid CampaignId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-session-import-" + Guid.NewGuid().ToString("N"));

    private readonly string _sourceRoot;

    private readonly string _destinationRoot;

    public BackupSessionImporterTests()
    {

        _sourceRoot = Path.Combine(_root, "archive");

        _destinationRoot = Path.Combine(_root, "installation");

        Directory.CreateDirectory(_sourceRoot);

        Directory.CreateDirectory(_destinationRoot);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task A_cancelled_import_leaves_no_attachment_payloads_in_the_live_installation()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        using CancellationTokenSource cancellation = new();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BackupSessionImporter.ImportAsync(
                Path.Combine(_sourceRoot, "arcanum.db"),
                Path.Combine(_destinationRoot, "arcanum.db"),
                [SessionId],
                Path.Combine(_sourceRoot, "attachments"),
                Path.Combine(_destinationRoot, "attachments"),
                destinationSecret,
                sourceSecret,
                cancellation.Token,
                beforeCommitForTests: cancellation.Cancel));

        Assert.Empty(
            Directory.EnumerateFiles(
                Path.Combine(_destinationRoot, "attachments"),
                "*",
                SearchOption.AllDirectories));

        Assert.Equal(0, await CountSessionsAsync(destinationSecret));

    }

    [Fact]
    public async Task A_completed_import_keeps_its_attachment_payloads()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(1, result.Attachments);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    SessionId.ToString("N"),
                    "note",
                    "v1",
                    "note.bin")));

        Assert.Equal(1, await CountSessionsAsync(destinationSecret));

    }

    /// <summary>
    /// A remapped Session has to take its payload directory with it. If the attachment row keeps the
    /// archived owner segment, the imported Session's rows point into the Session it collided with:
    /// deleting that one takes the import's bytes with it, and deleting the import leaves them behind
    /// forever, because the directory it looks for was never created.
    /// </summary>
    [Fact]
    public async Task A_remapped_Session_owns_the_attachment_directory_its_rows_point_at()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        await SeedCollidingSessionAsync(destinationSecret);

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.RemappedIds);

        Guid imported = await ReadImportedSessionIdAsync(destinationSecret);

        Assert.NotEqual(SessionId, imported);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, imported);

        Assert.StartsWith(imported.ToString("N") + "/", relative, StringComparison.Ordinal);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    relative.Replace('/', Path.DirectorySeparatorChar))));

        // The archived id names the Session already living here. Nothing this import wrote may land
        // under it.
        Assert.False(
            Directory.Exists(
                Path.Combine(_destinationRoot, "attachments", SessionId.ToString("N"))));

    }

    /// <summary>
    /// Payload bytes are the one part of an import no transaction unwinds, so the importer deletes
    /// them itself when it does not commit. It may only delete the ones it wrote: a destination file
    /// it merely collided with belongs to the live installation, and removing it dangles that row
    /// while the returned issue says the destination is unchanged.
    /// </summary>
    [Fact]
    public async Task An_import_never_removes_destination_payload_bytes_it_did_not_write()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        string occupied = Path.Combine(
            _destinationRoot,
            "attachments",
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);

        await File.WriteAllTextAsync(occupied, "bytes the destination already had");

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Equal(
            "bytes the destination already had",
            await File.ReadAllTextAsync(occupied));

        Assert.Empty(result.Issues);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, SessionId);

        Assert.NotEqual(SourceRelativePath, relative);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    relative.Replace('/', Path.DirectorySeparatorChar))));

    }

    [Fact]
    public async Task A_Campaign_bound_Session_with_no_mapping_refuses_before_the_destination_is_opened()
    {

        string sourceSecret = await SeedSourceAsync(campaignBound: true);

        BackupSessionImportResult result = await BackupSessionImporter.ImportProtectedAsync(
            new CovenantSelectiveImportServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new UnreachableTransferStore()),
            Path.Combine(_sourceRoot, "arcanum.db"),
            // Deliberately a path with no database and no KDF sidecar. Opening it would throw rather
            // than refuse, so reaching this refusal at all proves the coverage pass ran first — and
            // therefore that nothing was committed into a live installation before it.
            Path.Combine(_destinationRoot, "never-opened", "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            "destination secret",
            sourceSecret,
            [],
            CancellationToken.None);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal("backup.restore_import_refused", issue.Code);

        // The typed refusal survives the wrap: the operator is still told which archived Campaign is
        // unaccounted for and which option answers it.
        Assert.Contains(CampaignId.ToString("D"), issue.Message, StringComparison.Ordinal);

        Assert.Contains("--map-campaign", issue.Message, StringComparison.Ordinal);

        Assert.Equal(0, result.Sessions);

    }

    private sealed class UnreachableTransferStore : IProtectedArtifactTransferStore
    {

        public Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>
            CommitImportedSessionAsync(
                ImportedSessionTransferRequest request,
                ImportedSessionSourceLease sourceLease,
                CovenantProtectedTransferLease transferLease,
                ProtectedSessionImportDestination destination,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A refused selection commits no protected transfer.");

    }

    // The owner segment is the session id in "N" form, exactly as SessionAttachmentStore writes it.
    // Seeding the dashed form instead would make the importer's id remap look like it worked, which
    // is how a permanent no-op survived here in the first place.
    private static string SourceRelativePath => SessionId.ToString("N") + "/note/v1/note.bin";

    private async Task<string> SeedSourceAsync(bool campaignBound = false)
    {

        string secret = await CreateDatabaseAsync(_sourceRoot);

        string payload = Path.Combine(
            _sourceRoot,
            "attachments",
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        await File.WriteAllTextAsync(payload, "attachment bytes");

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            secret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        string campaignId = campaignBound ? $"'{CampaignId}'" : "NULL";

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{SessionId}', {campaignId}, 'Archived session', 'active',
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                 "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                 "SourceKind", "SourceStatus", "EncryptionVersion")
            VALUES ('55555555-5555-5555-5555-555555555555', '{SessionId}', 'Bound', 'note',
                    'note.txt', 1, '{SourceRelativePath}', 'abc', 'text/plain', 16, 'Text',
                    '2026-01-01T00:00:00Z', 'WorkspaceFile', 'Refreshable', 0);
            """;

        _ = await seed.ExecuteNonQueryAsync();

        return secret;

    }

    private async Task<string> SeedDestinationAsync()
    {

        string secret = await CreateDatabaseAsync(_destinationRoot);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(
            Path.Combine(_destinationRoot, "attachments"));

        return secret;

    }

    /// <summary>Gives the destination a Session under the archived id, so the import must remap.</summary>
    private async Task SeedCollidingSessionAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{SessionId}', NULL, 'The Session already living here', 'active',
                    '2026-02-02T00:00:00Z', '2026-02-02T00:00:00Z');
            """;

        _ = await seed.ExecuteNonQueryAsync();

    }

    private async Task<Guid> ReadImportedSessionIdAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "Id" FROM "Sessions" WHERE "Title" = 'Archived session';
            """;

        return Guid.Parse((string)(await read.ExecuteScalarAsync())!);

    }

    private async Task<string> ReadAttachmentRelativePathAsync(
        string destinationSecret,
        Guid sessionId)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "RelativePath" FROM "SessionAttachments" WHERE "SessionId" = $id;
            """;

        _ = read.Parameters.AddWithValue("$id", sessionId.ToString());

        return (string)(await read.ExecuteScalarAsync())!;

    }

    private static async Task<string> CreateDatabaseAsync(string installationRoot)
    {

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string databasePath = Path.Combine(installationRoot, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(databasePath, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Pooling = false,

            }.ToString(),
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            1536,
            CancellationToken.None);

        return secret;

    }

    private async Task<long> CountSessionsAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand count = connection.CreateCommand();

        count.CommandText = """
            SELECT COUNT(*) FROM "Sessions";
            """;

        return Convert.ToInt64(await count.ExecuteScalarAsync());

    }

}
