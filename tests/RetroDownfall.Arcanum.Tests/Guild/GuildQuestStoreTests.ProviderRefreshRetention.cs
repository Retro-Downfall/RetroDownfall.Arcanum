using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Real_refresh_reader_claim_sets_retention_conflict_until_disposal()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = "DELETE FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        f.Query.CommandText = "SELECT AccountingAssociationId FROM guild_budget_effects WHERE EffectId=$effect";

        Guid run = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        f.Query.CommandText = "UPDATE InferenceRuns SET Status=1,StartedAt='2000-01-01T00:00:00.0000000Z',CompletedAt='2000-01-01T00:00:00.0000000Z' WHERE lower(replace(Id,'-',''))=$run";

        f.Query.Parameters.AddWithValue("$run", run.ToString("N"));

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(f.Connection, null, [run.ToString("N")], default);

        Assert.True(proof[run.ToString("N")]);

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperationOwnership ownership = new();

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        BlockingRefreshStream stream = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(stream)),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, ownership, TimeProvider.System,
            new GuildSpendRefreshTracker());

        ArcanumSettings settings = new();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        DataRetentionService retention = new(db, new TestOptionsMonitor<ArcanumSettings>(settings), operations, TimeProvider.System,
            NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(db), operationOwnership: ownership);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        Assert.DoesNotContain((await retention.PlanAsync(request)).Conflicts,
            c => c.ResourceId == run.ToString("D") && c.Code == ErrorCodes.Guild.ReconciliationRequired);

        Guid refresh = Guid.NewGuid();

        var running = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            f.Query.CommandText = "UPDATE LongRunningOperations SET State=4 WHERE Id=(SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$active)";

            f.Query.Parameters.AddWithValue("$active", refresh.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

            Assert.Contains((await retention.PlanAsync(request)).Conflicts,
                c => c.ResourceId == run.ToString("D") && c.Code == ErrorCodes.Guild.ReconciliationRequired);

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Contains((await retention.PlanAsync(request)).Conflicts,
                c => c.ResourceId == run.ToString("D") && c.Code == ErrorCodes.Guild.ReconciliationRequired);

            stream.ReleaseDispose.TrySetResult();

            await running;

            f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$active";

            Guid operation = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

            Assert.False(ownership.IsClaimed(operation));

            Assert.DoesNotContain((await retention.PlanAsync(request)).Conflicts,
                c => c.ResourceId == run.ToString("D") && c.Code == ErrorCodes.Guild.ReconciliationRequired);
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            await running;
        }
    }
}
