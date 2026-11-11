using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Owner_cleanup_drives_revoked_creator_to_cancellation_without_rebinding_it()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var current = await f.Store.GetAsync(owner, root.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
            new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        GuildCohortCleanupParticipant cleanup = new(f.Connections, cohorts, f.Policy);

        await using var connection = await f.OpenAsync();

        var advanced = await cleanup.PrepareSealAsync(new(2, root.Quest.SessionId!.Value, null), connection, default);

        Assert.True(advanced.IsFailure);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, advanced.Error.Code);

        Assert.Equal((long)GuildCohortState.Draining, await f.ScalarAsync($"SELECT StateCode FROM guild_cohorts WHERE Id='{cohort.Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT COUNT(*) FROM guild_cohort_stop_frontiers WHERE CohortId='{cohort.Id:D}' COLLATE NOCASE AND Completed=0"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT Closed FROM guild_budget_accounts WHERE AccountId='{cohort.AllocationAccountId:D}' COLLATE NOCASE"));
    }
}
