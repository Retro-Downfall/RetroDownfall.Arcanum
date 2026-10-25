using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Tests.Intelligence;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// What the coordinators add over the sweeps they drive: a lease of their own, and a commit.
/// </summary>
/// <remarks>
/// The algorithms are proven by the workers' own suites, which compose a lease and a transaction by
/// hand. That composition is exactly what had no production equivalent, so what is worth proving here
/// is the part the suites were standing in for — that the coordinator acquires the gate lease itself,
/// opens its own transaction, and leaves the batch durable rather than rolled back. A sweep that ran
/// and did not commit would look identical to one that ran, in every log line it writes.
/// </remarks>
public sealed class CovenantMaintenanceCoordinatorTests
{
    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task Empty_cleanup_preserves_a_live_delegation_grant()
    {
        await using CovenantCanonicalFixture fixture = await CleanupFixtureAsync();

        await using var delegation = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using MemoryDelegationRoot root = await delegation.PrepareAsync();

        CovenantOwnerCleanupCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantCleanupWorker(),
            DetachedAvailabilityRepublisher.Create(),
            delegation.Coordinator);

        Result<CovenantCleanupOutcome> outcome = await coordinator.RunBatchAsync(64, Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        Assert.False(root.Revocation.IsCancellationRequested);

        Result<MemoryDelegationChild> consumed = await delegation.Coordinator.ConsumeAsync(
            root, delegation.RootId, Guid.NewGuid(), delegation.Digest);

        Assert.True(consumed.IsSuccess, consumed.IsFailure ? consumed.Error.Message : null);

        await using MemoryDelegationChild child = consumed.Value;
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_cleanup_or_full_sweep_debt_still_drains_delegated_effects_before_the_transaction(bool fullSweep)
    {
        await using CovenantCanonicalFixture fixture = await CleanupFixtureAsync();

        await using var delegation = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using MemoryDelegationRoot root = await delegation.PrepareAsync();

        await using MemoryDelegationChild child = (await delegation.Coordinator.ConsumeAsync(
            root, delegation.RootId, Guid.NewGuid(), delegation.Digest)).Value;

        await using MemoryDelegationEffect effect = (await delegation.Coordinator.AdmitEffectAsync(root)).Value;

        if (fullSweep)
        {
            await using SqliteCommand command = fixture.Connection.CreateCommand();

            command.CommandText = "UPDATE capability_cleanup_state SET FullSweepRequired = 1 WHERE CapabilityFamilyCode = 1;";

            _ = await command.ExecuteNonQueryAsync(Token);
        }
        else
        {
            Guid campaign = CovenantOperationGateFixture.CampaignOne;

            await fixture.AddCampaignAsync(campaign, "deleted", Token);

            await using SqliteCommand command = fixture.Connection.CreateCommand();

            command.CommandText = "DELETE FROM Campaigns;";

            _ = await command.ExecuteNonQueryAsync(Token);
        }

        CovenantOwnerCleanupCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantCleanupWorker(),
            DetachedAvailabilityRepublisher.Create(),
            delegation.Coordinator);

        TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration = root.Revocation.Register(() => revoked.TrySetResult());

        Task<Result<CovenantCleanupOutcome>> pass = coordinator.RunBatchAsync(64, Token).AsTask();

        try
        {
            await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(pass.IsCompleted);

            // The scope connection has no transaction while delegated physical effects drain.
            await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();

            await transaction.RollbackAsync(Token);

            Assert.Equal(0, await CovenantCapacityFixture.ScalarAsync(fixture,
                "SELECT AppliedCampaignSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;", Token));
        }
        finally
        {
            await effect.DisposeAsync();
        }

        Result<CovenantCleanupOutcome> outcome = await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        Assert.Equal(fullSweep ? 0 : 1, outcome.Value.CampaignsCleaned);
    }

    [SkippableFact]
    public async Task A_missing_cleanup_cursor_fails_closed_without_revoking_a_live_grant()
    {
        await using CovenantCanonicalFixture fixture = await CleanupFixtureAsync();

        await using var delegation = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using MemoryDelegationRoot root = await delegation.PrepareAsync();

        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;";

            _ = await command.ExecuteNonQueryAsync(Token);
        }

        CovenantOwnerCleanupCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantCleanupWorker(),
            DetachedAvailabilityRepublisher.Create(),
            delegation.Coordinator);

        Result<CovenantCleanupOutcome> outcome = await coordinator.RunBatchAsync(64, Token);

        Assert.True(outcome.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, outcome.Error.Code);

        Assert.False(root.Revocation.IsCancellationRequested);
    }

    [Fact]
    public async Task The_outbox_coordinator_takes_its_own_lease_and_leaves_the_projection_committed()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 4; index++)
        {
            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);
        }

        CovenantSearchOutboxCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantSearchOutboxWorker(),
            DetachedAvailabilityRepublisher.Create());

        // No lease and no transaction passed in. Everything the worker's own suite supplies by hand is
        // supplied here by the thing that supplies it in service.
        Result<CovenantOutboxSyncOutcome> outcome = await coordinator
            .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        Assert.Equal(4, outcome.Value.ProjectionsWritten);

        // The commit is the claim. A second pass over the same installation finds nothing left to
        // apply, which it could only find if the first pass's rows were actually consumed rather than
        // rolled back at the end of a transaction nobody committed.
        Result<CovenantOutboxSyncOutcome> second = await coordinator
            .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, Token);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

        Assert.Equal(0, second.Value.ProjectionsWritten);
    }

    [Fact]
    public async Task The_cleanup_coordinator_takes_its_own_lease_and_reports_an_empty_journal_as_nothing_to_do()
    {
        await using CovenantCanonicalFixture fixture = await CleanupFixtureAsync();

        CovenantOwnerCleanupCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantCleanupWorker(),
            DetachedAvailabilityRepublisher.Create());

        // An installation with nothing deleted is the ordinary case, and it has to be a quiet success
        // rather than a refusal: the driver runs this every pass, and a sweep that reported failure on
        // an empty journal would fill an operator's log with a problem they do not have.
        Result<CovenantCleanupOutcome> outcome = await coordinator
            .RunBatchAsync(CovenantCleanupWorker.DefaultBatchSize, Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        Assert.Equal(0, outcome.Value.CampaignsCleaned);

        Assert.Equal(0, outcome.Value.SessionsCleaned);
    }

    [Fact]
    public async Task The_compaction_coordinator_folds_nothing_when_no_session_has_outgrown_its_tail()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantTurnReceiptCompactionCoordinator coordinator = new(
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantTurnReceiptCompactor());

        // The discovery read is the part that only exists here: the compactor folds one Session and
        // cannot find its own work. On an installation under the ceiling it must select nothing, so a
        // pass costs one bounded query rather than a fold per Session that did not need one.
        Result<CovenantReceiptCompactionOutcome> outcome = await coordinator
            .CompactAsync(CovenantTurnReceiptCompactionCoordinator.DefaultSessionsPerPass, Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        Assert.Equal(0, outcome.Value.SessionsFolded);

        Assert.Equal(0, outcome.Value.ReceiptsFolded);
    }

    /// <summary>
    /// The owner-cleanup composition, from the suite that already had one.
    /// </summary>
    /// <remarks>
    /// The cleanup family needs its journal, its guards, and its cursor row, none of which the search
    /// composition installs. Reused rather than reinvented, because a second idea of which core objects
    /// the sweep needs is a second idea of what it is allowed to touch.
    /// </remarks>
    private static async Task<CovenantCanonicalFixture> CleanupFixtureAsync()
    {
        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            Token,
            coreObjects:
            [
                .. CovenantCapacityFixture.CoreObjects,

                "capability_cleanup_state",

                "owner_deletion_events",

                "owner_deletion_operation_intents",

                "Campaigns_owner_deletion_event",

                "Sessions_owner_deletion_event",

                "owner_deletion_events_guard_delete",

                "owner_deletion_events_guard_update",
            ]);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO capability_cleanup_state
                (CapabilityFamilyCode, AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired, UpdatedAtUtc)
            VALUES (1, 0, 0, 0, '2026-01-01T00:00:00.0000000Z');
            """;

        _ = await command.ExecuteNonQueryAsync(Token);

        return fixture;
    }
}
