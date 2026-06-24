using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ApiBootstrapperRateLimitTests
{

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

    private static string InvokeResolveRateLimitPartitionKey(HttpContext context)
    {

        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "ResolveRateLimitPartitionKey",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object? result = method.Invoke(null, [context]);

        return Assert.IsType<string>(result);

    }

}
