using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

/// <summary>
/// <c>POST /api/providers/test</c> — verifies the response-size cap (a hostile/misconfigured
/// "OpenAI-compatible" endpoint must not be buffered unbounded) and that outbound URL validation
/// failures return a generic client-facing message rather than the raw guard detail.
/// </summary>
[Collection("ApiHost")]
public sealed class ProviderTestEndpointTests : IAsyncLifetime
{

    private readonly ArcanumWebApplicationFactory _factory;

    private HttpListener? _listener;

    public ProviderTestEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {

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

        Assert.False(body.Data!.IsReachable);

        Assert.Contains("exceeded the maximum allowed size", body.Data.Error, StringComparison.OrdinalIgnoreCase);

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

        Assert.True(body.Data!.IsReachable);

        Assert.Contains("gpt-test", body.Data.ModelsFound);

    }

    [SkippableFact]
    public async Task Test_BlockedUrl_ReturnsGenericMessage_NotRawGuardDetail()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string unresolvableHost = "https://this-host-does-not-resolve.invalid.arcanum-test/v1";

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

        Assert.DoesNotContain("this-host-does-not-resolve", body.Error?.Message ?? string.Empty, StringComparison.Ordinal);

    }

    /// <summary>Starts a loopback HTTP listener that returns <paramref name="responseBody"/> for a single request, and returns its base URL.</summary>
    private string StartListener(string responseBody)
    {

        int port = GetFreeTcpPort();

        _listener = new HttpListener();

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        _listener.Start();

        _ = Task.Run(async () =>
        {

            try
            {

                HttpListenerContext ctx = await _listener.GetContextAsync().ConfigureAwait(false);

                byte[] bytes = Encoding.UTF8.GetBytes(responseBody);

                ctx.Response.StatusCode = 200;

                ctx.Response.ContentType = "application/json";

                ctx.Response.ContentLength64 = bytes.Length;

                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);

                ctx.Response.OutputStream.Close();

            }
            catch (Exception)
            {

                // Listener stopped/disposed during teardown — nothing to serve.

            }

        });

        return $"http://127.0.0.1:{port}/v1";

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
