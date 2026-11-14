using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Guild;

using static RetroDownfall.Arcanum.Tests.Backup.BackupRestoreServiceTests;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task Restore_refuses_invalid_leased_refresh_revision_before_changing_other_operations(string revision)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            PRAGMA foreign_keys=ON;
            CREATE TABLE LongRunningOperations(Id TEXT PRIMARY KEY, State INTEGER, RootOperationId TEXT, ParentOperationId TEXT,
                LeaseOwner TEXT, LeaseExpiresAt TEXT, Revision, TerminalErrorCode TEXT);
            CREATE TABLE guild_spend_refreshes(OperationId TEXT REFERENCES LongRunningOperations(Id) ON DELETE RESTRICT);
            INSERT INTO LongRunningOperations VALUES('refresh',1,NULL,NULL,'old-host','2099-01-01', {revision},NULL);
            INSERT INTO LongRunningOperations VALUES('ordinary',0,NULL,NULL,NULL,NULL,1,NULL);
            INSERT INTO guild_spend_refreshes VALUES('refresh');
            """;

        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(connection, default));

        command.CommandText = "SELECT count(*) FROM LongRunningOperations WHERE Id='ordinary' AND State=0";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT LeaseOwner FROM LongRunningOperations WHERE Id='refresh'";

        Assert.Equal("old-host", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Restore_keeps_refresh_checkpoint_and_clears_old_lease_even_without_quest_table()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE LongRunningOperations(Id TEXT PRIMARY KEY, State INTEGER, RootOperationId TEXT, ParentOperationId TEXT,
                LeaseOwner TEXT, LeaseExpiresAt TEXT, Revision, TerminalErrorCode TEXT, CheckpointPayload BLOB);
            CREATE TABLE guild_spend_refreshes(OperationId TEXT REFERENCES LongRunningOperations(Id) ON DELETE RESTRICT);
            INSERT INTO LongRunningOperations VALUES('refresh',1,NULL,NULL,'old-host','2099-01-01',7,NULL,x'0102');
            INSERT INTO LongRunningOperations VALUES('ordinary',0,NULL,NULL,NULL,NULL,1,NULL,NULL);
            INSERT INTO guild_spend_refreshes VALUES('refresh');
            """;

        await command.ExecuteNonQueryAsync();

        Assert.Equal(1, await BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(connection, default));

        command.CommandText = "SELECT State || ':' || Revision || ':' || hex(CheckpointPayload) || ':' || (LeaseOwner IS NULL) || ':' || (LeaseExpiresAt IS NULL) FROM LongRunningOperations WHERE Id='refresh'";

        Assert.Equal("1:8:0102:1:1", await command.ExecuteScalarAsync());
    }
}

