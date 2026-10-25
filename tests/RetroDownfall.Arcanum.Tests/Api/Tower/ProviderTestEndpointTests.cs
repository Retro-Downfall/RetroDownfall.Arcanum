using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// <c>POST /api/providers/test</c> — verifies the response-size cap (a hostile/misconfigured
/// "OpenAI-compatible" endpoint must not be buffered unbounded) and that outbound URL validation
/// failures return sanitized, actionable diagnostics rather than raw guard details.
/// </summary>
[Collection("ApiHost")]
public sealed class ProviderTestEndpointTests : IAsyncLifetime
{
    private const string UnresolvableHostName = "no-such-host.invalid";

    private readonly ArcanumWebApplicationFactory _factory = new();

    private readonly RecordingDnsResolver _dns = new();

    private readonly RecordingLogger _validationLogger = new();

    private HttpListener? _listener;

    private readonly TaskCompletionSource _responseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _releaseStalledResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _stalledResponseTask;

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

    public ProviderTestEndpointTests()
    {
        _factory.ServiceOverrides = services =>
        {
            services.AddSingleton<IDnsResolver>(_dns);

            services.AddSingleton<ILoggerFactory>(new ProbeLoggerFactory(_validationLogger));
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _tearingDown = true;

        _releaseStalledResponse.TrySetResult();

        _listener?.Stop();

        _listener?.Close();

        if (_stalledResponseTask is not null)
        {
            await _stalledResponseTask;
        }

        await _factory.DisposeAsync();
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
    public async Task Test_StalledWithinCapBody_UsesTheProbeDeadlineAfterHeaders()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string baseUrl = StartStalledListener(declaredLength: 64);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using CancellationTokenSource cleanup = new();

        Task<HttpResponseMessage> probe = client.PostAsync(
            "/api/providers/test",
            new StringContent(
                JsonSerializer.Serialize(new ProviderTestRequest(baseUrl, null, AiProviderKind.OpenAICompatible), ArcanumJsonContext.Default.ProviderTestRequest),
                Encoding.UTF8,
                "application/json"),
            cleanup.Token);

        try
        {
            await _responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using HttpResponseMessage response = await probe.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ArcanumJsonContext.Default.ApiResponseProviderTestResult);

            Assert.NotNull(body);
            Assert.False(body.Data!.IsReachable);
            Assert.Equal("The provider probe timed out.", body.Data.Error);
        }
        finally
        {
            await cleanup.CancelAsync();

            try
            {
                using HttpResponseMessage ignored = await probe;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Test_CallerCancellationDuringBodyRead_PropagatesInsteadOfReportingTimeout()
    {
        string baseUrl = StartStalledListener(declaredLength: 64);

        using CancellationTokenSource cancellation = new();

        MethodInfo method = typeof(ProviderTestEndpoints).GetMethod("ProbeProviderAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        Task<ProviderTestResult> probe = (Task<ProviderTestResult>)method.Invoke(
            null,
            [new ProviderTestRequest(baseUrl, null, AiProviderKind.OpenAICompatible), _dns, NullLogger.Instance, cancellation.Token])!;

        await _responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
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
    public async Task Test_UnresolvableUrl_ReturnsSanitizedDnsRemedy()
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

        // A fixed reason and remedy must not echo the raw guard detail or resolved address.
        Assert.Contains("DnsFailed", body.Error?.Message, StringComparison.Ordinal);

        Assert.Contains("hostname", body.Error?.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(UnresolvableHostName, body.Error?.Message ?? string.Empty, StringComparison.Ordinal);

        // The 400 must come from the guard's own policy decision, not from whatever the runner's
        // resolver happens to answer. A network that wildcards NXDOMAIN to a parking or search IP
        // resolves the name, the guard passes, and the endpoint issues a real outbound request from
        // the CI machine.
        Assert.Contains(UnresolvableHostName, _dns.Queries);
    }

    [Theory]
    [InlineData("<html>not a provider</html>")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{}]}")]
    public async Task Test_MalformedModelList_IsUnreachableWithSanitizedRemedy(string payload)
    {
        string endpoint = StartListener(payload);

        RecordingLogger logger = new();

        ProviderTestResult result = await ProbeAsync(endpoint, logger);

        Assert.False(result.IsReachable);

        Assert.Contains("NotAModelList", result.Error);

        Assert.Contains("/v1", result.Error);

        AssertWarning(logger, "NotAModelList");
    }

    [Fact]
    public async Task Test_ValidEmptyModelList_RemainsReachable()
    {
        ProviderTestResult result = await ProbeAsync(StartListener("{\"data\":[]}"), new RecordingLogger());

        Assert.True(result.IsReachable);

        Assert.Empty(result.ModelsFound);

        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Test_AuthenticationRefusal_ExplainsApiKeyWithoutLoggingSecrets(int status)
    {
        string endpoint = StartListener("secret provider body", status);

        RecordingLogger logger = new();

        ProviderTestResult result = await ProbeAsync(endpoint + "?token=query-secret", logger);

        Assert.False(result.IsReachable);

        Assert.Contains("Unauthorized", result.Error);

        Assert.Contains("apiKey", result.Error);

        AssertWarning(logger, "Unauthorized");

        Assert.Contains("127.0.0.1", logger.Entries.Single().Message);

        Assert.DoesNotContain("query-secret", logger.Entries.Single().Message);

        Assert.DoesNotContain("secret provider body", logger.Entries.Single().Message);
    }

    [Fact]
    public async Task Test_StoppedProvider_ExplainsConnectionFailureWithoutLoggingException()
    {
        // A just-stopped loopback listener reproduces a stopped local provider.
        int port = GetFreeTcpPort();

        RecordingLogger logger = new();

        ProviderTestResult result = await ProbeAsync($"http://userinfo-secret:password-secret@provider.test:{port}/v1?token=query-secret", logger);

        Assert.False(result.IsReachable);

        Assert.Contains("ConnectionRefused", result.Error);

        Assert.Contains("running", result.Error);

        AssertWarning(logger, "ConnectionRefused");

        Assert.DoesNotContain("query-secret", logger.Entries.Single().Message);

        Assert.DoesNotContain("userinfo-secret", logger.Entries.Single().Message);

        Assert.DoesNotContain("password-secret", logger.Entries.Single().Message);

        Assert.DoesNotContain("127.0.0.1", logger.Entries.Single().Message);
    }

    [Theory]
    [InlineData("http://no-such-host.invalid/v1", "DnsFailed")]
    [InlineData("http://blocked.test/v1", "Blocked")]
    public async Task Test_ConnectTimeDnsAndPolicyFailures_RemainDistinct(string endpoint, string reason)
    {
        RecordingLogger logger = new();

        ProviderTestResult result = await ProbeAsync(endpoint, logger);

        Assert.False(result.IsReachable);

        Assert.Contains(reason, result.Error);

        AssertWarning(logger, reason);

        Assert.DoesNotContain("169.254.169.254", result.Error);

        Assert.DoesNotContain("169.254.169.254", logger.Entries.Single().Message);

        Assert.Single(_dns.Queries);
    }

    [Fact]
    public async Task Test_UntrustedProviderCertificate_ExplainsTlsWithoutLoggingCertificateOrException()
    {
        using RSA key = RSA.Create(2048);

        CertificateRequest certificateRequest = new("CN=private-certificate-secret", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));

        using TcpListener server = new(IPAddress.Loopback, 0);

        server.Start();

        int port = ((IPEndPoint)server.LocalEndpoint).Port;

        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));

        Task handshake = Task.Run(async () =>
        {
            using TcpClient connection = await server.AcceptTcpClientAsync(cleanup.Token);

            await using SslStream tls = new(connection.GetStream());

            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cleanup.Token);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // The production client refuses this self-signed certificate.
            }
        });

