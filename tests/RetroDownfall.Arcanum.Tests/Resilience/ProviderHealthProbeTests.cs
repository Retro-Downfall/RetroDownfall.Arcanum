using System.Net;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Resilience;

/// <summary>
/// <see cref="ProviderHealthProbe"/> — verifies key-required OpenAI-compatible providers are probed
/// with an <c>Authorization</c> header (previously omitted, causing 401/403 probes to wrongly mark a
/// healthy, key-required provider as down).
/// </summary>
public sealed class ProviderHealthProbeTests
{

    [Fact]
    public async Task ProbeAsync_AttachesResolvedApiKey_AsBearerAuthorizationHeader()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "keyed",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            ApiKey = "plain-test-key",
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.NotNull(request.Headers.Authorization);

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

        Assert.Equal("plain-test-key", request.Headers.Authorization!.Parameter);

    }

    [Fact]
    public async Task ProbeAsync_NoApiKeyConfigured_SendsNoAuthorizationHeader()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "keyless",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            ApiKey = null,
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Null(request.Headers.Authorization);

    }

    [Fact]
    public async Task ProbeAsync_KeyRequiredProviderWithoutHeader_WouldHaveReportedUnhealthy()
    {

        // Regression guard for the original bug: an endpoint that 401s absent a Bearer header, but
        // 200s with the correct one, must be reported healthy once the header is attached.
        RecordingHttpHandler handler = new(request =>
            Task.FromResult(request.Headers.Authorization is { Scheme: "Bearer", Parameter: "secret-key" }
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "keyed",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            ApiKey = "secret-key",
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

    }

    [Fact]
    public async Task ProbeAsync_LlamaCppServerWithNoModelsOrModelMap_ReturnsUnhealthy()
    {

        // ConfigurationValidator rejects this shape at startup, but hot-reloaded settings are not
        // re-validated, so a LlamaCppServer provider with nothing to ever serve can still reach the
        // probe at runtime — it must be reported unhealthy, not silently healthy.
        RecordingHttpHandler handler = new(_ => throw new InvalidOperationException("HTTP must not be used for LlamaCppServer probes."));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "misconfigured-llama",
            Type = AiProviderKind.LlamaCppServer,
            Endpoint = "http://127.0.0.1:8080",
            Models = [],
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.False(healthy);

        Assert.Empty(handler.Requests);

    }

    private static ProviderHealthProbe CreateProbe(HttpMessageHandler handler)
    {

        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new ProviderHealthProbe(
            new FakeHttpClientFactory(handler),
            new NoopLlamaServerManager(),
            secretProtector,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return responder(request);

        }

    }

    private sealed class NoopLlamaServerManager : ILlamaServerManager
    {

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string modelKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by OpenAI-compatible provider probes.");

        public Task<IDisposable> AcquireSlotAsync(string modelKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by OpenAI-compatible provider probes.");

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => true;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

}
