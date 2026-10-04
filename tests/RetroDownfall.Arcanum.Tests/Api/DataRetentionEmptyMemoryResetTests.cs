using System.Net;
using System.Net.Http.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Resetting a store that holds nothing is a successful no-op, and a preview of an empty store still
/// refuses once rows appear.
/// </summary>
/// <remarks>
/// An empty store previews no candidate. Applying that preview used to fail its own candidate check and
/// answer 409 "Memory data changed after preview" when nothing had changed, so an operator resetting an
/// already-empty store saw a conflict. The preview stays the authority: a store that gains rows after an
/// empty preview is still refused rather than cleared unseen.
/// </remarks>
[Collection("ApiHost")]
public sealed class DataRetentionEmptyMemoryResetTests
{
    private static CancellationToken Token => CancellationToken.None;

    public static TheoryData<MemoryResetScope, bool> EmptyScopes() =>
        new()
        {
            { MemoryResetScope.Entry, false },
            { MemoryResetScope.Attachments, false },
            { MemoryResetScope.Workspace, false },
            { MemoryResetScope.Saga, false },
            { MemoryResetScope.Lexicon, false },
            { MemoryResetScope.Saga, true },
            { MemoryResetScope.Lexicon, true },
        };

    [SkippableTheory]
    [MemberData(nameof(EmptyScopes))]
    public async Task An_empty_store_resets_as_a_no_op_success(MemoryResetScope scope, bool forCampaign)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid? campaign = forCampaign ? await RegisterCampaignAsync(factory, client) : null;

        MemoryResetRequest request = new(scope, CampaignId: campaign);

        DataRetentionPlan plan = await PlanAsync(client, request);

        Assert.Empty(plan.Items);

        Assert.Empty(plan.CandidateIds);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        using HttpResponseMessage reset = await client.PostAsync(
            "/api/data/memory/reset",
            JsonContent.Create(request with { ExpectedPlanId = plan.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest),
            Token);

        Assert.True(reset.StatusCode == HttpStatusCode.OK, $"{(int)reset.StatusCode}: {await reset.Content.ReadAsStringAsync(Token)}");

        DataRetentionApplyResult result = await MemoryErasureRouteDriver.ReadDataAsync(
            reset,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.Equal(plan.PlanId, result.PlanId);

        Assert.Equal(
            (0L, 0L, 0L, 0L),
            (result.RowsDeleted, result.FilesDeleted, result.EstimatedBytesDeleted, result.DerivedRecordsDeleted));

        Assert.True(result.Reconciled);

        Assert.Empty(result.Blockers);

        Assert.Empty(result.Conflicts);
    }

    /// <summary>
    /// A store that was empty at preview and holds a memory at apply is refused, and the memory stays.
    /// </summary>
    [SkippableFact]
    public async Task A_reset_previewed_empty_refuses_once_rows_appear()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        using HttpClient client = factory.CreateAuthenticatedClient();

        MemoryResetRequest request = new(MemoryResetScope.Saga);

        DataRetentionPlan plan = await PlanAsync(client, request);

        Assert.Empty(plan.CandidateIds);

        string memory = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "The ferry leaves at noon.", ct: Token);

        using HttpResponseMessage reset = await client.PostAsync(
            "/api/data/memory/reset",
            JsonContent.Create(request with { ExpectedPlanId = plan.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);

        Assert.Equal(ErrorCodes.Data.PlanChanged, await MemoryErasureRouteDriver.ReadErrorCodeAsync(reset));

        Assert.Equal(1L, await CountSagaAsync(factory, memory));
    }

    /// <summary>How many Saga memories carry this id. Assertion-only.</summary>
    private static async Task<long> CountSagaAsync(ArcanumWebApplicationFactory factory, string id)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, Token);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM saga_memories WHERE Id = $id;";

        _ = command.Parameters.AddWithValue("$id", id);

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<DataRetentionPlan> PlanAsync(HttpClient client, MemoryResetRequest request)
    {
        using HttpResponseMessage planned = await client.PostAsync(
            "/api/data/memory/reset/plan",
            JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest),
            Token);

        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(planned, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);
    }

    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {
        string path = Path.Combine(factory.TempHome, "empty-reset-campaign");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest("Empty reset", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest),
            Token);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }
}
