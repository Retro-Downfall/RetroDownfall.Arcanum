using System.Net;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class DataRetentionEndpointTests
{
    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2)]
    public async Task Real_factory_plan_HTTP_preserves_owner_guidance_as_successful_nested_diagnostic(string kind, int version)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = CreateLoopbackAuthenticatedClient(factory);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ILongRunningOperationStore store = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            kind, kind == LongRunningOperationKinds.DataRetentionMutation
                ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete : LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "private-stored-content", now));

        LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(operation.Id, "diagnostic-owner", now, now.AddMinutes(1));

        Assert.True(lease.Acquired);

        Assert.True(await store.SaveCheckpointAsync(operation.Id, "diagnostic-owner", 0, version,
            [1, 2, 3], "private-stored-reference", operation.PublicSummary, now));

        LongRunningOperation before = Assert.IsType<LongRunningOperation>(await store.GetAsync(operation.Id));

        using HttpResponseMessage response = await client.PostAsync("/api/data/factory-reset/plan", JsonContent(
            new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
            ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<DataRetentionPlan> envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Assert.True(envelope.IsSuccess);

        DataRetentionConflict conflict = Assert.Single(envelope.Data!.Conflicts,
            row => row.ResourceId == operation.Id.ToString("D"));

        Assert.Equal("Data.ActiveOperation", conflict.Code);

        Assert.Contains("authenticated offline transition", conflict.Message, StringComparison.Ordinal);

        Assert.Contains(operation.Id.ToString("D"), conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-stored", conflict.Message, StringComparison.Ordinal);

        foreach (string action in new[] { "cancel", "retry" })
        {
            using HttpResponseMessage refused = await client.PostAsync($"/api/operations/{operation.Id:D}/{action}", null);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            ApiResponse<LongRunningOperationDto> refusal = await ReadAsync(refused, ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);

            Assert.Equal("Operation.StateConflict", refusal.Error?.Code);

            Assert.Contains("authenticated offline transition", refusal.Error?.Message, StringComparison.Ordinal);
        }

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(operation.Id));

        Assert.Equal(before with { CheckpointPayload = null }, after with { CheckpointPayload = null });

        Assert.Equal(before.CheckpointPayload, after.CheckpointPayload);
    }
}
