using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

/// <summary>
/// Installs a fake clock and TTL on <see cref="SessionAttachmentToolAmbient"/> and clears its two
/// binding maps around every test. Clock, TTL, and both maps are statics the whole process shares,
/// and the real in-process MCP path binds into and resolves out of those same maps, so this has to
/// be serialized against everything else.
/// </summary>
[Collection(ProcessGlobalSeamCollectionName.Value)]
public sealed class SessionAttachmentToolAmbientTtlTests : IDisposable
{

    public SessionAttachmentToolAmbientTtlTests()
    {
        SessionAttachmentToolAmbient.ResetTestSeams();
    }

    public void Dispose()
    {
        SessionAttachmentToolAmbient.ResetTestSeams();
    }

    [Fact]
    public void Abandoned_BindRequest_Leaks_Until_Ttl_Sweep()
    {
        Guid sessionId = Guid.NewGuid();
        long now = 1_000_000L;
        SessionAttachmentToolAmbient.SetUtcTicksNowForTests(() => now);
        SessionAttachmentToolAmbient.SetBindingTtlForTests(TimeSpan.FromTicks(100));

        SessionAttachmentToolAmbient.BindRequest("conn-a", "req-1", sessionId);

        Assert.Equal(1, SessionAttachmentToolAmbient.RequestBindingCountForTests);
        Assert.True(SessionAttachmentToolAmbient.TryResolveRequest("conn-a", "req-1", out Guid resolved));
        Assert.Equal(sessionId, resolved);

        // Still within TTL — abandoned bind remains (the leak without sweep/TTL).
        now += 50;
        Assert.Equal(1, SessionAttachmentToolAmbient.RequestBindingCountForTests);

        // Past TTL — sweep clears the abandoned binding.
        now += 100;
        Assert.Equal(0, SessionAttachmentToolAmbient.RequestBindingCountForTests);
        Assert.False(SessionAttachmentToolAmbient.TryResolveRequest("conn-a", "req-1", out _));
    }

    [Fact]
    public void Abandoned_OpaqueToken_Leaks_Until_Ttl_Sweep()
    {
        Guid sessionId = Guid.NewGuid();
        long now = 5_000_000L;
        SessionAttachmentToolAmbient.SetUtcTicksNowForTests(() => now);
        SessionAttachmentToolAmbient.SetBindingTtlForTests(TimeSpan.FromTicks(100));

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);
        Assert.Equal(1, SessionAttachmentToolAmbient.OpaqueTokenCountForTests);

        now += 50;
        Assert.Equal(1, SessionAttachmentToolAmbient.OpaqueTokenCountForTests);

        now += 100;
        Assert.Equal(0, SessionAttachmentToolAmbient.OpaqueTokenCountForTests);
        Assert.False(SessionAttachmentToolAmbient.TryTakeOpaqueToken(token, out _));
    }

    [Fact]
    public void UnbindRequest_Clears_Before_Ttl()
    {
        SessionAttachmentToolAmbient.SetBindingTtlForTests(TimeSpan.FromHours(1));
        SessionAttachmentToolAmbient.BindRequest("conn-b", "req-2", Guid.NewGuid());
        Assert.Equal(1, SessionAttachmentToolAmbient.RequestBindingCountForTests);

        SessionAttachmentToolAmbient.UnbindRequest("conn-b", "req-2");
        Assert.Equal(0, SessionAttachmentToolAmbient.RequestBindingCountForTests);
    }

}
