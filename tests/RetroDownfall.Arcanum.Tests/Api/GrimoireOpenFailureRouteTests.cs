using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Api.Workspaces;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed partial class GrimoireOpenFailureRouteTests
{
    [SkippableFact]
    public async Task Actual_factory_open_failure_reaches_memory_route_and_health_without_maintenance_or_secrets()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FailingOpen seam = new();

        MaintenanceHostLogCapture logs = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<IGrimoireOrdinaryConnectionFactoryTestSeam>(seam);

                services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        seam.Enabled = true;

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Contains(ErrorCodes.Covenant.Unavailable, body, StringComparison.Ordinal);

        Assert.Contains("526", body, StringComparison.Ordinal);

        Assert.DoesNotContain("maintenance", body, StringComparison.OrdinalIgnoreCase);

        Assert.IsType<GrimoireLivenessProbe>(factory.Services.GetRequiredService<IGrimoireLivenessProbe>()).ClearCacheForTests();

        using HttpResponseMessage healthResponse = await client.GetAsync("/api/health");

        ApiResponse<HealthReportDto>? health = await healthResponse.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        HealthComponentDto grimoire = Assert.Single(health!.Data!.Components, item => item.Name == "Grimoire");

        Assert.Equal(HealthStatus.Unhealthy, grimoire.Status);

        Assert.Contains("526", grimoire.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain("maintenance", grimoire.Detail!, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(FailingOpen.Sentinel, body + grimoire.Detail, StringComparison.Ordinal);

        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("526", StringComparison.Ordinal));

        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(FailingOpen.Sentinel, entry.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(FailingOpen.Sentinel, entry.Exception?.ToString() ?? "", StringComparison.Ordinal);
        });

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        IGrimoireOrdinaryConnectionFactory connections = scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        using CovenantConnectionSource source = new(db, connections);

        await Assert.ThrowsAsync<GrimoireConnectionUnavailableException>(() => source.GetOpenCoreConnectionAsync(CancellationToken.None).AsTask());

        using LongRunningOperationStore store = new(db, connections);

        await Assert.ThrowsAsync<GrimoireConnectionUnavailableException>(() => store.RenewLeaseAsync(
            Guid.NewGuid(), "test-owner", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));

        DivinationResult[] hits = [new("missing", 1, new Dictionary<string, string>())];

        await Assert.ThrowsAsync<GrimoireConnectionUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Tower.ISessionDivinationService>()
                .SearchAsync(new Microsoft.Extensions.AI.Embedding<float>(new float[] { 1, 0, 0 }), null, null, 1, 0, CancellationToken.None));

        await AssertThrowsUnavailable(typeof(WorkspaceDivinationEndpoints), "JoinWorkspaceChunksAsync", null,
            [db, connections, hits, "test-workspace", 1, CancellationToken.None]);

        await AssertThrowsUnavailable(typeof(CovenantCampaignScopeProbe), "HasDeletionEventAsync", null,
            [db, connections, Guid.NewGuid(), CancellationToken.None]);

        WizardIntelligenceProvider wizard = ActivatorUtilities.CreateInstance<WizardIntelligenceProvider>(scope.ServiceProvider);

        await AssertThrowsUnavailable(typeof(WizardIntelligenceProvider), "JoinWorkspaceChunkMetadataAsync", wizard,
            [db, hits, "test-workspace", 1, CancellationToken.None]);

        GrimoireRepository repository = Assert.IsType<GrimoireRepository>(scope.ServiceProvider.GetRequiredService<IGrimoireRepository>());

        await AssertThrowsUnavailable(typeof(GrimoireRepository), "IncrementSessionTokensAndCostWithinImmediateTransactionAsync", repository,
            [Guid.NewGuid(), 1L, 0m, CancellationToken.None]);

        seam.Enabled = false;
    }

    private static async Task AssertThrowsUnavailable(Type type, string method, object? instance, object?[] arguments)
    {
        MethodInfo target = type.GetMethod(method, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task = Assert.IsAssignableFrom<Task>(target.Invoke(instance, arguments));

        GrimoireConnectionUnavailableException failed = await Assert.ThrowsAsync<GrimoireConnectionUnavailableException>(() => task);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, failed.Error.Code);

        Assert.Contains("526", failed.Message, StringComparison.Ordinal);
    }

    private sealed class FailingOpen : IGrimoireOrdinaryConnectionFactoryTestSeam
    {
        internal const string Sentinel = "PRIVATE-path-passphrase-query-SENTINEL";

        internal bool Enabled { get; set; }

        public void BeforeProviderConstruction()
        {
        }

        public ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken) => Enabled
            ? ValueTask.FromException(new SqliteException(Sentinel, 14, 526))
            : ValueTask.CompletedTask;

        public void AfterExactPoolClear(SqliteConnection connection)
        {
        }
    }
}
