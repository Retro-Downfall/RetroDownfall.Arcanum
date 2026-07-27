using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class ExpiringRequestBindingStoreTests
{
    [Fact]
    public void Resolve_expires_requested_binding_before_periodic_sweep()
    {
        long now = 0;
        ExpiringRequestBindingStore<string> store = new(
            ttl: 100,
            sweepInterval: 1_000,
            getTimestamp: () => now);

        store.Bind("connection", "request", "value");
        now = 101;

        Assert.False(
            store.TryResolve(
                "connection",
                "request",
                out _));
        Assert.Equal(0, store.CountForTests);
    }

    [Fact]
    public void Due_sweep_removes_only_expired_abandoned_bindings()
    {
        long now = 0;
        ExpiringRequestBindingStore<string> store = new(
            ttl: 100,
            sweepInterval: 200,
            getTimestamp: () => now);

        store.Bind("connection", "stale", "old");
        now = 150;
        store.Bind("connection", "current", "new");
        Assert.Equal(2, store.CountForTests);

        now = 201;

        Assert.True(
            store.TryResolve(
                "connection",
                "current",
                out string? value));
        Assert.Equal("new", value);
        Assert.Equal(1, store.CountForTests);
    }

    [Fact]
    public void Connection_scoping_and_unbind_are_preserved()
    {
        ExpiringRequestBindingStore<int> store = new(
            ttl: 100,
            sweepInterval: 50,
            getTimestamp: static () => 0);

        store.Bind("first", "same-request", 1);
        store.Bind("second", "same-request", 2);
        store.Unbind("first", "same-request");

        Assert.False(
            store.TryResolve(
                "first",
                "same-request",
                out _));
        Assert.True(
            store.TryResolve(
                "second",
                "same-request",
                out int value));
        Assert.Equal(2, value);
    }
}
