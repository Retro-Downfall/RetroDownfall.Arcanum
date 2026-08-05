using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Issue #47 — a securely stored inference credential must actually reach the provider transport,
/// so <c>arcanum setup</c> can leave a user ready to run without exporting an environment variable.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ProviderApiKeyResolutionTests : IDisposable
{

    private const string CredentialVariable = "ARCANUM_TEST_PROVIDER_RESOLUTION_KEY";

    private readonly string? _originalCredential =
        global::System.Environment.GetEnvironmentVariable(CredentialVariable);

    public ProviderApiKeyResolutionTests() =>
        global::System.Environment.SetEnvironmentVariable(CredentialVariable, null);

    public void Dispose() =>
        global::System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            _originalCredential);

    [Fact]
    public async Task Chat_client_factory_resolves_the_credential_through_the_shared_resolver()
    {

        ArcanumSettings settings = Settings();

        RecordingResolver resolver = new("stored-chat-secret");

        ChatClientFactory factory = new(
            new StubHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            resolver);

        using ChatClientLease lease = await factory.ResolveClientAsync(
            "gpt-test",
            CancellationToken.None);

        Assert.Equal("gpt-test", lease.ResolvedModel);

        Assert.Equal(["alpha"], resolver.ResolvedProviders);

    }

    [Fact]
    public async Task Embedding_generator_factory_resolves_the_credential_through_the_shared_resolver()
    {

        ArcanumSettings settings = Settings();

        settings.Features.Embeddings = true;

        settings.Integrations.Embeddings = new EmbeddingIntegrationSettings
        {

            Provider = "alpha",

            Model = "embed-test",

        };

        RecordingResolver resolver = new("stored-embedding-secret");

        EmbeddingGeneratorFactory factory = new(
            new StubHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            resolver);

        using EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(
            CancellationToken.None);

        Assert.NotNull(lease.Generator);

        Assert.Equal(["alpha"], resolver.ResolvedProviders);

    }

    [Fact]
    public async Task Provider_health_probe_sends_the_stored_credential_as_a_bearer_token()
    {

        CapturingHandler handler = new();

        ProviderHealthProbe probe = new(
            new StubHttpClientFactory(handler),
            new RecordingResolver("stored-probe-secret"));

        bool healthy = await probe.ProbeAsync(
            Settings().Providers[0],
            CancellationToken.None);

        Assert.True(healthy);

        Assert.Equal("Bearer", handler.Authorization?.Scheme);

        Assert.Equal("stored-probe-secret", handler.Authorization?.Parameter);

    }

    [Fact]
    public async Task Provider_health_probe_omits_authorization_for_keyless_providers()
    {

        CapturingHandler handler = new();

        ProviderHealthProbe probe = new(
            new StubHttpClientFactory(handler),
            new RecordingResolver(null));

        _ = await probe.ProbeAsync(Settings().Providers[0], CancellationToken.None);

        Assert.Null(handler.Authorization);

    }

    [Fact]
    public async Task Health_report_counts_a_securely_stored_credential_without_disclosing_it()
    {

        const string Secret = "stored-health-secret-material";

        HealthComponentDto component = await ArcanumHealthChecker.BuildProvidersComponentAsync(
            Settings(),
            new AlwaysHealthyTracker(),
            new RecordingResolver(Secret),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, component.Status);

        Assert.Contains(
            "1/1 provider credentials",
            component.Detail,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("secure store", component.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(Secret, component.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Health_report_reports_no_credential_when_none_is_stored_or_referenced()
    {

        HealthComponentDto component = await ArcanumHealthChecker.BuildProvidersComponentAsync(
            Settings(),
            new AlwaysHealthyTracker(),
            new RecordingResolver(null),
            CancellationToken.None);

        Assert.Contains(
            "0/1 provider credentials",
            component.Detail,
            StringComparison.OrdinalIgnoreCase);

    }

    private static ArcanumSettings Settings() =>
        new()
        {

            DefaultModel = "gpt-test",

            Providers =
            [
                new ProviderSettings
                {

                    Name = "alpha",

                    Type = AiProviderKind.OpenAICompatible,

                    Endpoint = "https://example.test/v1",

                    CredentialEnvironmentVariable = CredentialVariable,

                    Models = ["gpt-test", "embed-test"],

                },
            ],

        };

    private sealed class RecordingResolver(string? apiKey) : IProviderApiKeyResolver
    {

        private readonly List<string> _resolved = [];

        public IReadOnlyList<string> ResolvedProviders => _resolved;

        public Task<string?> ResolveAsync(
            ProviderSettings provider,
            CancellationToken cancellationToken = default)
        {

            _resolved.Add(provider.Name);

            return Task.FromResult(apiKey);

        }

    }

    private sealed class AlwaysHealthyTracker : IProviderHealthTracker
    {

        public event Action<ProviderHealthStatus>? HealthChanged;

        public bool IsHealthy(string providerName) => true;

        public void MarkFailed(string providerName) =>
            HealthChanged?.Invoke(
                new ProviderHealthStatus(providerName, false, DateTimeOffset.UtcNow, 1));

        public void MarkHealthy(string providerName) =>
            HealthChanged?.Invoke(
                new ProviderHealthStatus(providerName, true, DateTimeOffset.UtcNow, 0));

        public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() => [];

    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);

    }

    private sealed class CapturingHandler : HttpMessageHandler
    {

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Authorization = request.Headers.Authorization;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        }

    }

}
