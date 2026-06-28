using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class WardGateTests
{

    private const string TimeoutReason = "The ward held until timeout — action was not allowed";

    [Fact]
    public async Task WardAsync_ResolveAllow_ReturnsAllowedResolution()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-allow",
            "write_file",
            arguments: null,
            sessionId: "session-1",
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        ResolveStatus status = gate.Resolve("ward-allow", allow: true, reason: "Operator approved");

        Assert.Equal(ResolveStatus.Success, status);

        WardResolution resolution = await wardTask;

        Assert.True(resolution.Allowed);

        Assert.Equal("Operator approved", resolution.Reason);

    }

    [Fact]
    public async Task WardAsync_ResolveDeny_ReturnsDeniedResolution()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-deny",
            "execute_command",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        ResolveStatus status = gate.Resolve("ward-deny", allow: false, reason: "Too risky");

        Assert.Equal(ResolveStatus.Success, status);

        WardResolution resolution = await wardTask;

        Assert.False(resolution.Allowed);

        Assert.Equal("Too risky", resolution.Reason);

    }

    [Fact]
    public async Task WardAsync_Timeout_ReturnsDeniedWithTimeoutReason()
    {

        WardGate gate = CreateGate();

        WardResolution resolution = await gate.WardAsync(
            "ward-timeout",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromMilliseconds(75),
            CancellationToken.None);

        Assert.False(resolution.Allowed);

        Assert.Equal(TimeoutReason, resolution.Reason);

    }

    [Fact]
    public async Task WardAsync_DuplicateWardId_ThrowsInvalidOperationException()
    {

        WardGate gate = CreateGate();

        _ = gate.WardAsync(
            "ward-duplicate",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.WardAsync(
                "ward-duplicate",
                "write_file",
                arguments: null,
                sessionId: null,
                timeout: TimeSpan.FromSeconds(30),
                CancellationToken.None));

    }

    [Fact]
    public void Resolve_UnknownWard_ReturnsNotFound()
    {

        WardGate gate = CreateGate();

        ResolveStatus status = gate.Resolve("missing-ward", allow: true, reason: null);

        Assert.Equal(ResolveStatus.NotFound, status);

    }

    [Fact]
    public async Task Resolve_AlreadyResolved_ReturnsAlreadyResolved()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-twice",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ResolveStatus.Success, gate.Resolve("ward-twice", allow: true, reason: "first"));

        WardResolution first = await wardTask;

        Assert.True(first.Allowed);

        ResolveStatus second = gate.Resolve("ward-twice", allow: false, reason: "second");

        Assert.Equal(ResolveStatus.AlreadyResolved, second);

    }

    [Fact]
    public async Task WardAsync_CallerCancellation_ThrowsOperationCanceledException()
    {

        WardGate gate = CreateGate();

        using CancellationTokenSource cts = new();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-cancel",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wardTask);

    }

    [Fact]
    public async Task GetActiveWards_IncludesPendingWardMetadata()
    {

        WardGate gate = CreateGate();

        using JsonDocument arguments = JsonDocument.Parse("""{"path":"README.md"}""");

        _ = gate.WardAsync(
            "ward-active",
            "read_file_chunk",
            arguments,
            sessionId: "sess-42",
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        IReadOnlyList<ActiveWard> active = gate.GetActiveWards();

        ActiveWard ward = Assert.Single(active);

        Assert.Equal("ward-active", ward.WardId);

        Assert.Equal("read_file_chunk", ward.ToolName);

        Assert.Equal("sess-42", ward.SessionId);

        Assert.True(ward.ExpiresAt > ward.PlacedAt);

        gate.Resolve("ward-active", allow: true, reason: null);

    }

    [Fact]
    public async Task Resolve_BeforeTimeout_PreventsTimeoutResolution()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-preempt",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromMilliseconds(500),
            CancellationToken.None);

        gate.Resolve("ward-preempt", allow: true, reason: "Resolved early");

        WardResolution resolution = await wardTask;

        Assert.True(resolution.Allowed);

        Assert.Equal("Resolved early", resolution.Reason);

    }

    [Fact]
    public async Task WardAsync_CompletedWard_PrunesResolvedTombstone()
    {

        WardGate gate = CreateGate(timeoutSeconds: 1);

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-prune",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ResolveStatus.Success, gate.Resolve("ward-prune", allow: true, reason: "ok"));

        _ = await wardTask;

        Assert.Equal(ResolveStatus.AlreadyResolved, gate.Resolve("ward-prune", allow: false, reason: "late"));

        Assert.Equal(ResolveStatus.AlreadyResolved, gate.Resolve("ward-prune", allow: true, reason: "tombstone retained"));

    }

    [Fact]
    public async Task WardAsync_AtMaxActiveWards_AutoDenies()
    {

        WardGate gate = CreateGate(maxActiveWards: 1);

        _ = gate.WardAsync(
            "ward-cap-1",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        WardResolution resolution = await gate.WardAsync(
            "ward-cap-2",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(resolution.Allowed);

        Assert.Equal("Maximum active wards reached — action was not allowed", resolution.Reason);

        gate.Resolve("ward-cap-1", allow: true, reason: null);

    }

    // W3.3 Fix 1: the soft cap must be enforced atomically. The old code did a
    // non-atomic check-then-add (`if (_pending.Count >= max) return; ... TryAdd`),
    // so N concurrent submissions could all pass the count check and overshoot the
    // cap. The fix uses an Interlocked increment-then-compare-then-rollback counter
    // (mirroring SseConnectionGate). A Barrier releases all submissions onto the
    // gate at the same instant so the check-then-add window is actually contested;
    // with MaxActiveWards=K < N, at most K wards may ever be active simultaneously.
    [Fact]
    public async Task WardAsync_ConcurrentSubmissions_NeverOvershootsMaxActiveWards()
    {

        const int maxActiveWards = 4;

        const int submissionCount = 16;

        WardGate gate = CreateGate(maxActiveWards: maxActiveWards);

        TimeSpan longTimeout = TimeSpan.FromSeconds(30);

        Task<WardResolution>[] tasks = Enumerable.Range(0, submissionCount)
            .Select(i => Task.Run(() =>
            {

                return gate.WardAsync(
                    $"ward-race-{i}",
                    "write_file",
                    arguments: null,
                    sessionId: null,
                    longTimeout,
                    CancellationToken.None);

            }))
            .ToArray();

        // The Interlocked counter makes simultaneity unnecessary, so no Barrier. Poll until the
        // capacity-rejected submissions have completed (they return immediately) and the rest are
        // active (held wards await their TCS and do not complete until resolved below). Use a
        // generous deadline — thread-pool scheduling under parallel test load can delay task startup.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        int active;

        while (true)
        {

            active = gate.GetActiveWards().Count;

            int completed = tasks.Count(t => t.IsCompleted);

            if (active + completed == submissionCount)
            {

                break;

            }

            if (DateTimeOffset.UtcNow > deadline)
            {

                throw new TimeoutException(
                    $"Ward race did not reach steady state: active={active}, completed={completed}.");

            }

            await Task.Delay(25);

        }

        Assert.True(active <= maxActiveWards, $"Overshot cap: {active} active wards > {maxActiveWards}.");

        foreach (ActiveWard ward in gate.GetActiveWards())
        {

            Assert.Equal(ResolveStatus.Success, gate.Resolve(ward.WardId, allow: true, reason: "test"));

        }

        WardResolution[] resolutions = await Task.WhenAll(tasks);

        int denied = resolutions.Count(r => !r.Allowed && r.Reason == "Maximum active wards reached — action was not allowed");

        Assert.Equal(submissionCount, active + denied);

    }

    private static WardGate CreateGate(int timeoutSeconds = 30, int maxActiveWards = 50) =>
        new(new FakeOptionsMonitor(new ArcanumSettings
        {
            Ward = new WardSettings
            {
                TimeoutSeconds = timeoutSeconds,
                MaxActiveWards = maxActiveWards,
            },
        }));

    private sealed class FakeOptionsMonitor(ArcanumSettings value) : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue { get; } = value;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

    }

}
