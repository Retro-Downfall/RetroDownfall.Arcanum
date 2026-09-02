using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ProcessEnvironment")]
public sealed class ApiBootstrapperRateLimitTests : IDisposable
{

    private readonly string? _originalHostAny;

    public ApiBootstrapperRateLimitTests()
    {

        _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    }

    [Fact]
    public void ResolveRateLimitPartitionKey_always_uses_remote_ip()
    {

        DefaultHttpContext context = new();

        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        context.Request.Headers["X-Arcanum-Key"] = "secret-key";

        context.Request.Headers.Authorization = "Bearer secret-token";

        string partitionKey = InvokeResolveRateLimitPartitionKey(context);

        Assert.Equal("ip:203.0.113.10", partitionKey);

    }

    [Fact]
    public void ResolveRateLimitPartitionKey_unknown_ip_uses_unknown_label()
    {

        DefaultHttpContext context = new();

        string partitionKey = InvokeResolveRateLimitPartitionKey(context);

        Assert.Equal("ip:unknown", partitionKey);

    }

    [Fact]
    public void IsRateLimitEnabled_ListenAnyForcesLimiter()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:ListenAny"] = "true",
            })
            .Build();

        Assert.True(InvokeIsRateLimitEnabled(configuration));

    }

    [Fact]
    public void IsRateLimitEnabled_RemovedHostRateLimitKeyCannotEnableLoopback()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:ListenAny"] = "false",
                ["Arcanum:Host:RateLimit:Enabled"] = "true",
            })
            .Build();

        Assert.False(InvokeIsRateLimitEnabled(configuration));
    }

    [Fact]
    public async Task OnRejected_V1PathEmitsOpenAiErrorEnvelopeWithRetryAfter()
    {

        (HttpContext http, MemoryStream body) = CreateRejectedContext("/v1/chat/completions");

        await InvokeOnRejectedAsync(http, TimeSpan.FromSeconds(30));

        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);

        Assert.Equal("30", http.Response.Headers.RetryAfter.ToString());

        using JsonDocument document = ReadJson(body);

        JsonElement error = document.RootElement.GetProperty("error");

        Assert.Equal("rate_limit_error", error.GetProperty("type").GetString());

        Assert.Equal("rate_limit_exceeded", error.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        Assert.False(document.RootElement.TryGetProperty("data", out _));

    }

    [Fact]
    public async Task OnRejected_ApiPathKeepsArcanumEnvelopeAndAddsRetryAfter()
    {

        (HttpContext http, MemoryStream body) = CreateRejectedContext("/api/sessions");

        await InvokeOnRejectedAsync(http, TimeSpan.FromMilliseconds(1500));

        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);

        Assert.Equal("2", http.Response.Headers.RetryAfter.ToString());

        using JsonDocument document = ReadJson(body);

        Assert.False(document.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(
            ErrorCodes.RateLimit.TooManyRequests,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());

    }

    private static (HttpContext Http, MemoryStream Body) CreateRejectedContext(string path)
    {

        DefaultHttpContext http = new();

        http.Request.Path = path;

        MemoryStream body = new();

        http.Response.Body = body;

        return (http, body);

    }

    private static JsonDocument ReadJson(MemoryStream body)
    {

        body.Position = 0;

        return JsonDocument.Parse(body);

    }

    private static async Task InvokeOnRejectedAsync(HttpContext http, TimeSpan retryAfter)
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:ListenAny"] = "true",
            })
            .Build();

        ServiceCollection services = new();

        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "RegisterRateLimiter",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        _ = method.Invoke(null, [services, configuration]);

        await using ServiceProvider provider = services.BuildServiceProvider();

        RateLimiterOptions options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.NotNull(options.OnRejected);

        OnRejectedContext rejected = new()
        {
            HttpContext = http,
            Lease = new RetryAfterLease(retryAfter),
        };

        await options.OnRejected(rejected, CancellationToken.None);

    }

    private sealed class RetryAfterLease(TimeSpan retryAfter) : RateLimitLease
    {

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {

            if (string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {

                metadata = retryAfter;

                return true;

            }

            metadata = null;

            return false;

        }

    }

    /// <summary>
    /// The limiter's mechanics are entirely code-owned (<c>ArcanumRuntimeDefaults.HostRateLimit</c>
    /// plus the remote IP), so the per-request partitioner must not reach into
    /// <see cref="HttpContext.RequestServices"/> at all — a DI resolve on the hottest path in the
    /// host that no limit value is ever read from is pure waste.
    /// </summary>
    [Fact]
    public void RateLimitPartitioner_does_not_resolve_request_services()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:ListenAny"] = "true",
            })
            .Build();

        ServiceCollection services = new();

        InvokeRegisterRateLimiter(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        RateLimiterOptions options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        object policy = ResolveRegisteredPolicy(options, ApiBootstrapper.ArcanumRateLimiterPolicyName, provider);

        using ServiceProvider emptyRequestServices = new ServiceCollection().BuildServiceProvider();

        DefaultHttpContext context = new()
        {
            RequestServices = emptyRequestServices,
        };

        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");

        object? partition = InvokeGetPartition(policy, context);

        Assert.NotNull(partition);
    }

    private static void InvokeRegisterRateLimiter(IServiceCollection services, IConfiguration configuration)
    {
        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "RegisterRateLimiter",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        Invoke(method, null, [services, configuration]);
    }

    private static object ResolveRegisteredPolicy(
        RateLimiterOptions options,
        string policyName,
        IServiceProvider provider)
    {
        PropertyInfo? policyMapProperty = typeof(RateLimiterOptions).GetProperty(
            "PolicyMap",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(policyMapProperty);

        IDictionary? policyMap = policyMapProperty.GetValue(options) as IDictionary;

        Assert.NotNull(policyMap);

        object? entry = policyMap[policyName];

        Assert.NotNull(entry);

        // Depending on the AddPolicy overload the framework stores either the policy itself or a
        // factory over IServiceProvider; both shapes end at the same partitioner.
        if (entry is Delegate factory)
        {
            entry = factory.DynamicInvoke(provider);

            Assert.NotNull(entry);
        }

        return entry;
    }

    private static object? InvokeGetPartition(object policy, HttpContext context)
    {
        MethodInfo? method = policy.GetType().GetMethod(
            "GetPartition",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        return Invoke(method, policy, [context]);
    }

    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();

            throw;
        }
    }

    private static string InvokeResolveRateLimitPartitionKey(HttpContext context)
    {

        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "ResolveRateLimitPartitionKey",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object? result = method.Invoke(null, [context]);

        return Assert.IsType<string>(result);

    }

    private static bool InvokeIsRateLimitEnabled(IConfiguration configuration)
    {

        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "IsRateLimitEnabled",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object? result = method.Invoke(null, [configuration]);

        return Assert.IsType<bool>(result);

    }

    // W2-10: UseArcanumRateLimiter's partition key is Connection.RemoteIpAddress
    // (ResolveRateLimitPartitionKey above), read straight off the connection with no
    // forwarded-headers processing in front of it. Behind the reverse proxy ListenAny's own remarks
    // name as the expected topology, that collapses every caller into the proxy's one address. This
    // builds a minimal host (WebApplication.CreateSlimBuilder + UseTestServer, the same pattern
    // ApiDomainSplitContractTests.RouteGraph uses) around the real UseArcanumRateLimiter extension
    // method — the production entry point named by the finding — and asserts that two requests
    // carrying different X-Forwarded-For values, from what UseTestServer reports as a loopback peer,
    // are seen downstream as their own distinct addresses rather than the one peer address.
    [Fact]
    public async Task UseArcanumRateLimiter_LoopbackPeer_HonorsForwardedForFromTheDefaultTrustedProxy()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "true");

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        // UseArcanumRateLimiter only requires that AddRateLimiter has been called somewhere; it does
        // not read or depend on which policy exists, so a no-op probe policy is enough to let
        // UseRateLimiter() activate without pulling in the full production policy.
        builder.Services.AddRateLimiter(static options =>
            options.AddPolicy("probe", static _ => RateLimitPartition.GetNoLimiter("probe")));

        await using WebApplication app = builder.Build();

        app.UseArcanumRateLimiter();

        app.MapGet("/probe", (HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        await app.StartAsync();

        try
        {

            HttpClient client = app.GetTestClient();

            HttpRequestMessage first = new(HttpMethod.Get, "/probe");

            first.Headers.Add("X-Forwarded-For", "203.0.113.10");

            string firstIp = await (await client.SendAsync(first)).Content.ReadAsStringAsync();

            HttpRequestMessage second = new(HttpMethod.Get, "/probe");

            second.Headers.Add("X-Forwarded-For", "198.51.100.20");

            string secondIp = await (await client.SendAsync(second)).Content.ReadAsStringAsync();

            Assert.Equal("203.0.113.10", firstIp);

            Assert.Equal("198.51.100.20", secondIp);

            Assert.NotEqual(firstIp, secondIp);

        }
        finally
        {

            await app.StopAsync();

        }

    }

}
