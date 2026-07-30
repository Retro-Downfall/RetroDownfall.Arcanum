using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ApiBootstrapperTelemetryTests
{
    [SkippableFact]
    public async Task Production_host_starts_telemetry_and_invokes_canonical_web_tool_through_di()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with
                {
                    WebBrowsing = true,
                },
            },
            ServiceOverrides = static services =>
            {
                services.RemoveAll<IWebResearchApiKeyResolver>();
                services.AddSingleton<IWebResearchApiKeyResolver>(
                    new MissingApiKeyResolver());
            },
        };
        using HttpClient client = factory.CreateClient();
        TagList tags = new()
        {
            { "provider", "bootstrap-test" },
            { "operation", "search" },
            { "outcome", "success" },
        };

        // This occurs before the test explicitly resolves TelemetryService. It is
        // observed only if endpoint mapping eagerly constructed the singleton.
        ArcanumMetrics.WebResearchRequestsTotal.Add(7, tags);

        TelemetryService telemetry =
            factory.Services.GetRequiredService<TelemetryService>();
        IWebResearchProviderCatalog catalog =
            factory.Services.GetRequiredService<IWebResearchProviderCatalog>();
        using IServiceScope scope = factory.Services.CreateScope();
        IBuiltInToolRegistry registry =
            scope.ServiceProvider.GetRequiredService<IBuiltInToolRegistry>();
        using JsonDocument arguments =
            JsonDocument.Parse("""{"query":"current release"}""");
        Result<JsonElement> invocation = await registry.InvokeAsync(
            ArcanumWebSearchTool.ToolName,
            arguments.RootElement,
            CancellationToken.None);

        Assert.True(telemetry.GetSnapshot().WebResearch.Requests >= 7);
        Assert.True(
            catalog.TryGetProvider(
                WebResearchProviderNames.Perplexity,
                out IWebResearchProvider? searchProvider));
        Assert.True(
            (searchProvider.Capabilities & WebResearchCapabilities.Search) != 0);
        Assert.True(
            catalog.TryGetProvider(
                WebResearchProviderNames.LocalHttp,
                out IWebResearchProvider? readProvider));
        Assert.True(
            (readProvider.Capabilities & WebResearchCapabilities.ReadUrl) != 0);
        Assert.Contains(
            ArcanumWebSearchTool.ToolName,
            registry.GetToolNames());
        Assert.True(invocation.IsSuccess);
        Assert.Equal(
            ErrorCodes.WebResearch.MissingCredential,
            invocation.Value.GetProperty("code").GetString());
    }

    private sealed class MissingApiKeyResolver : IWebResearchApiKeyResolver
    {
        public ValueTask<string?> ResolveApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }
}
