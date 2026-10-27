using System.Net;
using System.Net.Http.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class DataRetentionLogInventoryTests
{
    [SkippableTheory]
    [InlineData("audit", RetentionDataClass.AuditLogs)]
    [InlineData("guardrails", RetentionDataClass.GuardrailLogs)]
    public async Task Log_inventory_current_factory_route_removes_retention_quarantined_logs(string stem, RetentionDataClass dataClass)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        using HttpClient client = new(factory.Server.CreateHandler(LoopbackTransportPeer.Apply))
        {
            BaseAddress = new Uri("http://localhost"),
        };

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        string directory = Directory.CreateDirectory(Path.Combine(ArcanumPaths.GrimoireDirectory, ".arcanum-retention-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string path = Path.Combine(directory, stem + "-20000101.jsonl");

        await File.WriteAllTextAsync(path, "retained log");

        using HttpResponseMessage preview = await client.PostAsync("/api/data/factory-reset/plan", JsonContent.Create(new InstallationResetDataPlanRequest(InstallationResetDataScope.Global), ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        DataRetentionPlan plan = await MemoryErasureRouteDriver.ReadDataAsync(preview, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        using HttpResponseMessage response = await client.PostAsync("/api/data/factory-reset", JsonContent.Create(new FactoryResetRequest("factory-reset", plan.PlanId, Guid.NewGuid()), ArcanumJsonContext.Default.FactoryResetRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        DataRetentionApplyResult applied = await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.True(applied.Reconciled);

        Assert.False(File.Exists(path));

        Assert.False(Directory.Exists(directory));

        DataRetentionPlanItem logs = Assert.Single(plan.Items, item => item.DataClass == dataClass);

        Assert.Equal(1, logs.Files);

        Assert.Equal(12, logs.EstimatedBytes);
    }
}
