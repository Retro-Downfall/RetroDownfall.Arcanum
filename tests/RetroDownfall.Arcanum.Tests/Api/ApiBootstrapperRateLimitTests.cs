using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
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

}
