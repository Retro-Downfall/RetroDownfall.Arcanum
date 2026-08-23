using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// <c>POST /api/providers/test</c> — verifies the response-size cap (a hostile/misconfigured
/// "OpenAI-compatible" endpoint must not be buffered unbounded) and that outbound URL validation
/// failures return a generic client-facing message rather than the raw guard detail.
/// </summary>
[Collection("ApiHost")]
public sealed class ProviderTestEndpointTests : IAsyncLifetime
{

    private const string UnresolvableHostName = "no-such-host.invalid";

    private readonly ArcanumWebApplicationFactory _factory;

    private readonly RecordingDnsResolver _dns = new();

    private HttpListener? _listener;

    private IDnsResolver? _originalResolver;

    private volatile bool _tearingDown;

    private Exception? _acceptFailure;

    /// <summary>
    /// Detail appended to the failure message of every assertion that depends on the fake provider
    /// having served the request. Without it a listener that never accepted anything surfaced only as
    /// "unreachable" or "no size-cap message", with nothing pointing at the listener.
    /// </summary>
    private string ServingDetail =>
        _acceptFailure is null
            ? string.Empty
            : $" The fake provider listener never accepted the request: {_acceptFailure}";

    public ProviderTestEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    /// <summary>
    /// Installs the hermetic resolver for the duration of one test. <c>OutboundUrlGuard.DnsResolver</c>
    /// is a process-global seam, and this class cannot join the <c>OutboundUrlGuardDns</c> collection
    /// because it needs <c>ApiHost</c> for its factory fixture — but both collections declare
    /// <c>DisableParallelization</c>, so xUnit never runs another class while this one holds the swap.
    /// </summary>
    public Task InitializeAsync()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        OutboundUrlGuard.DnsResolver = _dns;

