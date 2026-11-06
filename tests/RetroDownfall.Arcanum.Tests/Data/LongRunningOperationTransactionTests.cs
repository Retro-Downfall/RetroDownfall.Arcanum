using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationTransactionTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await _db.DisposeAsync();

        SqliteConnection.ClearPool(connection);

        File.Delete(_path);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_checkpoint_and_domain_write_share_the_callers_commit(bool commit)
    {
        RequireSqlCipher();

        SqliteConnection connection = await ConnectionAsync();

        Guid operationId = Guid.NewGuid();

        byte[] payload = [11, 22, 33];

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            LongRunningOperation created = await LongRunningOperationStore.CreateWithinAsync(connection, transaction,
                operationId, Request(), 1, payload, "quest:accepted", CancellationToken.None);

            Assert.Equal(operationId, created.Id);

            Assert.Equal(1, created.CheckpointVersion);

            Assert.Equal(payload, created.CheckpointPayload);

            Assert.Equal("quest:accepted", created.CheckpointReference);

            await using SqliteCommand sibling = connection.CreateCommand();

            sibling.Transaction = transaction;

            sibling.CommandText = "CREATE TABLE quest_atomic_probe(Id INTEGER PRIMARY KEY); INSERT INTO quest_atomic_probe VALUES(7);";

            await sibling.ExecuteNonQueryAsync();

            LongRunningOperation stored = Assert.IsType<LongRunningOperation>(await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, CancellationToken.None));

            Assert.Equal(payload, stored.CheckpointPayload);

            if (commit) await transaction.CommitAsync();
        }

        LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperation? after = await store.GetAsync(operationId);

        Assert.Equal(commit, after is not null);

        await using SqliteCommand probe = connection.CreateCommand();

        probe.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='quest_atomic_probe';";

        Assert.Equal(commit ? 1L : 0L, await probe.ExecuteScalarAsync());

        if (after is not null)
        {
            Assert.Equal(1, after.CheckpointVersion);

            Assert.Equal(payload, after.CheckpointPayload);

            Assert.Equal(LongRunningOperationState.Pending, after.State);

            Assert.Equal(0, after.Revision);
        }
    }

    [SkippableFact]
    public async Task Invalid_creation_policy_leaves_the_callers_transaction_unchanged()
    {
        RequireSqlCipher();

        SqliteConnection connection = await ConnectionAsync();

        Guid operationId = Guid.NewGuid();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await Assert.ThrowsAsync<ArgumentException>(() => LongRunningOperationStore.CreateWithinAsync(connection, transaction,
            operationId, Request() with { RecoveryPolicy = LongRunningOperationRecoveryPolicy.AbandonSafely },
            1, [1], null, CancellationToken.None));

        Assert.Null(await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, CancellationToken.None));
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationState.Completed, true, true)]
    [InlineData(LongRunningOperationState.Failed, true, true)]
    [InlineData(LongRunningOperationState.Abandoned, true, true)]
    [InlineData(LongRunningOperationState.ReconciliationRequired, false, true)]
    [InlineData(LongRunningOperationState.Waiting, false, false)]
    public async Task Transaction_transition_fences_revision_and_owner_and_preserves_ledger_semantics(
        LongRunningOperationState target, bool terminal, bool releasesLease)
    {
        RequireSqlCipher();

        SqliteConnection connection = await ConnectionAsync();

        LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperation created = await store.CreateAsync(Request());

        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(created.Id, "driver", Now, Now.AddMinutes(2));

        Assert.True(leased.Acquired);

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.False(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, created.Id,
                leased.Operation.Revision - 1, "driver", target, Now.AddSeconds(1), "test.outcome", CancellationToken.None));

            Assert.False(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, created.Id,
                leased.Operation.Revision, "wrong-driver", target, Now.AddSeconds(1), "test.outcome", CancellationToken.None));

            Assert.True(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, created.Id,
                leased.Operation.Revision, "driver", target, Now.AddSeconds(1), "test.outcome", CancellationToken.None));
        }

        LongRunningOperation rolledBack = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id));

        Assert.Equal(leased.Operation.Revision, rolledBack.Revision);

        Assert.Equal(LongRunningOperationState.Running, rolledBack.State);

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.True(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, created.Id,
                leased.Operation.Revision, "driver", target, Now.AddSeconds(2), "test.outcome", CancellationToken.None));

            await transaction.CommitAsync();
        }

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id));

        Assert.Equal(target, after.State);

        Assert.Equal(leased.Operation.Revision + 1, after.Revision);

        Assert.Equal(terminal ? Now.AddSeconds(2) : (DateTimeOffset?)null, after.CompletedAt);

        Assert.Equal(releasesLease ? null : "driver", after.LeaseOwner);

        Assert.Equal(releasesLease ? null : (DateTimeOffset?)Now.AddMinutes(2), after.LeaseExpiresAt);

        Assert.Equal("test.outcome", after.TerminalErrorCode);
    }

    [SkippableFact]
    public async Task Transaction_transition_retains_prune_cancellation_while_parking_reconciliation()
    {
        RequireSqlCipher();

        SqliteConnection connection = await ConnectionAsync();

        LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperation created = await store.CreateAsync(Request() with { Kind = LongRunningOperationKinds.DataRetentionPrune });

        Assert.True(await store.RequestCancellationAsync(created.Id, created.Revision, Now));

        LongRunningOperation cancelling = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id));

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.True(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, created.Id,
                cancelling.Revision, null, LongRunningOperationState.ReconciliationRequired, Now.AddSeconds(1),
                ErrorCodes.Data.ReconciliationFailed, CancellationToken.None));

            await transaction.CommitAsync();
        }

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id));

        Assert.Equal(LongRunningOperationState.Cancelling, after.State);

        Assert.Null(after.CompletedAt);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, after.TerminalErrorCode);
    }

    [SkippableFact]
    public async Task Exhausted_revision_refuses_transaction_and_ordinary_transitions_without_numeric_promotion()
    {
        RequireSqlCipher();

        SqliteConnection connection = await ConnectionAsync();

        LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperation operation = await store.CreateAsync(Request());

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.CommandText = "UPDATE LongRunningOperations SET Revision=$revision WHERE Id=$id";

            seed.Parameters.AddWithValue("$revision", long.MaxValue);

            seed.Parameters.AddWithValue("$id", operation.Id.ToString("N"));

            Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.False(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                long.MaxValue, null, LongRunningOperationState.Completed, Now, null, default));

            await transaction.CommitAsync();
        }

        Assert.False(await store.TryTransitionAsync(operation.Id, long.MaxValue, null, LongRunningOperationState.Completed, Now));

        await using SqliteCommand inspect = connection.CreateCommand();

        inspect.CommandText = "SELECT typeof(Revision),Revision,State FROM LongRunningOperations WHERE Id=$id";

        inspect.Parameters.AddWithValue("$id", operation.Id.ToString("N"));

        await using SqliteDataReader reader = await inspect.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal("integer", reader.GetString(0));

        Assert.Equal(long.MaxValue, reader.GetInt64(1));

        Assert.Equal((int)LongRunningOperationState.Pending, reader.GetInt32(2));
    }

    private async Task<SqliteConnection> ConnectionAsync()
    {
        await _db.Database.OpenConnectionAsync();

        return (SqliteConnection)_db.Database.GetDbConnection();
    }

    private static LongRunningOperationCreateRequest Request() => new(LongRunningOperationKinds.BlobEncryptionMigration,
        LongRunningOperationRecoveryPolicy.RestartIdempotently, "Transaction admission.", Now);

    private static void RequireSqlCipher() => Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
}