// This fact installs the real process-global workspace fence through host readiness.
[Collection("WorkspacePathPolicy")]
public sealed class BackupRestoreRefreshRecoveryTests
{
    [Fact]
    public async Task Encrypted_archive_parks_real_admitted_refresh_with_restored_authority()
    {
        using BackupRestoreServiceTests owner = new();

        Fixture fixture = await owner.CreateFixtureAsync();

        Guid operationId;

        string reservation;

        string bills;

        string events;

        string checkpoint;

        long leasedRevision;

        string keyId;

        string epoch;

        string key;

        string source;

        string receipts;

        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, default))
        {
            await using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.CommandText = "UPDATE covenant_authority_state SET InstallationIdentity='12345678-1234-4234-8234-123456789ABC' WHERE StateKey=1";

                await identity.ExecuteNonQueryAsync();
            }

            await using (SqliteCommand binding = connection.CreateCommand())
            {
                using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

                binding.CommandText = "INSERT INTO session_campaign_bindings VALUES($session,1,NULL,'2026-01-01T00:00:00Z')";

                binding.Parameters.AddWithValue("$session", Fixture.SessionId.ToString("D").ToUpperInvariant());

                await binding.ExecuteNonQueryAsync();
            }

            operationId = await GuildLocalHostPolicyTests.AdmitRealRefreshForEncryptedRestoreAsync(connection.ConnectionString, Fixture.SessionId);

            await using (SqliteCommand uncertain = connection.CreateCommand())
            {
                uncertain.CommandText = "SELECT COUNT(*) FROM guild_budget_effects e JOIN BudgetReservations r ON lower(replace(e.ReservationId,'-',''))=lower(replace(r.Id,'-','')) WHERE e.UnknownUsd=1 AND r.Status=$status";

                uncertain.Parameters.AddWithValue("$status", (int)BudgetReservationStatus.UncertainPaid);

                Assert.Equal(1L, await uncertain.ExecuteScalarAsync());
            }

            reservation = await RestoreEvidenceAsync(connection, "SELECT Id || RunId || BudgetPeriod || Status || ExpiresAt || CreatedAt || UpdatedAt || ReservedUsd || ReconciledUsd FROM BudgetReservations");

            bills = await RestoreEvidenceAsync(connection, "SELECT group_concat(Id || ActualCostUsd, '|') FROM BillableOperations");

            events = await RestoreEvidenceAsync(connection, "SELECT group_concat(EventSequence || EventKindCode, '|') FROM guild_events");

            await using (SqliteCommand operation = connection.CreateCommand())
            {
                operation.CommandText = "SELECT hex(CheckpointPayload),Revision FROM LongRunningOperations WHERE Id=$id AND LeaseOwner='crashed'";

                operation.Parameters.AddWithValue("$id", operationId.ToString("N"));

                await using SqliteDataReader row = await operation.ExecuteReaderAsync();

                Assert.True(await row.ReadAsync());

                checkpoint = row.GetString(0);

                leasedRevision = row.GetInt64(1);
            }

            keyId = await RestoreEvidenceAsync(connection, "SELECT KeyId FROM guild_spend_authority_state");

            epoch = await RestoreEvidenceAsync(connection, "SELECT AuthorityEpoch FROM guild_spend_authority_state");

            key = await RestoreEvidenceAsync(connection, "SELECT hex(CredentialKey) FROM guild_spend_authority_state");

            source = await RestoreEvidenceAsync(connection, "SELECT EffectId || SourceId || AuthorityKeyId || AuthorityEpoch || hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence FROM guild_spend_sources");

            receipts = await RestoreEvidenceAsync(connection, "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)");
        }

        string archive = await fixture.CreateBackupAsync("provider-restore.arcbackup");

        InMemoryOsCredentialStore credentials = new();

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(),
                new BackupRestoreJournalAnchorStore(credentials, new BackupRestoreJournalKeyProvider(credentials),
                    new BackupRestoreJournalInstallationIdentityProvider(credentials)),
                new BackupRestoreJournalInstallationIdentityProvider(credentials),
                new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreEffectDigestCalculator()),
        };

        BackupRestoreResult restored = await owner.Restore(new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret }, options).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
            Passphrase.AsMemory(), default);

        Assert.True(restored.Status == BackupRestoreStatus.Completed,
            $"Restore status {restored.Status}: {string.Join("; ", restored.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");

        await using SqliteConnection reopened = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, default);

        Assert.NotEqual(keyId, await RestoreEvidenceAsync(reopened, "SELECT KeyId FROM guild_spend_authority_state"));

        Assert.NotEqual(epoch, await RestoreEvidenceAsync(reopened, "SELECT AuthorityEpoch FROM guild_spend_authority_state"));

        Assert.NotEqual(key, await RestoreEvidenceAsync(reopened, "SELECT hex(CredentialKey) FROM guild_spend_authority_state"));

        Assert.Equal(source, await RestoreEvidenceAsync(reopened, "SELECT EffectId || SourceId || AuthorityKeyId || AuthorityEpoch || hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence FROM guild_spend_sources"));

        Assert.Equal(receipts, await RestoreEvidenceAsync(reopened, "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)"));

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(reopened, default)).IsSuccess);

        Assert.Equal(reservation, await RestoreEvidenceAsync(reopened, "SELECT Id || RunId || BudgetPeriod || Status || ExpiresAt || CreatedAt || UpdatedAt || ReservedUsd || ReconciledUsd FROM BudgetReservations"));

        await using (SqliteCommand operation = reopened.CreateCommand())
        {
            operation.CommandText = "SELECT hex(CheckpointPayload),Revision,LeaseOwner,LeaseExpiresAt FROM LongRunningOperations WHERE Id=$id";

            operation.Parameters.AddWithValue("$id", operationId.ToString("N"));

            await using SqliteDataReader row = await operation.ExecuteReaderAsync();

            Assert.True(await row.ReadAsync());

            Assert.Equal(checkpoint, row.GetString(0));

            Assert.Equal(leasedRevision + 1, row.GetInt64(1));

            Assert.True(row.IsDBNull(2));

            Assert.True(row.IsDBNull(3));
        }

        string restoredConnectionString = reopened.ConnectionString;

        await reopened.DisposeAsync();

        var recovered = await GuildLocalHostPolicyTests.RecoverRestoredRefreshAsync(restoredConnectionString, operationId);

        Assert.Equal(0, recovered.Gets);

        Assert.Equal(operationId, recovered.Operation.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.Operation.State);

        Assert.Equal("Guild.OwnerUnavailable", recovered.Operation.TerminalErrorCode);

        await using SqliteConnection after = await BackupRestoreDatabaseWorker.OpenAsync(fixture.DatabasePath, fixture.GrimoireSecret, false, default);

        Assert.Equal(source, await RestoreEvidenceAsync(after, "SELECT EffectId || SourceId || AuthorityKeyId || AuthorityEpoch || hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence FROM guild_spend_sources"));

        Assert.Equal(reservation, await RestoreEvidenceAsync(after, "SELECT Id || RunId || BudgetPeriod || Status || ExpiresAt || CreatedAt || UpdatedAt || ReservedUsd || ReconciledUsd FROM BudgetReservations"));

        Assert.Equal(bills, await RestoreEvidenceAsync(after, "SELECT group_concat(Id || ActualCostUsd, '|') FROM BillableOperations"));

        Assert.Equal(events, await RestoreEvidenceAsync(after, "SELECT group_concat(EventSequence || EventKindCode, '|') FROM guild_events"));

        Assert.Equal(receipts, await RestoreEvidenceAsync(after, "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)"));
    }
}
