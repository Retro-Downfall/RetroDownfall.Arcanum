using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
[Trait("Category", "Integration")]
public sealed class ArcanumHealthCheckerScopeTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ArcanumHealthCheckerScopeTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task ArcanumHealthChecker_resolves_under_validate_scopes_and_reflects_provider_count()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();

        ArcanumHealthChecker checker = scope.ServiceProvider.GetRequiredService<ArcanumHealthChecker>();

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        Assert.NotEmpty(report.Components);

        IOptionsMonitor<ArcanumSettings> options =
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        int configuredProviders = (options.CurrentValue.Providers ?? []).Length;

        HealthComponentDto providers = Assert.Single(report.Components, c => c.Name == "Providers");

        if (configuredProviders == 0)
        {

            Assert.Contains("No providers configured", providers.Detail, StringComparison.Ordinal);

        }
        else
        {

            Assert.Contains($"{configuredProviders}", providers.Detail, StringComparison.Ordinal);

            Assert.Contains("reachability is tracked by resilience probes", providers.Detail, StringComparison.Ordinal);

        }

    }

}
