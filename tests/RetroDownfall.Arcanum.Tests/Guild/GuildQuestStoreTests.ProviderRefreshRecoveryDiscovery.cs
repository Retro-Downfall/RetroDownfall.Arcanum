using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Accepted_never_attempted_refresh_is_discovered_without_starting_other_pending_kinds()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        Guid operationId = f.OperationId;

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation unrelated = await operations.CreateAsync(new(LongRunningOperationKinds.BlobEncryptionMigration,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "unrelated pending work", now));

        LongRunningOperation actual = (await operations.GetAsync(operationId))!;

        Assert.Equal(LongRunningOperationState.Pending, actual.State);

        Assert.Equal(0, actual.AttemptCount);

        IReadOnlyList<LongRunningOperation> candidates = await operations.FindExpiredAsync(now.AddSeconds(1), 100);

        Assert.Contains(candidates, item => item.Id == operationId);

        Assert.DoesNotContain(candidates, item => item.Id == unrelated.Id);
    }
}
