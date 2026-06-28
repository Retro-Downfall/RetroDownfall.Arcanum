using Microsoft.AspNetCore.Http;
using System.Net.Http;
using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

public sealed class ClientDisconnectTests
{

    [Fact]
    public void IsClientDisconnect_returns_true_for_IOException()
    {

        bool result = ClientDisconnect.IsClientDisconnect(new IOException("broken pipe"));

        Assert.True(result);

    }

    [Fact]
    public void IsClientDisconnect_returns_true_for_HttpIOException()
    {

        bool result = ClientDisconnect.IsClientDisconnect(new HttpIOException(HttpRequestError.ConnectionError, "reset"));

        Assert.True(result);

    }

    [Fact]
    public void IsClientDisconnect_returns_true_for_OCE_when_request_aborted()
    {

        DefaultHttpContext ctx = new();

        CancellationTokenSource cts = new();

        ctx.RequestAborted = cts.Token;

        cts.Cancel();

        OperationCanceledException oce = new(ctx.RequestAborted);

        bool result = ClientDisconnect.IsClientDisconnect(oce, ctx);

        Assert.True(result);

    }

    [Fact]
    public void IsClientDisconnect_returns_false_for_clean_cooperative_OCE()
    {

        DefaultHttpContext ctx = new();

        CancellationTokenSource cooperativeCts = new();

        OperationCanceledException oce = new(cooperativeCts.Token);

        bool result = ClientDisconnect.IsClientDisconnect(oce, ctx);

        Assert.False(result);

    }

    [Fact]
    public void IsClientDisconnect_returns_false_for_application_exception()
    {

        bool result = ClientDisconnect.IsClientDisconnect(new InvalidOperationException("boom"));

        Assert.False(result);

    }

    [Fact]
    public void IsClientDisconnect_returns_false_for_null()
    {

        bool result = ClientDisconnect.IsClientDisconnect(null);

        Assert.False(result);

    }

}
