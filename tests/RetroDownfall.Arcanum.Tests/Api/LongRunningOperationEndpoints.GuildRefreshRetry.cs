using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class GuildRefreshRetryEndpointTests(ArcanumWebApplicationFactory factory)
{
    [SkippableFact]
    public async Task Retry_explains_that_legacy_spend_refresh_is_retired_and_leaves_operation_unchanged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ILongRunningOperationStore store = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation created = await store.CreateAsync(new(LongRunningOperationKinds.GuildSpendRefresh,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Provider refresh", now));

        Assert.True((await store.TryAcquireLeaseAsync(created.Id, "retry-test", now, now.AddMinutes(2))).Acquired);

        Assert.True(await store.SaveCheckpointAsync(created.Id, "retry-test", 0, 1, [123, 125], null, "Provider refresh", now));

        LongRunningOperation active = (await store.GetAsync(created.Id))!;

        Assert.True(await store.TryTransitionAsync(created.Id, active.Revision, "retry-test",
            LongRunningOperationState.ReconciliationRequired, now, "Guild.Unavailable"));

        LongRunningOperation before = (await store.GetAsync(created.Id))!;

        HttpResponseMessage response = await client.PostAsync($"/api/operations/{created.Id:D}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Contains("Spend refresh has been retired", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        LongRunningOperation after = (await store.GetAsync(created.Id))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, after.State);

        Assert.Equal(before.Revision, after.Revision);

        Assert.Equal(before.TerminalErrorCode, after.TerminalErrorCode);
    }
}
