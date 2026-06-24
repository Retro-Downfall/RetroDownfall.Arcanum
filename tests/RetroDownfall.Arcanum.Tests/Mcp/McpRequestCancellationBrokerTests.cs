using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpRequestCancellationBrokerTests
{

    [Fact]
    public void GetTokenOrFallback_returns_registered_caller_token()
    {

        McpRequestCancellationBroker broker = new();

        using CancellationTokenSource cts = new();

        broker.Register("req-1", cts.Token);

        CancellationToken registered = broker.GetTokenOrFallback("req-1", CancellationToken.None);

        Assert.Equal(cts.Token, registered);

        Assert.Equal(CancellationToken.None, broker.GetTokenOrFallback("missing", CancellationToken.None));

    }

    [Fact]
    public void Register_duplicate_id_throws()
    {

        McpRequestCancellationBroker broker = new();

        broker.Register("dup", CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => broker.Register("dup", CancellationToken.None));

    }

    [Fact]
    public void Unregister_removes_entry_and_disposes_linked_source()
    {

        McpRequestCancellationBroker broker = new();

        using CancellationTokenSource cts = new();

        broker.Register("req-2", cts.Token);

        CancellationToken registered = broker.GetTokenOrFallback("req-2", CancellationToken.None);

        broker.Unregister("req-2");

        Assert.Equal(CancellationToken.None, broker.GetTokenOrFallback("req-2", CancellationToken.None));

        Assert.False(registered.IsCancellationRequested);

    }

    [Fact]
    public void Caller_token_cancellation_unregisters_entry()
    {

        McpRequestCancellationBroker broker = new();

        using CancellationTokenSource cts = new();

        broker.Register("req-3", cts.Token);

        cts.Cancel();

        Assert.Equal(CancellationToken.None, broker.GetTokenOrFallback("req-3", CancellationToken.None));

    }

}
