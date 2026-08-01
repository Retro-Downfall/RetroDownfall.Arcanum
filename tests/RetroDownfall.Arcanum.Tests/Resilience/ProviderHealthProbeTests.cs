using System.Net;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Resilience;

/// <summary>
/// <see cref="ProviderHealthProbe"/> — verifies key-required OpenAI-compatible providers are probed
/// with an <c>Authorization</c> header (previously omitted, causing 401/403 probes to wrongly mark a
/// healthy, key-required provider as down).
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ProviderHealthProbeTests : IDisposable
{
    private const string CredentialVariable = "ARCANUM_TEST_HEALTH_PROVIDER_KEY";
    private readonly string? _originalCredential;

    public ProviderHealthProbeTests()
    {
        _originalCredential =
            System.Environment.GetEnvironmentVariable(CredentialVariable);
        System.Environment.SetEnvironmentVariable(CredentialVariable, null);
    }

    [Fact]
    public async Task ProbeAsync_AttachesEnvironmentApiKey_AsBearerAuthorizationHeader()
    {
        System.Environment.SetEnvironmentVariable(CredentialVariable, "plain-test-key");

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "keyed",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            CredentialEnvironmentVariable = CredentialVariable,
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
            CredentialEnvironmentVariable = CredentialVariable,
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Null(request.Headers.Authorization);

    }

    [Fact]
    public async Task ProbeAsync_KeyRequiredProviderWithoutHeader_WouldHaveReportedUnhealthy()
    {
        System.Environment.SetEnvironmentVariable(CredentialVariable, "secret-key");

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
            CredentialEnvironmentVariable = CredentialVariable,
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

    }

    [Fact]
    public async Task ProbeAsync_EmptyEndpoint_ReturnsFalseWithoutHttpCall()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {
            Name = "incomplete",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "   ",
            Models = ["m"],
        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.False(healthy);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task ProbeAsync_DoesNotBufferSuccessfulResponseBody()
    {

        StreamContent content = new(new ThrowOnReadStream());

        RecordingHttpHandler handler = new(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = content,

            }));

        ProviderHealthProbe probe = CreateProbe(handler);

        ProviderSettings provider = new()
        {

            Name = "streaming-health",

            Type = AiProviderKind.OpenAICompatible,

            Endpoint = "https://example.test/v1",

        };

        bool healthy = await probe.ProbeAsync(provider, CancellationToken.None);

        Assert.True(healthy);

    }

    private static ProviderHealthProbe CreateProbe(HttpMessageHandler handler)
    {
        return new ProviderHealthProbe(
            new FakeHttpClientFactory(handler));

    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            _originalCredential);
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

    private sealed class ThrowOnReadStream : Stream
    {

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {

            get => 0;

            set => throw new NotSupportedException();

        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Health probes must not read response bodies.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new IOException("Health probes must not read response bodies."));

        public override void Flush()
        {

        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    }

}