        return Task.CompletedTask;

    }

    public Task DisposeAsync()
    {

        if (_originalResolver is not null)
        {

            OutboundUrlGuard.DnsResolver = _originalResolver;

        }

        _tearingDown = true;

        _listener?.Stop();

        _listener?.Close();

        return Task.CompletedTask;

    }

    [SkippableFact]
    public async Task Test_OversizedResponse_IsRejectedWithoutBufferingWholeBody()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // One byte over the endpoint's 4 MiB cap, wrapped so a full (unbounded) read would still
        // parse as valid OpenAI /models JSON — proving the rejection is size-based, not a parse error.
        string oversizedPayload = $$"""{"data":[{"id":"{{new string('x', 4 * 1024 * 1024 + 1)}}"}]}""";

        string baseUrl = StartListener(oversizedPayload);

        HttpClient client = _factory.CreateAuthenticatedClient();

        ProviderTestRequest request = new(baseUrl, null, AiProviderKind.OpenAICompatible);

        HttpResponseMessage response = await client.PostAsync(
            "/api/providers/test",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ProviderTestRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseProviderTestResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.False(body.Data!.IsReachable, $"Expected the size cap to reject the probe.{ServingDetail}");

        Assert.True(
            body.Data.Error?.Contains("exceeded the maximum allowed size", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected a size-cap rejection, got: {body.Data.Error}.{ServingDetail}");

    }

    /// <summary>
    /// The cap can only protect what it is consulted about. <c>GetAsync</c>'s default
    /// <c>ResponseContentRead</c> buffers the whole body — pre-sized to the declared Content-Length —
    /// before the handler ever reaches <c>TryReadCappedStringAsync</c>, so a provider that announces a
    /// gigantic body forces that allocation and then the probe merely reports it. Reading headers first
    /// is what makes the announced length refusable before a byte of it is held.
    /// </summary>
    [SkippableFact]
    public async Task Test_HugeDeclaredContentLength_IsRefusedFromHeadersWithoutAwaitingTheBody()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string baseUrl = StartStalledListener(declaredLength: 512L * 1024 * 1024);

        HttpClient client = _factory.CreateAuthenticatedClient();

        ProviderTestRequest request = new(baseUrl, null, AiProviderKind.OpenAICompatible);

        HttpResponseMessage response = await client.PostAsync(
            "/api/providers/test",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ProviderTestRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseProviderTestResult);

        Assert.NotNull(body);

        Assert.False(body!.Data!.IsReachable, $"Expected the size cap to reject the probe.{ServingDetail}");

        Assert.True(
            body.Data.Error?.Contains("exceeded the maximum allowed size", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected the declared length to be refused from the headers, got: {body.Data.Error}.{ServingDetail}");

    }

    [SkippableFact]
    public async Task Test_WithinCapResponse_IsReachableAndParsesModels()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string baseUrl = StartListener("""{"data":[{"id":"gpt-test"}]}""");

        HttpClient client = _factory.CreateAuthenticatedClient();

        ProviderTestRequest request = new(baseUrl, null, AiProviderKind.OpenAICompatible);

        HttpResponseMessage response = await client.PostAsync(
            "/api/providers/test",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ProviderTestRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseProviderTestResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.True(body.Data!.IsReachable, $"Expected the fake provider to be reachable.{ServingDetail}");

        Assert.True(
            body.Data.ModelsFound.Contains("gpt-test", StringComparer.Ordinal),
            $"Expected the parsed models to contain gpt-test, got: [{string.Join(", ", body.Data.ModelsFound)}].{ServingDetail}");

    }

    [SkippableFact]
    public async Task Test_BlockedUrl_ReturnsGenericMessage_NotRawGuardDetail()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string unresolvableHost = $"https://{UnresolvableHostName}/v1";

        ProviderTestRequest request = new(unresolvableHost, null, AiProviderKind.OpenAICompatible);

        HttpResponseMessage response = await client.PostAsync(
            "/api/providers/test",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ProviderTestRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseProviderTestResult);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        // The generic client-facing message must not echo the raw guard detail (which can carry
        // resolved hosts/IPs); the detailed reason is logged server-side only.
        Assert.Contains("failed validation", body.Error?.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(UnresolvableHostName, body.Error?.Message ?? string.Empty, StringComparison.Ordinal);

        // The 400 must come from the guard's own policy decision, not from whatever the runner's
        // resolver happens to answer. A network that wildcards NXDOMAIN to a parking or search IP
        // resolves the name, the guard passes, and the endpoint issues a real outbound request from
        // the CI machine.
        Assert.Contains(UnresolvableHostName, _dns.Queries);

    }

    /// <summary>
    /// Deterministic form of the ephemeral-port TOCTOU in <see cref="GetFreeTcpPort"/>: the probe binds
    /// port 0, reads the port the kernel assigned and releases it again, so between that release and
    /// <see cref="HttpListener.Start"/> any process on the machine can take it. Binding must retry on a
    /// fresh port instead of failing the test with an address-already-in-use error that has nothing to
    /// do with the response-size cap under test.
    /// </summary>
    [Fact]
    public void BindLoopbackListener_retries_when_the_probed_port_was_already_taken()
    {

        TcpListener squatter = new(IPAddress.Loopback, 0);

        squatter.Start();

        int takenPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        HttpListener? listener = null;

        try
        {

            int freePort = GetFreeTcpPort();

            Queue<int> ports = new([takenPort, takenPort, freePort]);

            int bound;

            (listener, bound) = BindLoopbackListener(ports.Dequeue, attempts: 3);

            Assert.Equal(freePort, bound);

            Assert.True(listener.IsListening);

        }
        finally
        {

            listener?.Close();

            squatter.Stop();

        }

    }

    /// <summary>
    /// Binds a loopback <see cref="HttpListener"/> to a port taken from <paramref name="portSource"/>,
    /// retrying on a fresh port when the probed one was claimed in the meantime, and returns the
    /// listening instance together with the port it actually bound. A failed <c>Start</c> disposes the
    /// listener, so each attempt needs its own.
    /// </summary>
    private static (HttpListener Listener, int Port) BindLoopbackListener(Func<int> portSource, int attempts)
    {

        for (int attempt = 1; ; attempt++)
        {

            int port = portSource();

            HttpListener listener = new();

            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {

                listener.Start();

                return (listener, port);

            }
            catch (HttpListenerException) when (attempt < attempts)
            {

                // Something claimed the probed port between the probe's release and this bind.

                listener.Close();

            }

        }

    }

    /// <summary>
    /// Starts a loopback listener that announces <paramref name="declaredLength"/> bytes and then sends
    /// almost none of them. The declared length alone is enough for the cap to refuse the probe, so a
    /// reader that consults the headers answers immediately; one that buffers the body first waits for
    /// bytes that never arrive.
    /// </summary>
    private string StartStalledListener(long declaredLength)
    {

        (HttpListener listener, int port) = BindLoopbackListener(GetFreeTcpPort, attempts: 5);

        _listener = listener;

        _ = Task.Run(async () =>
        {

            HttpListenerContext ctx;

            try
            {

                ctx = await listener.GetContextAsync().ConfigureAwait(false);

            }
            catch (Exception ex)
            {

                if (!_tearingDown)
                {

                    _acceptFailure = ex;

                }

                return;

            }

            try
            {

                ctx.Response.StatusCode = 200;

                ctx.Response.ContentType = "application/json";

                ctx.Response.ContentLength64 = declaredLength;

                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{")).ConfigureAwait(false);

                await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);

                // Never completes the announced body; the probe's own 5 s timeout is the only exit for
                // a client that insists on reading it all.
                await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            }
            catch (Exception)
            {

                // Expected: the endpoint aborts the read as soon as the declared length passes the cap.

            }

        });

        return $"http://127.0.0.1:{port}/v1";

    }

    /// <summary>Starts a loopback HTTP listener that returns <paramref name="responseBody"/> for a single request, and returns its base URL.</summary>
    private string StartListener(string responseBody)
    {

        (HttpListener listener, int port) = BindLoopbackListener(GetFreeTcpPort, attempts: 5);

        _listener = listener;

        _ = Task.Run(async () =>
        {

            HttpListenerContext ctx;

            try
            {

                ctx = await listener.GetContextAsync().ConfigureAwait(false);

            }
            catch (Exception ex)
            {

                // Failing to accept at all means the endpoint talked to nobody, which every assertion
                // below would otherwise report as a plain "unreachable". Teardown stops the listener
                // deliberately, so only a failure before then is diagnostic.
                if (!_tearingDown)
                {

                    _acceptFailure = ex;

                }

                return;

            }

            try
            {

                byte[] bytes = Encoding.UTF8.GetBytes(responseBody);

                ctx.Response.StatusCode = 200;

                ctx.Response.ContentType = "application/json";

                ctx.Response.ContentLength64 = bytes.Length;

                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);

                ctx.Response.OutputStream.Close();

            }
            catch (Exception)
            {

                // The client aborts the read once the response passes the cap, so a write failure
                // after the request arrived is the expected path of the oversized-response test.

            }

        });

        return $"http://127.0.0.1:{port}/v1";

    }

    /// <summary>
    /// Records every hostname it is asked to resolve and answers only from its own map, so a lookup this
    /// class did not register fails with <see cref="SocketError.HostNotFound"/> exactly as an NXDOMAIN
    /// would.
    /// </summary>
    private sealed class RecordingDnsResolver : IDnsResolver
    {

        private readonly Dictionary<string, IPAddress[]> _map =
            new(StringComparer.OrdinalIgnoreCase) { ["127.0.0.1"] = [IPAddress.Loopback] };

        public List<string> Queries { get; } = new();

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {

            lock (Queries)
            {

                Queries.Add(host);

            }

            if (_map.TryGetValue(host, out IPAddress[]? addresses))
            {

                return Task.FromResult(addresses);

            }

            throw new SocketException((int)SocketError.HostNotFound);

        }

    }

    private static int GetFreeTcpPort()
    {

        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;

    }

}