        RecordingLogger logger = new();

        try
        {
            ProviderTestResult result = await ProbeAsync($"https://provider.test:{port}/v1", logger);

            Assert.False(result.IsReachable);

            Assert.Contains("Tls", result.Error);

            Assert.Contains("certificate", result.Error);

            AssertWarning(logger, "Tls");

            Assert.DoesNotContain("private-certificate-secret", logger.Entries.Single().Message);
        }
        finally
        {
            await cleanup.CancelAsync();

            try
            {
                await handshake;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [SkippableTheory]
    [InlineData("localhost:11434/v1", "Validation.InvalidUrl", "InvalidUrl", "http")]
    [InlineData("https://no-such-host.invalid/v1", "Provider.DnsFailed", "DnsFailed", "hostname")]
    [InlineData("http://169.254.169.254/v1", "Security.BlockedOutboundUrl", "Blocked", "permitted")]
    public async Task Test_UrlFailures_AreDistinctAndSanitized(string endpoint, string code, string reason, string remedy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/providers/test", new StringContent(
            JsonSerializer.Serialize(new ProviderTestRequest(endpoint, "api-secret", AiProviderKind.OpenAICompatible), ArcanumJsonContext.Default.ProviderTestRequest), Encoding.UTF8, "application/json"));

        ApiResponse<ProviderTestResult>? body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseProviderTestResult);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(code, body!.Error?.Code);

        Assert.Contains(reason, body.Error?.Message);

        Assert.Contains(remedy, body.Error?.Message);

        Assert.DoesNotContain(endpoint, body.Error?.Message ?? string.Empty);

        Assert.True(_dns.Queries.Count <= 1);

        AssertWarning(_validationLogger, reason);
    }

    private async Task<ProviderTestResult> ProbeAsync(string endpoint, RecordingLogger logger)
    {
        MethodInfo method = typeof(ProviderTestEndpoints).GetMethod("ProbeProviderAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        return await (Task<ProviderTestResult>)method.Invoke(null,
            [new ProviderTestRequest(endpoint, "api-secret", AiProviderKind.OpenAICompatible), _dns, logger, CancellationToken.None])!;
    }

    private static void AssertWarning(RecordingLogger logger, string reason)
    {
        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);

        Assert.Contains(reason, entry.Message);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("api-secret", entry.Message);
    }

    private sealed class ProbeLoggerFactory(RecordingLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => categoryName == "RetroDownfall.Arcanum.Api.Tower.ProviderTest" ? logger : NullLogger.Instance;

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
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

        _stalledResponseTask = Task.Run(async () =>
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

                _responseStarted.TrySetResult();

                // Keep the body incomplete until test teardown; endpoint cancellation must end its read.
                await _releaseStalledResponse.Task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // Expected: the endpoint aborts the read as soon as the declared length passes the cap.
            }
        });

        return $"http://127.0.0.1:{port}/v1";
    }

    /// <summary>Starts a loopback HTTP listener that returns <paramref name="responseBody"/> for a single request, and returns its base URL.</summary>
    private string StartListener(string responseBody, int statusCode = 200)
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

                ctx.Response.StatusCode = statusCode;

                ctx.Response.ContentType = "application/json";

                ctx.Response.ContentLength64 = bytes.Length;

                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);

                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
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
            new(StringComparer.OrdinalIgnoreCase) { ["127.0.0.1"] = [IPAddress.Loopback], ["provider.test"] = [IPAddress.Loopback], ["blocked.test"] = [IPAddress.Parse("169.254.169.254")] };

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
