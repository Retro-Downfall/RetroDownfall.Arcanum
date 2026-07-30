using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LongRunningOperationEndpointTests
{
    private readonly ArcanumWebApplicationFactory _factory;

    public LongRunningOperationEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task ListAndShow_ReturnSafeSummariesWithoutCheckpointPayload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        _ = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Indexed 10 safe paths.",
            now));
        _ = await store.TryAcquireLeaseAsync(operation.Id, "api-test", now, now.AddMinutes(1));
        _ = await store.SaveCheckpointAsync(
            operation.Id,
            "api-test",
            0,
            1,
            [115, 101, 99, 114, 101, 116],
            null,
            "Indexed 10 safe paths.",
            now);

        HttpClient client = _factory.CreateAuthenticatedClient();
        HttpResponseMessage listResponse = await client.GetAsync("/api/operations");
        HttpResponseMessage showResponse = await client.GetAsync($"/api/operations/{operation.Id:D}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, showResponse.StatusCode);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        string showJson = await showResponse.Content.ReadAsStringAsync();
        Assert.Contains("Indexed 10 safe paths.", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpointPayload", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2VjcmV0", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpointPayload", showJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2VjcmV0", showJson, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CancelThenRetry_UsesCurrentRevisionAndReturnsUpdatedState()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        _ = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Retry API test.",
            now));
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "api-test",
            now,
            now.AddMinutes(1));

        HttpClient client = _factory.CreateAuthenticatedClient();
        HttpResponseMessage cancel = await client.PostAsync(
            $"/api/operations/{operation.Id:D}/cancel",
            content: null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        LongRunningOperation cancelling = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(LongRunningOperationState.Cancelling, cancelling.State);

        _ = await store.TryTransitionAsync(
            operation.Id,
            cancelling.Revision,
            "api-test",
            LongRunningOperationState.Abandoned,
            now.AddSeconds(1));
        HttpResponseMessage retry = await client.PostAsync(
            $"/api/operations/{operation.Id:D}/retry",
            content: null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        LongRunningOperation pending = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(LongRunningOperationState.Pending, pending.State);
        Assert.True(pending.Revision > leased.Operation.Revision);
    }

    [SkippableFact]
    public async Task OperationsRequireApiKey()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/operations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
