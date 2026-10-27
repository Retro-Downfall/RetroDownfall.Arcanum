using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class DataRetentionRefusalContractTests
{
    [SkippableTheory]
    [InlineData(MemoryResetScope.Entry)]
    [InlineData(MemoryResetScope.Attachments)]
    [InlineData(MemoryResetScope.Workspace)]
    public async Task Refusal_contract_real_reset_route_reports_invalid_campaign_scope_as_bad_request(MemoryResetScope scope)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        using HttpClient client = factory.CreateAuthenticatedClient();

        MemoryResetRequest request = new(scope, CampaignId: Guid.NewGuid());

        using HttpResponseMessage preview = await client.PostAsync("/api/data/memory/reset/plan", JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        DataRetentionPlan plan = await MemoryErasureRouteDriver.ReadDataAsync(preview, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, Assert.Single(plan.Blockers).ReasonCode);

        using HttpResponseMessage applied = await client.PostAsync("/api/data/memory/reset", JsonContent.Create(request with { ExpectedPlanId = plan.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest));

        Assert.Equal(HttpStatusCode.BadRequest, applied.StatusCode);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, await MemoryErasureRouteDriver.ReadErrorCodeAsync(applied));
    }

    [SkippableTheory]
    [InlineData("/api/data/memory/reset/plan", false)]
    [InlineData("/api/data/memory/reset/plan", true)]
    [InlineData("/api/data/memory/reset", false)]
    [InlineData("/api/data/memory/reset", true)]
    public async Task Targeted_Covenant_reset_real_route_refuses_without_inventory_or_launch(string path, bool expectedPlan)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        using HttpClient client = factory.CreateAuthenticatedClient();

        MemoryResetRequest request = new(MemoryResetScope.Covenant, expectedPlan ? "untrusted-preview" : null, Guid.NewGuid());

        using HttpResponseMessage response = await client.PostAsync(path, JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest));

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");

        Assert.Equal(ErrorCodes.Data.InvalidRequest, await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));

        Assert.DoesNotContain("canonicalRows", body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("operationId", body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("\"covenant\"", body, StringComparison.OrdinalIgnoreCase);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM LongRunningOperations";

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
