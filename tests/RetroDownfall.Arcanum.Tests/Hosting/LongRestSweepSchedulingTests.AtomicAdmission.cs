using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class LongRestSweepSchedulingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_scope_insert_rolls_back_the_operation_and_any_stale_scope_retirement(bool replaceStale)
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        Guid? oldId = replaceStale ? await h.RequestAsync(CanonicalCampaignContext.GlobalOnly) : null;

        LongRunningOperation? before = oldId is { } id ? await h.AssertPendingAsync(id) : null;

        object? oldStamp = oldId is { } stamped
            ? await h.F.H.ScalarAsync($"SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = '{stamped:N}';") : null;

        if (replaceStale) ChangeGateAndRestore(h);

        // Inject a real SQLite refusal after the operation insert, inside the admission transaction.
        await h.F.H.ExecuteAsync("""
            CREATE TEMP TRIGGER reject_request_scope BEFORE INSERT ON long_rest_sweeps
            BEGIN SELECT RAISE(ABORT, 'request scope refused'); END;
            """);

        var refused = await h.Coordinator.RequestAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None);

        Assert.Equal(replaceStale ? 1L : 0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        Assert.True(refused.IsFailure);

        Assert.Equal(replaceStale ? 1L : 0L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps;"));

        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations o LEFT JOIN long_rest_sweeps s ON s.OperationId = o.Id WHERE o.Kind = 'long-rest-sweep' AND s.OperationId IS NULL;"));

        if (oldId is { } retained)
        {
            Assert.Equal(before, await h.ReadAsync(retained));

            Assert.Equal(oldStamp, await h.F.H.ScalarAsync($"SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = '{retained:N}';"));

            Assert.Equal((long)LongRestSweepPhase.Capture, await h.F.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{retained:N}';"));

            Assert.False(h.Ownership.IsClaimed(retained));
        }

        Assert.Equal(0, h.F.Handler.SendCount);
    }

    [Fact]
    public async Task Canonical_fence_changed_during_scope_insert_refuses_and_rolls_back_the_whole_admission()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        CanonicalCampaignContext campaign = (await h.CreateCampaignsAsync())[0];

        long registry = await h.CountAsync("SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;");

        // Mutate a real canonical fence after the write, before admission's final validation.
        // This deterministic injection does not assert an external SQLite writer can bypass an immediate transaction.
        await h.F.H.ExecuteAsync("""
            CREATE TEMP TRIGGER change_request_fence AFTER INSERT ON long_rest_sweeps
            BEGIN UPDATE campaign_registry_state SET RegistryEpoch = RegistryEpoch + 1 WHERE StateKey = 1; END;
            """);

        var refused = await h.Coordinator.RequestAsync(campaign, CancellationToken.None);

        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        Assert.True(refused.IsFailure);

        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps;"));

        Assert.Equal(registry, await h.CountAsync("SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"));

        Assert.Equal(0, h.F.Handler.SendCount);
    }
}
