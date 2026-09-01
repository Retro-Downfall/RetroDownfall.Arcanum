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
        SessionAttachmentToolAmbient.SetTicksNowForTests(() => now);
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
        SessionAttachmentToolAmbient.SetTicksNowForTests(() => now);
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

    // The fake-clock seam above (SetTicksNowForTests) fully replaces the tick source, so a test
    // that installs its own closure can never tell a wall-clock default from a monotonic one — both
    // are driven entirely by whatever the test supplies from that point on. The only way to observe
    // which real source production wires up by default is to read the untouched default directly,
    // which is why this test does not go through BindRequest/TryResolveRequest at all.
    [Fact]
    public void Default_tick_source_is_monotonic_uptime_not_wall_clock_utc_ticks()
    {
        long ticks = SessionAttachmentToolAmbient.CurrentTicksNowForTests;

        // DateTime.UtcNow.Ticks today is on the order of 6.4e17 (100ns ticks since 0001-01-01).
        // Even ten years of continuous machine uptime, expressed in that same 100ns-tick unit, is
        // under 3.2e15 — a monotonic uptime source and a wall-clock source can never land in the
        // same decade-wide band, so this bound tells the two apart without needing to rewind a real
        // clock (which is impossible in-process).
        Assert.True(
            ticks < TimeSpan.FromDays(3650).Ticks,
            $"Default tick source read {ticks}, which is wall-clock-sized; expected an uptime-sized value under {TimeSpan.FromDays(3650).Ticks}.");
    }

}
