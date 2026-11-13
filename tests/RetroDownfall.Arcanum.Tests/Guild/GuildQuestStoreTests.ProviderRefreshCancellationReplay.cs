using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Accepted_provider_receipt_precedes_cancellation_and_needs_no_source_or_credentials()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        f.Query.CommandText = "DELETE FROM BillableOperations";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid operationId = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation running = (await operations.GetAsync(operationId))!;

        Assert.True(await operations.RequestCancellationAsync(operationId, running.Revision, DateTimeOffset.UtcNow));

        LongRunningOperation cancelling = (await operations.GetAsync(operationId))!;

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(operationId, out Guid claim));

        CountingRefreshResolver resolver = new();

        using SpendMetadataHandler handler = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler), null!, null!, operations, ownership,
            TimeProvider.System, new GuildSpendRefreshTracker());

        var outcome = await service.RecoverClaimedAsync(f.Scenario.Fixture.Caller, cancelling, default);

        Assert.Equal(LongRunningOperationState.Completed, outcome.State);

        Assert.Equal(0, resolver.Reads);

        Assert.Null(handler.Uri);

        Assert.True(ownership.Release(operationId, claim));
    }
}
