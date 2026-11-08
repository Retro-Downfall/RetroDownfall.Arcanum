using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
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

        // The disabled scheduler also establishes this durable identity before scanning for work
        // to cancel. Settle that idempotent startup write before asserting an unchanged preview.
        await InitializeGuildOperatorAsync(factory);

        string directory = Directory.CreateDirectory(Path.Combine(ArcanumPaths.GrimoireDirectory, ".arcanum-retention-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string path = Path.Combine(directory, stem + "-20000101.jsonl");

        await File.WriteAllTextAsync(path, "retained log");

        using HttpResponseMessage preview = await client.PostAsync("/api/data/factory-reset/plan", JsonContent.Create(new InstallationResetDataPlanRequest(InstallationResetDataScope.Global), ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

        Assert.True(preview.StatusCode == HttpStatusCode.OK,
            $"Plan returned {preview.StatusCode}: {await preview.Content.ReadAsStringAsync()}");

        DataRetentionPlan plan = await MemoryErasureRouteDriver.ReadDataAsync(preview, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpResponseMessage response = await client.PostAsync("/api/data/factory-reset", JsonContent.Create(new FactoryResetRequest("factory-reset", plan.PlanId, requestedOperationId), ArcanumJsonContext.Default.FactoryResetRequest));

        if (response.StatusCode != HttpStatusCode.OK)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            LongRunningOperationRequestIdentityMatch? observed = await scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .FindByRequestedOperationIdAsync(requestedOperationId);

            string operationEvidence = observed is null ? "No request mapping exists."
                : $"Actual operation {observed.Operation.Id}: {observed.Operation.Kind}, {observed.Operation.State}, {observed.Operation.TerminalErrorCode}.";

            using HttpResponseMessage after = await client.PostAsync("/api/data/factory-reset/plan", JsonContent.Create(new InstallationResetDataPlanRequest(InstallationResetDataScope.Global), ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

            Assert.Fail(
                $"Apply returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
                + $"\nRequested operation {requestedOperationId}: {operationEvidence}"
                + $"\nOriginal preview: {await preview.Content.ReadAsStringAsync()}"
                + $"\nFailure-only preview: {after.StatusCode}: {await after.Content.ReadAsStringAsync()}");
        }

        DataRetentionApplyResult applied = await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.True(applied.Reconciled);

        Assert.False(File.Exists(path));

        Assert.False(Directory.Exists(directory));

        DataRetentionPlanItem logs = Assert.Single(plan.Items, item => item.DataClass == dataClass);

        Assert.Equal(1, logs.Files);

        Assert.Equal(12, logs.EstimatedBytes);
    }

    [SkippableFact]
    public async Task Log_inventory_factory_route_refuses_a_preview_when_Guild_authority_is_initialized_afterwards()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        Action<IServiceCollection>? configure = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            configure?.Invoke(services);

            // Drive the scheduler's real identity initialization explicitly after the preview.
            ServiceDescriptor scheduler = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Method.ReturnType
                    == typeof(InstallationResetRecoveryAwareHostedService<GuildLocalQuestScheduler>));

            services.Remove(scheduler);
        };

        using HttpClient client = new(factory.Server.CreateHandler(LoopbackTransportPeer.Apply))
        {
            BaseAddress = new Uri("http://localhost"),
        };

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        string directory = Directory.CreateDirectory(Path.Combine(ArcanumPaths.GrimoireDirectory,
            ".arcanum-retention-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string path = Path.Combine(directory, "audit-20000101.jsonl");

        await File.WriteAllTextAsync(path, "retained log");

        using HttpResponseMessage preview = await client.PostAsync("/api/data/factory-reset/plan",
            JsonContent.Create(new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
                ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

        Assert.True(preview.StatusCode == HttpStatusCode.OK,
            $"Plan returned {preview.StatusCode}: {await preview.Content.ReadAsStringAsync()}");

        DataRetentionPlan plan = await MemoryErasureRouteDriver.ReadDataAsync(preview, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        await InitializeGuildOperatorAsync(factory);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpResponseMessage response = await client.PostAsync("/api/data/factory-reset",
            JsonContent.Create(new FactoryResetRequest("factory-reset", plan.PlanId, requestedOperationId),
                ArcanumJsonContext.Default.FactoryResetRequest));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Equal(ErrorCodes.Data.PlanChanged, await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));

        Assert.True(Directory.Exists(directory));

        Assert.Equal("retained log", await File.ReadAllTextAsync(path));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
            .FindByRequestedOperationIdAsync(requestedOperationId));
    }

    private static async Task InitializeGuildOperatorAsync(ArcanumWebApplicationFactory factory)
    {
        var initialized = await factory.Services.GetRequiredService<GuildLocalHostPolicy>()
            .CreateNativeOperatorAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(initialized.IsSuccess, initialized.IsFailure ? initialized.Error.Message : null);
    }
}
