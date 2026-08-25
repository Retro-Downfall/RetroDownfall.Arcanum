using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

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
            new CovenantSearchOutboxWorker());

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
            new CovenantCleanupWorker());

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
