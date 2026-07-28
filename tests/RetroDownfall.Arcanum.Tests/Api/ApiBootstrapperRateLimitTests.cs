using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api;

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
