using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ProviderDiagnosticsPureReadTests
{

    [Fact]
    public async Task Provider_reachability_uses_non_mutating_credential_resolution()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "alpha",

                    Endpoint = "https://example.test/v1",

                },
            ],

        };

        MigrationSensitiveResolver resolver = new();

        CapturingProbe probe = new();

        ProviderReachabilityCheck check = new(
            Options.Create(settings),
            resolver,
            probe);

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Healthy, finding.Outcome);

        Assert.Equal(0, resolver.PersistentMutationCount);

        Assert.Equal("peek-secret", probe.ApiKey);

    }

    private sealed class MigrationSensitiveResolver : IProviderApiKeyResolver
    {

        public int PersistentMutationCount { get; private set; }

        public Task<string?> ResolveAsync(
            ProviderSettings provider,
            CancellationToken cancellationToken = default)
        {

            PersistentMutationCount++;

            return Task.FromResult<string?>("migration-secret");

        }

        public Task<string?> PeekAsync(
            ProviderSettings provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("peek-secret");

    }

    private sealed class CapturingProbe : ISetupProviderProbe
    {

        public string? ApiKey { get; private set; }

        public Task<SetupConnectivityResult> ProbeAsync(
            string? endpoint,
            string? model,
            string? apiKey,
            CancellationToken cancellationToken)
        {

            ApiKey = apiKey;

            return Task.FromResult(new SetupConnectivityResult(
                SetupConnectivityStatus.Reachable,
                LatencyMs: 1,
                ModelsAdvertised: 1,
                SelectedModelAdvertised: true,
                Detail: "reachable"));

        }

    }

}
