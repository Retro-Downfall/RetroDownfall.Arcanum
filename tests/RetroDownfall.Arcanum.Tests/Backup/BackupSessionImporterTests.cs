using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The importer merges into the <em>live</em> installation, so its failure paths are the interesting
/// ones: whatever it wrote outside the destination transaction has to come back out with it.
/// </summary>
public sealed class BackupSessionImporterTests : IDisposable
{

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

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
                    SessionId.ToString(),
                    "note.bin")));

        Assert.Equal(1, await CountSessionsAsync(destinationSecret));

    }

    private async Task<string> SeedSourceAsync()
    {

        string secret = await CreateDatabaseAsync(_sourceRoot);

        string payload = Path.Combine(
            _sourceRoot,
            "attachments",
            SessionId.ToString(),
            "note.bin");

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        await File.WriteAllTextAsync(payload, "attachment bytes");

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            secret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{SessionId}', NULL, 'Archived session', 'active',
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                 "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                 "SourceKind", "SourceStatus", "EncryptionVersion")
            VALUES ('55555555-5555-5555-5555-555555555555', '{SessionId}', 'Bound', 'note',
                    'note.txt', 1, '{SessionId}/note.bin', 'abc', 'text/plain', 16, 'Text',
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

    private static async Task<string> CreateDatabaseAsync(string installationRoot)
    {

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string databasePath = Path.Combine(installationRoot, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(databasePath, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        SQLitePCL.Batteries_V2.Init();

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Pooling = false,

            }.ToString());

        await connection.OpenAsync();

        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            1536,
            logger: null,
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
