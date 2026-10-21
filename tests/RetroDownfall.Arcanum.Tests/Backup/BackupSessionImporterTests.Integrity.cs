using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupSessionImporterTests
{
    [Fact]
    public async Task A_finalized_archive_imports_with_consumed_capacity_and_nonreplayable_evidence()
    {
        string sourceSecret = await SeedFinalizedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret);

        Assert.Empty(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        Assert.Equal(2, result.Entries);

        Assert.Equal(1, result.Attachments);

        await using SqliteConnection destination = await OpenImportDatabaseAsync(_destinationRoot, destinationSecret);

        await using SqliteCommand read = destination.CreateCommand();

        read.CommandText = """
            SELECT COUNT(*) FROM assistant_entry_finalizations f
            JOIN assistant_finalization_capacity_reservations r
                ON r.AssistantEntryId = f.AssistantEntryId AND r.SessionId = f.SessionId
            JOIN session_turn_quota_state q ON q.SessionId = f.SessionId
            WHERE f.SessionId = $session AND f.OutcomeCode = 3 AND f.FinalReceiptDigest IS NULL
                AND length(f.SourceEvidenceDigest) = 32 AND r.StateCode = 2 AND r.OriginCode = 3
                AND q.ConsumedFinalizationCount = 1 AND q.ReservedFinalizationCount = 0;
            """;

        read.Parameters.AddWithValue("$session", committed.DestinationSessionId);

        Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);

        read.CommandText = "SELECT ConsumedFinalizationCount FROM installation_turn_quota_state;";

        Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);

        read.CommandText = "SELECT PhaseCode FROM protected_session_transfer_intents;";

        Assert.Equal((long)ProtectedSessionTransferPhase.Completed,
            (long)(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Failure_advancing_a_committed_import_preserves_its_receipt_and_closed_owner()
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        await using (SqliteConnection destination = await OpenImportDatabaseAsync(_destinationRoot, destinationSecret))
        {
            await using SqliteCommand inject = destination.CreateCommand();

            inject.CommandText = """
                CREATE TRIGGER fail_reopen_pending BEFORE UPDATE ON protected_session_transfer_intents
                WHEN NEW.PhaseCode = 4
                BEGIN SELECT RAISE(ABORT, 'injected postcommit journal failure'); END;
                """;

            await inject.ExecuteNonQueryAsync();
        }

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret);

        Assert.Single(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(await ReadImportedSessionIdAsync(destinationSecret), committed.DestinationSessionId);

        Assert.Equal(2, result.Entries);

        Assert.Equal(1, result.Attachments);

        await using SqliteConnection readConnection = await OpenImportDatabaseAsync(_destinationRoot, destinationSecret);

        await using SqliteCommand read = readConnection.CreateCommand();

        read.CommandText = "SELECT PhaseCode FROM protected_session_transfer_intents;";

        Assert.Equal((long)ProtectedSessionTransferPhase.DatabaseCommitted, (long)(await read.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_throwing_disposition_preserves_the_current_committed_import(bool throws)
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        ProtectedTransferGate gate = new(_ => throws
            ? throw new IOException("injected disposition exception")
            : new Error(ErrorCodes.Covenant.MaintenanceFailed, "injected disposition refusal"));

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret, gate);

        Assert.Single(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        Assert.Equal(await ReadImportedSessionIdAsync(destinationSecret), committed.DestinationSessionId);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(2, result.Entries);

        Assert.Equal(1, result.Attachments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_failure_or_cancellation_preserves_earlier_committed_imports(bool cancel)
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        using CancellationTokenSource cancellation = new();

        InterruptingTransferStore store = new(cancel ? cancellation : null);

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(
            sourceSecret, destinationSecret, store: store, sessions: [ArchivedSessionId, ArchivedSessionId],
            cancellationToken: cancellation.Token);

        Assert.Single(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        Assert.Equal(await ReadImportedSessionIdAsync(destinationSecret), committed.DestinationSessionId);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(2, result.Entries);

        Assert.Equal(1, result.Attachments);

        Assert.Equal(1, await CountDestinationAsync(destinationSecret, "Sessions"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plain_import_preserves_attachment_entry_association_through_a_cutoff_fork(bool bound)
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        using MemoryStream payload = new("attachment bytes"u8.ToArray());

        EncryptedBlobDescriptor encrypted = await TestEncryptedBlobStore.Create().WriteAsync(
            Path.Combine(_sourceRoot, "attachments", ArchivedSessionId.ToString("N"), "note", "v1", "note.bin"),
            payload, EncryptedBlobPurpose.SessionAttachment);

        await using (SqliteConnection source = await OpenImportDatabaseAsync(_sourceRoot, sourceSecret))
        {
            await using SqliteCommand bind = source.CreateCommand();

            bind.CommandText = "UPDATE SessionAttachments SET EntryId = $entry, EncryptionVersion = $version, EncryptionKeyId = $key;";

            bind.Parameters.AddWithValue("$version", encrypted.Version);

            bind.Parameters.AddWithValue("$key", encrypted.KeyId);

            bind.Parameters.AddWithValue("$entry", bound ? AssistantEntryId.ToString("D") : DBNull.Value);

            await bind.ExecuteNonQueryAsync();
        }

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult imported = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"), Path.Combine(_destinationRoot, "arcanum.db"),
            [ArchivedSessionId], Path.Combine(_sourceRoot, "attachments"), Path.Combine(_destinationRoot, "attachments"),
            destinationSecret, sourceSecret, CancellationToken.None);

        Assert.Empty(imported.Issues);

        await using SqliteConnection destination = await OpenImportDatabaseAsync(_destinationRoot, destinationSecret);

        await using SqliteCommand read = destination.CreateCommand();

        read.CommandText = "SELECT Id FROM Entries WHERE Sequence = 2;";

        Guid cutoff = Guid.Parse((string)(await read.ExecuteScalarAsync())!);

        Assert.NotEqual(AssistantEntryId, cutoff);

        read.CommandText = "SELECT EntryId FROM SessionAttachments;";

        object? association = await read.ExecuteScalarAsync();

        Assert.Equal(bound ? cutoff.ToString("D").ToUpperInvariant() : DBNull.Value, association);

        await using ArcanumDbContext db = new(new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite(destination).UseModel(ArcanumDbContextModel.Instance).Options,
            new TestApiKeySecretStore("unused"), new GrimoireDbPassphraseSource());

        ArcanumSettings settings = new();

        SessionAttachmentStore attachmentStore = new(db, Options.Create(settings),
            Path.Combine(_destinationRoot, "attachments"), TestEncryptedBlobStore.Create());

        SessionRepository sessions = new(db, attachmentStore, new TestOptionsMonitor<ArcanumSettings>(settings),
            FixtureOrdinaryConnectionFactory.For(db));

        Result<Session> forked = await sessions.ForkAsync(ArchivedSessionId,
            new ForkSessionRequest(UpToEntryId: cutoff), CancellationToken.None);

        Assert.True(forked.IsSuccess, forked.Error.Message);

        IReadOnlyList<SessionAttachmentRecord> copied = await attachmentStore.ListBoundAsync(forked.Value.Id);

        if (bound)
        {
            SessionAttachmentRecord attachment = Assert.Single(copied);

            Assert.Equal("attachment bytes"u8.ToArray(), (await attachmentStore.ReadBytesAsync(attachment)).ToArray());
        }
        else
        {
            Assert.Empty(copied);

            Assert.Null(Assert.Single(await attachmentStore.ListBoundAsync(ArchivedSessionId)).EntryId);
        }
    }

    [Fact]
    public async Task A_later_malformed_archive_digest_preserves_earlier_committed_imports()
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        Guid malformedSession = Guid.NewGuid();

        await using (SqliteConnection source = await OpenImportDatabaseAsync(_sourceRoot, sourceSecret))
        {
            await using SqliteCommand seed = source.CreateCommand();

            seed.CommandText = """
                INSERT INTO Sessions (Id, Title, Status, CreatedAt, UpdatedAt)
                VALUES ($session, 'Malformed later Session', 'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO SessionAttachments
                    (Id, SessionId, State, LogicalKey, OriginalFileName, Version, RelativePath,
                        ContentSha256, MimeType, ByteLength, Kind, CreatedAt, SourceKind, SourceStatus, EncryptionVersion)
                VALUES ($attachment, $session, 'Bound', 'bad', 'bad.txt', 1, 'bad.bin', $digest,
                    'text/plain', 16, 'Text', '2026-01-01T00:00:00Z', 'SnapshotOnly', 'NotApplicable', 0);
                """;

            seed.Parameters.AddWithValue("$session", malformedSession);

            seed.Parameters.AddWithValue("$attachment", Guid.NewGuid());

            seed.Parameters.AddWithValue("$digest", new string('z', 64));

            await seed.ExecuteNonQueryAsync();
        }

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret,
            sessions: [ArchivedSessionId, malformedSession]);

        Assert.Single(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        Assert.Equal(await ReadImportedSessionIdAsync(destinationSecret), committed.DestinationSessionId);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(1, await CountDestinationAsync(destinationSecret, "Sessions"));
    }

    private sealed class InterruptingTransferStore(CancellationTokenSource? cancellation) : IProtectedArtifactTransferStore
    {
        private int _calls;

        public async Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitImportedSessionAsync(
            ImportedSessionTransferRequest request,
            ImportedSessionSourceLease sourceLease,
            CovenantProtectedTransferLease transferLease,
            ProtectedSessionImportDestination destination,
            CancellationToken cancellationToken)
        {
            if (++_calls > 1)
            {
                throw new IOException("injected later import failure");
            }

            ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> result = await
                new ProtectedArtifactTransferStore(CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, TestEncryptedBlobStore.Create())
                    .CommitImportedSessionAsync(request, sourceLease, transferLease, destination, cancellationToken);

            cancellation?.Cancel();

            return result;
        }
    }

    [Fact]
    public async Task A_protected_import_authenticates_encrypted_attachment_plaintext_and_preserves_the_envelope()
    {
        string sourceSecret = await SeedFinalizedSourceAsync();

        string payload = await EncryptArchivedAttachmentAsync(sourceSecret);

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret);

        Assert.Empty(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, committed.DestinationSessionId);

        string imported = Path.Combine(_destinationRoot, "attachments", relative);

        Assert.Equal(await File.ReadAllBytesAsync(payload), await File.ReadAllBytesAsync(imported));

        await using Stream plaintext = await TestEncryptedBlobStore.Create().OpenReadAsync(
            imported, EncryptedBlobPurpose.SessionAttachment);

        using StreamReader reader = new(plaintext);

        Assert.Equal("attachment bytes", await reader.ReadToEndAsync());

        Assert.Equal(1, await CountDestinationAsync(destinationSecret, "assistant_entry_finalizations"));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("authentication")]
    [InlineData("key")]
    public async Task Protected_import_refuses_invalid_encrypted_attachment_evidence_without_a_graph(string corruption)
    {
        string sourceSecret = await SeedFinalizedSourceAsync();

        string payload = await EncryptArchivedAttachmentAsync(sourceSecret);

        if (corruption == "authentication")
        {
            byte[] bytes = await File.ReadAllBytesAsync(payload);

            bytes[^1] ^= 1;

            await File.WriteAllBytesAsync(payload, bytes);
        }
        else
        {
            await using SqliteConnection source = await OpenImportDatabaseAsync(_sourceRoot, sourceSecret);

            await using SqliteCommand corrupt = source.CreateCommand();

            corrupt.CommandText = corruption == "plaintext"
                ? "UPDATE SessionAttachments SET ContentSha256 = $value;"
                : "UPDATE SessionAttachments SET EncryptionKeyId = $value;";

            corrupt.Parameters.AddWithValue("$value", corruption == "plaintext" ? new string('0', 64) : "different-key");

            await corrupt.ExecuteNonQueryAsync();
        }

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret);

        Assert.Single(result.Issues);

        Assert.Empty(result.Committed);

        Assert.Equal(0, await CountDestinationAsync(destinationSecret, "Sessions"));

        Assert.Equal(0, await CountDestinationAsync(destinationSecret, "assistant_finalization_capacity_reservations"));
    }

    private async Task<string> EncryptArchivedAttachmentAsync(string sourceSecret)
    {
        string path = Path.Combine(_sourceRoot, "attachments", ArchivedSessionId.ToString("N"), "note", "v1", "note.bin");

        using MemoryStream payload = new("attachment bytes"u8.ToArray());

        EncryptedBlobDescriptor encrypted = await TestEncryptedBlobStore.Create().WriteAsync(
            path, payload, EncryptedBlobPurpose.SessionAttachment);

        await using SqliteConnection source = await OpenImportDatabaseAsync(_sourceRoot, sourceSecret);

        await using SqliteCommand update = source.CreateCommand();

        update.CommandText = "UPDATE SessionAttachments SET EncryptionVersion = $version, EncryptionKeyId = $key;";

        update.Parameters.AddWithValue("$version", encrypted.Version);

        update.Parameters.AddWithValue("$key", encrypted.KeyId);

        await update.ExecuteNonQueryAsync();

        return path;
    }

    private async Task<string> SeedFinalizedSourceAsync()
    {
        string sourceSecret = await SeedObjectRelationalSourceAsync();

        await using (SqliteConnection source = await OpenImportDatabaseAsync(_sourceRoot, sourceSecret))
        {
            await using SqliteTransaction transaction = source.BeginTransaction();

            Result<AssistantFinalizationCapacityReservation> allocated = await new CovenantQuotaGuard()
                .AllocateDirectFinalizationAsync(
                    new DirectFinalizationCapacityRequest(Guid.NewGuid(), ArchivedSessionId, AssistantEntryId,
                        AssistantFinalizationCapacityOrigin.Internal),
                    new CovenantMutationTransaction(source, transaction), CancellationToken.None);

            Assert.True(allocated.IsSuccess, allocated.Error.Message);

            await using SqliteCommand seed = source.CreateCommand();

            seed.Transaction = transaction;

            seed.CommandText = """
                INSERT INTO assistant_entry_finalizations (
                    AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                    ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                    FinalizedAtUtc)
                VALUES ($entry, $session, 1, 0, zeroblob(32), zeroblob(32), NULL, NULL,
                    '2026-01-01T00:00:00.0000000Z');
                """;

            seed.Parameters.AddWithValue("$entry", AssistantEntryId);

            seed.Parameters.AddWithValue("$session", ArchivedSessionId);

            await seed.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }


        return sourceSecret;
    }

    private Task<BackupSessionImportResult> ImportProtectedFixtureAsync(
        string sourceSecret,
        string destinationSecret,
        ICovenantOperationGate? gate = null,
        IProtectedArtifactTransferStore? store = null,
        IReadOnlyList<Guid>? sessions = null,
        CancellationToken cancellationToken = default) =>
        BackupSessionImporter.ImportProtectedAsync(
            new CovenantSelectiveImportServices(gate ?? new ProtectedTransferGate(),
                store ?? new ProtectedArtifactTransferStore(CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, TestEncryptedBlobStore.Create())),
            Path.Combine(_sourceRoot, "arcanum.db"), Path.Combine(_destinationRoot, "arcanum.db"),
            sessions ?? [ArchivedSessionId], Path.Combine(_sourceRoot, "attachments"), Path.Combine(_destinationRoot, "attachments"),
            destinationSecret, sourceSecret, [], cancellationToken);

    private static async Task<SqliteConnection> OpenImportDatabaseAsync(string root, string secret)
    {
        SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(root, "arcanum.db"), secret, readOnly: false, CancellationToken.None);

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            connection, CovenantSqliteConnectionMode.ReadWrite, CancellationToken.None);

        return connection;
    }
}
