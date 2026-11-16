using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LocalRuntimeEndpointTests
{
    [SkippableFact]
    public async Task Inspection_returns_capabilities_and_loaded_models_without_management_configuration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeAdapter adapter = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(adapter);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/local-runtimes/DESKTOP");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal("test-version", data.GetProperty("runtimeVersion").GetString());

        Assert.True(data.GetProperty("capabilities").GetProperty("modelResidencyRetention").GetBoolean());

        Assert.False(data.GetProperty("capabilities").GetProperty("kvSnapshotRestore").GetBoolean());

        Assert.False(data.GetProperty("capabilities").GetProperty("beforeUnloadNotification").GetBoolean());

        Assert.Equal("primary", data.GetProperty("loadedModels")[0].GetProperty("name").GetString());

        Assert.DoesNotContain("managementEndpoint", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("CUSTOM_MANAGEMENT_SECRET", json, StringComparison.Ordinal);

        Assert.DoesNotContain("private-management", json, StringComparison.Ordinal);

        Assert.Equal(1, adapter.Inspections);

        Assert.Equal(0, adapter.Retentions);
    }

    [SkippableTheory]
    [InlineData("missing", "ollama", 404, "LocalRuntime.NotFound")]
    [InlineData("desktop", "future", 503, "LocalRuntime.AdapterUnavailable")]
    public async Task Missing_runtime_or_unavailable_adapter_does_not_dispatch(string id, string adapterId, int status, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeAdapter adapter = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(adapter, adapterId);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/local-runtimes/" + id);

        Assert.Equal(status, (int)response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(0, adapter.Inspections);
    }

    [SkippableFact]
    public async Task Inspection_requires_authentication()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeAdapter adapter = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(adapter);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/local-runtimes/desktop");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(0, adapter.Inspections);
    }

    private static ArcanumWebApplicationFactory CreateFactory(FakeAdapter adapter, string adapterId = "ollama") => new()
    {
        SettingsOverride = settings => settings with
        {
            LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://private-management:11434", CredentialEnvironmentVariable = "CUSTOM_MANAGEMENT_SECRET" }],
        },

        ServiceOverrides = services =>
        {
            services.RemoveAll<ILocalRuntimeAdapter>();

            if (adapterId == "ollama") services.AddSingleton<ILocalRuntimeAdapter>(adapter);
        },
    };

    private sealed class FakeAdapter : ILocalRuntimeAdapter
    {
        public string AdapterId => "ollama";

        public int Inspections { get; private set; }

        public int Retentions { get; private set; }

        public Task<Result<LocalRuntimeInspection>> InspectAsync(LocalRuntimeSettings runtime, CancellationToken cancellationToken)
        {
            Inspections++;

            return Task.FromResult(Result<LocalRuntimeInspection>.Success(new("test-version", new(true, false, false), [new("primary", null, null, 123)])));
        }

        public Task<Result> RetainModelAsync(LocalRuntimeSettings runtime, string model, TimeSpan retention, CancellationToken cancellationToken)
        {
            Retentions++;

            throw new InvalidOperationException("Inspection must never retain or load a model.");
        }
    }
}
