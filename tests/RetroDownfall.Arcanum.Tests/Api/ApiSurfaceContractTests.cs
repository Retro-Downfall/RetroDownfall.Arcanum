using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Composition-level HTTP surface contracts that need no Grimoire: the CORS policy browser clients
/// negotiate against, the pinned provider egress handler, the bounded metric label for unmatched routes,
/// the multipart ceiling on <c>POST /v1/files</c>, and idempotency header/fingerprint identity.
/// </summary>
public sealed class ApiSurfaceContractTests : IDisposable
{

    private readonly string _tempHome;

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public ApiSurfaceContractTests()
    {

        _tempHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"api-surface-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempHome);

        Capture("ASPNETCORE_ENVIRONMENT");
        Capture("DOTNET_ENVIRONMENT");
        Capture("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _tempHome);

    }

    public void Dispose()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        try
        {

            Directory.Delete(_tempHome, recursive: true);

        }
        catch (IOException)
        {

            // A leftover temp directory is not worth failing a test over.

        }

    }

    private void Capture(string name) =>
        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

    [Fact]
    public void CorsPolicy_allows_the_documented_request_headers_and_exposes_the_documented_response_headers()
    {

        CorsPolicy policy = ResolveCorsPolicy();

        // DESIGN §11.4: any header / any method. A header allowlist that omitted Idempotency-Key made the
        // documented replay-protection contract unreachable from every browser client.
        Assert.True(policy.AllowAnyHeader);

        Assert.True(policy.AllowAnyMethod);

        // Custom response headers are unreadable from browser JavaScript unless explicitly exposed, which
        // made the documented X-Arcanum-Next-Cursor pagination contract impossible to follow.
        Assert.Contains(ArcanumApiHeaders.AuditNextCursor, policy.ExposedHeaders);

        Assert.Contains(ArcanumApiHeaders.StructuredOutputWarning, policy.ExposedHeaders);

    }

    [Fact]
    public void ProviderInferenceHttpClient_uses_the_pinned_provider_egress_handler()
    {

        using ServiceProvider provider = BuildApiServices();

        IHttpMessageHandlerFactory handlerFactory =
            provider.GetRequiredService<IHttpMessageHandlerFactory>();

        HttpMessageHandler pipeline = handlerFactory.CreateHandler("OpenAiCompatibleProvider");

        HttpMessageHandler current = pipeline;

        while (current is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {

            current = delegating.InnerHandler;

        }

        SocketsHttpHandler primary = Assert.IsType<SocketsHttpHandler>(current);

        // Without these, a validated provider endpoint can 3xx-redirect or DNS-rebind to a link-local
        // metadata address after the one-time PUT /api/config validation and be dialed on every turn.
        Assert.False(primary.AllowAutoRedirect);

        Assert.False(primary.UseProxy);

        Assert.NotNull(primary.ConnectCallback);

    }

    [Fact]
    public async Task UnmatchedRoutes_record_a_fixed_endpoint_label_not_the_raw_request_path()
    {

        string scannedPath = $"/api/scanner-probe-{Guid.NewGuid():N}";

        List<string> endpointLabels = [];

        using MeterListener listener = new();

        listener.InstrumentPublished = static (instrument, activeListener) =>
        {

            if (string.Equals(instrument.Name, "arcanum_http_requests_total", StringComparison.Ordinal))
            {

                activeListener.EnableMeasurementEvents(instrument);

            }

        };

        listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) =>
            {

                foreach (KeyValuePair<string, object?> tag in tags)
                {

                    if (string.Equals(tag.Key, "endpoint", StringComparison.Ordinal))
                    {

                        lock (endpointLabels)
                        {

                            endpointLabels.Add(tag.Value?.ToString() ?? string.Empty);

                        }

                    }

                }

            });

        listener.Start();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        await using WebApplication app = builder.Build();

        app.UseArcanumMetrics();

        await app.StartAsync();

        using HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(scannedPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await app.StopAsync();

        string[] observed;

        lock (endpointLabels)
        {

            observed = [.. endpointLabels];

        }

        // Every distinct raw path would otherwise become a permanent series in the Prometheus exporter.
        Assert.DoesNotContain(scannedPath, observed);

        Assert.Contains(ApiBootstrapper.UnmatchedRouteMetricLabel, observed);

    }

    [Fact]
    public async Task FileUploadEndpoint_raises_the_multipart_ceiling_to_the_code_owned_envelope()
    {

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        await using WebApplication app = builder.Build();

        app.MapGroup("/v1").MapOpenAiV1Files();

        await app.StartAsync();

        Endpoint uploadEndpoint = Assert.Single(
            app.Services.GetRequiredService<EndpointDataSource>().Endpoints,
            static e => string.Equals(
                e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                "PostOpenAiFiles",
                StringComparison.Ordinal));

        await app.StopAsync();

        IFormOptionsMetadata? formOptions = uploadEndpoint.Metadata.GetMetadata<IFormOptionsMetadata>();

        Assert.NotNull(formOptions);

        // The framework default is 128 MiB, which made every upload between 128 MiB and the documented
        // 512 MiB envelope fail during binding with an empty 400 and left the handler's 413 unreachable.
        Assert.NotNull(formOptions.MultipartBodyLengthLimit);

        Assert.True(formOptions.MultipartBodyLengthLimit >= OpenAiV1Endpoints.ResolveMaxUploadBytes());

    }

