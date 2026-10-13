using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The pure Long Rest recovery handler only ever sees the leased row, which is always <c>Running</c>. Lease acquisition therefore
/// records <c>long_rest.readmit_requested</c> on a Long Rest sweep it adopts from <c>Pending</c> with a prior attempt (what
/// <c>arcanum operation retry</c> produces), and on nothing else, so an operator retry is distinguishable from a crash.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationRetryMarkerTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Started = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.LongRestSweep, true)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration, false)]
    public async Task Lease_acquisition_marks_only_a_retried_long_rest_sweep(string kind, bool marked)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await RetriedAsync(store, kind);

        Assert.Equal(LongRunningOperationState.Pending, operation.State);

        Assert.True(operation.AttemptCount > 0);

        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(operation.Id, "recovery-owner", Started.AddMinutes(10), Started.AddMinutes(12));

        Assert.True(leased.Acquired);

        Assert.Equal(LongRunningOperationState.Running, leased.Operation.State);

        Assert.Equal(marked ? LongRestParkCodes.ReadmitRequested : null, leased.Operation.TerminalErrorCode);

        // The verdict that settles the row replaces the marker.
        Assert.True(await store.TryTransitionAsync(operation.Id, leased.Operation.Revision, "recovery-owner",
            LongRunningOperationState.ReconciliationRequired, Started.AddMinutes(11), LongRestParkCodes.Interrupted));

        Assert.Equal(LongRestParkCodes.Interrupted, (await store.GetAsync(operation.Id))!.TerminalErrorCode);
    }

    [SkippableFact]
    public async Task A_first_attempt_and_an_expired_running_row_carry_no_marker()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation fresh = await CreateAsync(store, LongRunningOperationKinds.LongRestSweep);

        LongRunningOperationLeaseResult first = await store.TryAcquireLeaseAsync(fresh.Id, "worker", Started, Started.AddMinutes(1));

        Assert.True(first.Acquired);

        Assert.Null(first.Operation.TerminalErrorCode);

        // A crashed worker's expired Running row is adopted as Running, never as a retry.
        LongRunningOperationLeaseResult adopted = await store.TryAcquireLeaseAsync(fresh.Id, "recovery", Started.AddMinutes(5), Started.AddMinutes(7));

        Assert.True(adopted.Acquired);

        Assert.Equal(2, adopted.Operation.AttemptCount);

        Assert.Null(adopted.Operation.TerminalErrorCode);
    }

    private async Task<LongRunningOperation> RetriedAsync(LongRunningOperationStore store, string kind)
    {
        LongRunningOperation created = await CreateAsync(store, kind);

        LongRunningOperationLeaseResult first = await store.TryAcquireLeaseAsync(created.Id, "worker", Started, Started.AddMinutes(1));

        Assert.True(first.Acquired);

        Assert.True(await store.TryTransitionAsync(created.Id, first.Operation.Revision, "worker",
            LongRunningOperationState.ReconciliationRequired, Started.AddMinutes(2), LongRestParkCodes.Interrupted));

        LongRunningOperation parked = (await store.GetAsync(created.Id))!;

        Assert.True(await store.ResetForRetryAsync(created.Id, parked.Revision, Started.AddMinutes(3)));

        return (await store.GetAsync(created.Id))!;
    }

    private async Task<LongRunningOperation> CreateAsync(LongRunningOperationStore store, string kind)
    {
        // The row is created as a kind the ledger accepts everywhere, then renamed, as the neighbouring store tests do.
        LongRunningOperation created = await store.CreateAsync(new LongRunningOperationCreateRequest(
            Kind: LongRunningOperationKinds.BlobEncryptionMigration,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.RestartIdempotently,
            PublicSummary: "Retry marker.",
            CreatedAt: Started));

        if (kind != LongRunningOperationKinds.BlobEncryptionMigration)
        {
            await using var command = _db!.Database.GetDbConnection().CreateCommand();

            command.CommandText = "UPDATE LongRunningOperations SET Kind = $kind WHERE Id = $id;";

            var kindParameter = command.CreateParameter();

            kindParameter.ParameterName = "$kind";

            kindParameter.Value = kind;

            command.Parameters.Add(kindParameter);

            var idParameter = command.CreateParameter();

            idParameter.ParameterName = "$id";

            idParameter.Value = created.Id.ToString("N");

            command.Parameters.Add(idParameter);

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        return (await store.GetAsync(created.Id))!;
    }
}
