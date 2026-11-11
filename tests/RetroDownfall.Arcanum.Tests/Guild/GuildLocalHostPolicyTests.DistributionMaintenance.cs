using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("guild_work_awards")]
    [InlineData("guild_inbox")]
    public async Task Distribution_maintenance_refuses_new_awards_and_drains_admitted_awards_once(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        string[] tables = ["guild_quests", "guild_signets", "guild_content", "guild_budget_accounts",
            "guild_budget_evidence", "guild_quest_budget", "guild_cohorts", "guild_cohort_member_quests",
            "guild_work_offers", "guild_work_bids", "guild_work_awards", "guild_work_mutations",
            "guild_quest_start_intents", "guild_portal_streams", "guild_portal_outbox", "guild_event_streams", "guild_events",
            "guild_envelopes", "guild_inbox", "guild_distribution_notifications"];

        var before = await DistributionMaintenanceSnapshotAsync(f, tables);

        DistributionMaintenanceConnections connections = new(f.Connections);

        using SignetAuthority authority = new(new(connections), f.Gate, f.Policy, TimeProvider.System);

        GuildQuestStore quests = new(new(connections, f.Gate, TimeProvider.System), authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, readiness: f.Readiness);

        GuildCohortStore cohorts = new(new(connections, f.Gate, TimeProvider.System), authority,
            quests, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        IGrimoireClosingOwner? closing = null;

        IGrimoireExclusiveClosedLease? closed = null;

        Task<Result<WorkAwardDto>>? admitted = null;

        WorkAwardDto? accepted = null;

        bool triggerInstalled = false;

        List<Exception> failures = [];

        try
        {
            if (boundary != "closed")
            {
                // Observe an actual INSERT after award effect admission has already won. The
                // observer grants nothing and writes nothing: it signals and pauses that command.
                await f.ExecuteAsync($"""
                    CREATE TRIGGER fixture_distribution_maintenance AFTER INSERT ON {boundary}
                    BEGIN SELECT fixture_distribution_maintenance(); END;
                    """);

                triggerInstalled = true;

                admitted = Task.Run(() => cohorts.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id,
                    setup.Request, default));

                // This timeout only bounds a broken-fixture hang; event signals order the test.
                await connections.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            var begun = f.Gate.BeginOrResumeExclusive(PeriodicHostMaintenance.Owner());

            Assert.True(begun.IsSuccess, begun.Error.Message);

            closing = begun.Value;

            Task<Result> drain = f.Gate.DrainRequestAndWorkAsync(closing, default).AsTask();

            if (admitted is not null)
            {
                // Maintenance defers revocation while the winning effect group finishes. It
                // cannot complete drain while the award's actual write transaction is paused.
                Assert.False(drain.IsCompleted, "An admitted award must stay joined to drain through commit or rollback and connection disposal.");

                connections.Release.TrySetResult();

                var completed = await admitted.WaitAsync(TimeSpan.FromSeconds(10));

                if (boundary == "guild_work_awards")
                {
                    // The effect group protects its own lifetime, but the later Circle
                    // recipient grant check needs a fresh policy admission for the primary.
                    // Closing refuses that independent admission; the whole award must roll
                    // back, including all provisional allocation and receipt/publication rows.
                    Assert.True(completed.IsFailure);

                    Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, completed.Error.Code);
                }
                else
                {
                    // By the inbox pause, that fresh grant check has already succeeded.
                    // The admitted transaction may finish atomically while drain waits.
                    Assert.True(completed.IsSuccess, completed.Error.Message);

                    accepted = completed.Value;

                    Assert.False(accepted.Receipt!.Replayed);
                }
            }

            Result drained = await drain.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(drained.IsSuccess, drained.Error.Message);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);

            Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(f.Gate));

            var closure = await f.Gate.CloseConnectionAdmissionAsync(closing, default);

            Assert.True(closure.IsSuccess, closure.Error.Message);

            closed = closure.Value;

            int opens = f.Connections.Kinds.Count;

            var refused = await cohorts.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id,
                setup.Request, default);

            Assert.Equal(ErrorCodes.Guild.MaintenanceDeferred, refused.Error.Code);

            Assert.Equal(opens, f.Connections.Kinds.Count);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            connections.Release.TrySetResult();

            // Each cleanup is attempted even if joining, reopening or disposal fails. Preserve
            // the primary assertion first, then report any independent cleanup failures with it.
            try
            {
                await CaptureDistributionMaintenanceFailureAsync(async () =>
                {
                    if (admitted is not null) await admitted.WaitAsync(TimeSpan.FromSeconds(10));
                }, failures);
            }
            finally
            {
                try
                {
                    await CaptureDistributionMaintenanceFailureAsync(async () =>
                    {
                        if (closed is not null)
                        {
                            try
                            {
                                await CaptureDistributionMaintenanceFailureAsync(async () =>
                                {
                                    Result reopened = await closed.CompleteAsync(
                                        CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None);

                                    Assert.True(reopened.IsSuccess, reopened.Error.Message);
                                }, failures);
                            }
                            finally
                            {
                                await CaptureDistributionMaintenanceFailureAsync(
                                    async () => await closed.DisposeAsync(), failures);
                            }
                        }
                        else if (closing is not null)
                        {
                            // This fixture's maintenance owner only began/drained admission;
                            // it performed no destructive maintenance effect or stage-two commit.
                            Result aborted = await f.Gate.AbortClosingAsync(closing,
                                static _ => ValueTask.FromResult(true), CancellationToken.None);

                            Assert.True(aborted.IsSuccess, aborted.Error.Message);
                        }
                    }, failures);
                }
                finally
                {
                    try
                    {
                        await CaptureDistributionMaintenanceFailureAsync(async () =>
                        {
                            if (closing is not null) await closing.DisposeAsync();
                        }, failures);
                    }
                    finally
                    {
                        await CaptureDistributionMaintenanceFailureAsync(async () =>
                        {
                            if (triggerInstalled) await f.ExecuteAsync("DROP TRIGGER fixture_distribution_maintenance");
                        }, failures);
                    }
                }
            }
        }

        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();

        if (failures.Count > 1) throw new AggregateException("Distribution maintenance assertion and cleanup failures.", failures);

        var afterMaintenance = await DistributionMaintenanceSnapshotAsync(f, tables);

        if (boundary != "guild_inbox")
        {
            Assert.Null(accepted);

            foreach (string table in tables) Assert.Equal(before[table], afterMaintenance[table]);

            // Both admission refusal and a later failed authority check leave no acceptance.
            // Retry the SAME request after reopen: changing its key or ExpectedRevision would
            // conceal an incorrectly retained partial mutation, receipt or allocation.
            var retried = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id,
                setup.Request, default);

            Assert.True(retried.IsSuccess, retried.Error.Message);

            accepted = retried.Value;
        }

        Assert.NotNull(accepted);

        Assert.False(accepted.Receipt!.Replayed);

        Assert.Equal(setup.Request.IdempotencyKey, accepted.Receipt.IdempotencyKey);

        var once = await DistributionMaintenanceSnapshotAsync(f, tables);

        var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id,
            setup.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(accepted.Id, replay.Value.Id);

        Assert.Equal(accepted.QuestId, replay.Value.QuestId);

        Assert.Equal(accepted.Receipt.Id, replay.Value.Receipt.Id);

        var twice = await DistributionMaintenanceSnapshotAsync(f, tables);

        foreach (string table in tables) Assert.Equal(once[table], twice[table]);

        foreach (string table in new[] { "guild_quests", "guild_signets", "guild_budget_accounts",
            "guild_cohort_member_quests", "guild_work_awards", "guild_work_mutations", "guild_envelopes",
            "guild_inbox", "guild_distribution_notifications" })
        {
            Assert.Equal(before[table].Length + 1, once[table].Length);
        }

        string offerId = setup.Offer.Id.ToString("D").ToUpperInvariant();

        string awardId = accepted.Id.ToString("D").ToUpperInvariant();

        string questId = accepted.QuestId.ToString("D").ToUpperInvariant();

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_offers WHERE Id='{offerId}' AND StateCode=2 AND AwardId='{awardId}' AND QuestId='{questId}'"));
    }

    private static async Task CaptureDistributionMaintenanceFailureAsync(Func<Task> cleanup, List<Exception> failures)
    {
        try
        {
            await cleanup();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    private static async Task<Dictionary<string, string[]>> DistributionMaintenanceSnapshotAsync(Fixture f, string[] tables)
    {
        Dictionary<string, string[]> snapshot = [];

        await using SqliteConnection connection = await f.OpenAsync();

        foreach (string table in tables)
        {
            // Every table name is a fixed test literal; include full existing account/evidence
            // rows, not only counts, to detect leaked balance/reservation/revision mutations.
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT * FROM {table}";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            List<string> rows = [];

            while (await reader.ReadAsync())
            {
                List<string> fields = [];

                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    fields.Add(reader.GetValue(ordinal) switch
                    {
                        DBNull => "null",
                        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                        string text => "text:" + text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text,
                        long number => "integer:" + number.ToString(CultureInfo.InvariantCulture),
                        double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidOperationException("Unexpected fixture SQLite storage class."),
                    });
                }

                rows.Add(string.Join("|", fields.Select(static field => field.Length.ToString(CultureInfo.InvariantCulture) + ":" + field)));
            }

            rows.Sort(StringComparer.Ordinal);

            snapshot.Add(table, [.. rows]);
        }

        return snapshot;
    }

    private sealed class DistributionMaintenanceConnections(IGrimoireOrdinaryConnectionFactory inner) : IGrimoireOrdinaryConnectionFactory
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _armed = 1;

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) =>
            inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            var acquired = await inner.OpenFreshAsync(kind, cancellationToken);

            if (acquired.IsFailure) return acquired.Error;

            try
            {
                acquired.Value.Connection.CreateFunction("fixture_distribution_maintenance", () =>
                {
                    if (Interlocked.Exchange(ref _armed, 0) == 1)
                    {
                        Reached.TrySetResult();

                        Release.Task.GetAwaiter().GetResult();
                    }

                    return 0;
                });

                return acquired;
            }
            catch
            {
                await acquired.Value.DisposeAsync();

                throw;
            }
        }
    }
}