    [Fact]
    public async Task DuplicateIdempotencyKeyHeaders_are_rejected_instead_of_silently_ignored()
    {

        (WebApplication app, ExecutionProbe probe) = await CreateIdempotencyProbeHostAsync();

        await using (app)
        {

            using HttpClient client = app.GetTestClient();

            HttpRequestMessage request = BuildProbeRequest();

            request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, "duplicate");

            request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, "duplicate");

            HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            Assert.Contains(
                ErrorCodes.Security.IdempotencyKeyAmbiguous,
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            // The side-effecting handler must not have run: the caller believes the header protects it.
            Assert.Equal(0, probe.Executions);

            await app.StopAsync();

        }

    }

    [Fact]
    public async Task NoIdempotencyKeyHeader_leaves_the_handler_reachable()
    {

        (WebApplication app, ExecutionProbe probe) = await CreateIdempotencyProbeHostAsync();

        await using (app)
        {

            using HttpClient client = app.GetTestClient();

            HttpResponseMessage response = await client.SendAsync(BuildProbeRequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Pins the contrast the duplicate-header case relies on: without a key the filter is a pass
            // through, so a duplicate key silently behaving the same way was indistinguishable to a caller.
            Assert.Equal(1, probe.Executions);

            await app.StopAsync();

        }

    }

    [Fact]
    public void Fingerprint_covers_the_query_string()
    {

        byte[] body = Encoding.UTF8.GetBytes("""{"prompt":"identical"}""");

        string workspaceA = IdempotencyIdentity.ComputeFingerprintHash(
            body,
            "/api/spells/build/execute",
            "workspace=/repo/A",
            "application/json");

        string workspaceB = IdempotencyIdentity.ComputeFingerprintHash(
            body,
            "/api/spells/build/execute",
            "workspace=/repo/B",
            "application/json");

        // Equal fingerprints replay the first workspace's cached answer for the second request.
        Assert.NotEqual(workspaceA, workspaceB);

    }

    [Fact]
    public void NormalizeQuery_is_order_independent_but_value_sensitive()
    {

        Assert.Equal(
            IdempotencyIdentity.NormalizeQuery(QueryContext("?workspace=/repo/A&version=2")),
            IdempotencyIdentity.NormalizeQuery(QueryContext("?version=2&workspace=/repo/A")));

        Assert.NotEqual(
            IdempotencyIdentity.NormalizeQuery(QueryContext("?workspace=/repo/A")),
            IdempotencyIdentity.NormalizeQuery(QueryContext("?workspace=/repo/B")));

        Assert.Equal(string.Empty, IdempotencyIdentity.NormalizeQuery(QueryContext(string.Empty)));

    }

    [Fact]
    public void ResolvePrincipal_ignores_the_client_supplied_host_header()
    {

        DefaultHttpContext localhost = new();

        localhost.Request.Host = new HostString("localhost", 5001);

        DefaultHttpContext loopback = new();

        loopback.Request.Host = new HostString("127.0.0.1", 5001);

        // A host-derived principal lets a caller partition its own claims and defeat replay protection.
        Assert.Equal(
            IdempotencyIdentity.ResolvePrincipal(localhost),
            IdempotencyIdentity.ResolvePrincipal(loopback));

    }

    private static DefaultHttpContext QueryContext(string queryString)
    {

        DefaultHttpContext context = new();

        context.Request.QueryString = new QueryString(queryString);

        return context;

    }

    private static HttpRequestMessage BuildProbeRequest()
    {

        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "probe"),
            ArcanumJsonContext.Default.PingRequest);

        return new HttpRequestMessage(HttpMethod.Post, "/api/probe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    }

    private static async Task<(WebApplication App, ExecutionProbe Probe)> CreateIdempotencyProbeHostAsync()
    {

        ExecutionProbe probe = new();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(probe);

        builder.Services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        WebApplication app = builder.Build();

        app.MapPost(
                "/api/probe",
                (PingRequest body, ExecutionProbe executionProbe) =>
                {

                    _ = body;

                    executionProbe.Record();

                    return Results.Ok();

                })
            .AddEndpointFilter(
                IdempotencyEndpointFilters.ForBoundArgument(0, ArcanumJsonContext.Default.PingRequest));

        await app.StartAsync();

        return (app, probe);

    }

    private CorsPolicy ResolveCorsPolicy()
    {

        using ServiceProvider provider = BuildApiServices();

        CorsOptions options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<CorsOptions>>()
            .Value;

        CorsPolicy? policy = options.GetPolicy("ArcanumCors");

        Assert.NotNull(policy);

        return policy;

    }

    private ServiceProvider BuildApiServices()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Port"] = "5001",
            })
            .Build();

        ServiceCollection services = [];

        services.AddSingleton(configuration);

        services.AddLogging();

        services.AddArcanumApiServices(configuration);

        return services.BuildServiceProvider();

    }

    private sealed class ExecutionProbe
    {

        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public void Record() => Interlocked.Increment(ref _executions);

    }

}
