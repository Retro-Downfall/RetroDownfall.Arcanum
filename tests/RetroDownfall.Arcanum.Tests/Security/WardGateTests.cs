using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class WardGateTests
{

    private const string TimeoutReason = "The ward held until timeout — action was not allowed";

    private const string CapacityReason = "Maximum active wards reached — action was not allowed";

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
    public async Task Resolve_ConcurrentLateAttempts_ReturnAlreadyResolved()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-concurrent-late",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(
            ResolveStatus.Success,
            gate.Resolve("ward-concurrent-late", allow: true, reason: "first"));

        WardResolution first = await wardTask;

        Assert.True(first.Allowed);

        ResolveStatus[] lateStatuses = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                    gate.Resolve("ward-concurrent-late", allow: false, reason: "late"))));

        Assert.All(lateStatuses, status => Assert.Equal(ResolveStatus.AlreadyResolved, status));

    }

    [Fact]
    public async Task Resolve_ConcurrentAttempts_OneSucceedsAndRestAreAlreadyResolved()
    {

        const int iterationCount = 10;

        const int resolverCount = 32;

        for (int iteration = 0; iteration < iterationCount; iteration++)
        {

            string wardId = $"ward-concurrent-{iteration}";

            WardGate gate = CreateGate();

            Task<WardResolution> wardTask = gate.WardAsync(
                wardId,
                "write_file",
                arguments: null,
                sessionId: null,
                timeout: TimeSpan.FromSeconds(30),
                CancellationToken.None);

            using var start = new Barrier(resolverCount + 1);

            Task<ResolveStatus>[] attempts = Enumerable.Range(0, resolverCount)
                .Select(i => Task.Factory.StartNew(
                    () =>
                    {

                        if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                        {

                            throw new TimeoutException("Concurrent resolvers did not reach the start barrier.");

                        }

                        return gate.Resolve(wardId, allow: i == 0, reason: $"resolver-{i}");

                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));

            ResolveStatus[] statuses = await Task.WhenAll(attempts);

            Assert.Equal(1, statuses.Count(status => status == ResolveStatus.Success));

            Assert.Equal(
                resolverCount - 1,
                statuses.Count(status => status == ResolveStatus.AlreadyResolved));

            Assert.DoesNotContain(ResolveStatus.NotFound, statuses);

            _ = await wardTask;

        }

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
    public async Task Resolve_ExpiredTombstone_ReturnsNotFoundWhileFreshTombstoneIsRetained()
    {

        int configuredTimeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(
            ArcanumRuntimeDefaults.Ward.TimeoutSeconds);

        FakeTimeProvider timeProvider = new();

        WardGate gate = CreateGate(timeProvider);

        Task<WardResolution> staleWardTask = gate.WardAsync(
            "ward-stale",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ResolveStatus.Success, gate.Resolve("ward-stale", allow: true, reason: "ok"));

        _ = await staleWardTask;

        Assert.Equal(
            ResolveStatus.AlreadyResolved,
            gate.Resolve("ward-stale", allow: false, reason: "still retained"));

        int retentionSeconds = ArcanumSettingClamps.WardTimeoutSeconds(configuredTimeoutSeconds) + 60;

        timeProvider.Advance(TimeSpan.FromSeconds(retentionSeconds + 1));

        Task<WardResolution> freshWardTask = gate.WardAsync(
            "ward-fresh",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ResolveStatus.Success, gate.Resolve("ward-fresh", allow: true, reason: "fresh"));

        _ = await freshWardTask;

        Assert.Equal(ResolveStatus.NotFound, gate.Resolve("ward-stale", allow: false, reason: "expired"));

        Assert.Equal(
            ResolveStatus.AlreadyResolved,
            gate.Resolve("ward-fresh", allow: false, reason: "fresh tombstone retained"));

    }

    [Fact]
    public async Task WardAsync_MissingWardSettings_UsesDefaultCapacityBoundary()
    {

        int defaultCapacity = ArcanumSettingClamps.MaxActiveWards(
            ArcanumRuntimeDefaults.Ward.MaxActiveWards);

        WardGate gate = new(new FakeOptionsMonitor(new ArcanumSettings
        {
            Security = new SecuritySettings { Ward = null! },
        }));

        Task<WardResolution>[] admitted = Enumerable.Range(0, defaultCapacity)
            .Select(i => gate.WardAsync(
                $"ward-default-{i}",
                "write_file",
                arguments: null,
                sessionId: null,
                timeout: TimeSpan.FromMinutes(2),
                CancellationToken.None))
            .ToArray();

        Assert.Equal(defaultCapacity, gate.GetActiveWards().Count);

        WardResolution overflow = await gate.WardAsync(
            "ward-default-overflow",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.False(overflow.Allowed);

        Assert.Equal(CapacityReason, overflow.Reason);

        foreach (ActiveWard ward in gate.GetActiveWards())
        {

            Assert.Equal(ResolveStatus.Success, gate.Resolve(ward.WardId, allow: true, reason: "cleanup"));

        }

        WardResolution[] resolutions = await Task.WhenAll(admitted);

        Assert.All(resolutions, resolution => Assert.True(resolution.Allowed));

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

        int maxActiveWards = ArcanumSettingClamps.MaxActiveWards(
            ArcanumRuntimeDefaults.Ward.MaxActiveWards);
        int submissionCount = maxActiveWards + 12;

        WardGate gate = CreateGate();

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

    // Issue #53: an automatic decision must never insert a live waiter. The gate performs an atomic
    // create→resolved transition so neither GET /api/wards nor a competing POST can observe the ward
    // as approvable, and the automatic result can never be overwritten by a manual resolver.

    [Fact]
    public void RecordAutomaticResolution_ProducesAnAllowedResolutionWithTheRequestedOrigin()
    {

        WardGate gate = CreateGate();

        WardResolution resolution = gate.RecordAutomaticResolution(
            "ward-auto-allow",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        Assert.True(resolution.Allowed);

        Assert.Equal(AutoApproveReason, resolution.Reason);

        Assert.Equal(WardResolutionOrigin.AutoApproved, resolution.Origin);

    }

    [Fact]
    public void RecordAutomaticResolution_NeverExposesTheWardAsActive()
    {

        WardGate gate = CreateGate();

        _ = gate.RecordAutomaticResolution(
            "ward-auto-invisible",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        Assert.Empty(gate.GetActiveWards());

    }

    [Fact]
    public void RecordAutomaticResolution_MakesACompetingManualResolveReturnAlreadyResolved()
    {

        WardGate gate = CreateGate();

        _ = gate.RecordAutomaticResolution(
            "ward-auto-race",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        Assert.Equal(
            ResolveStatus.AlreadyResolved,
            gate.Resolve("ward-auto-race", allow: false, reason: "manual deny after auto-approval"));

    }

    [Fact]
    public async Task RecordAutomaticResolution_ConcurrentManualResolvers_CannotChangeTheResult()
    {

        const int resolverCount = 32;

        WardGate gate = CreateGate();

        using Barrier start = new(resolverCount + 1);

        Task<ResolveStatus>[] attempts = Enumerable.Range(0, resolverCount)
            .Select(i => Task.Factory.StartNew(
                () =>
                {

                    if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    {

                        throw new TimeoutException("Concurrent resolvers did not reach the start barrier.");

                    }

                    return gate.Resolve("ward-auto-contended", allow: false, reason: $"resolver-{i}");

                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        WardResolution automatic = gate.RecordAutomaticResolution(
            "ward-auto-contended",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));

        ResolveStatus[] statuses = await Task.WhenAll(attempts);

        // Every resolver either arrived before the tombstone (NotFound — there is no live ward to
        // resolve) or after it (AlreadyResolved). None may succeed, because success would mean a
        // manual decision replaced the automatic one.
        Assert.DoesNotContain(ResolveStatus.Success, statuses);

        Assert.True(automatic.Allowed);

    }

    [Fact]
    public async Task RecordAutomaticResolution_OnALiveWard_FailsClosedAndLeavesTheWaiterPending()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-auto-collision",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        WardResolution automatic = gate.RecordAutomaticResolution(
            "ward-auto-collision",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        Assert.False(automatic.Allowed);

        Assert.False(wardTask.IsCompleted);

        Assert.Equal(
            ResolveStatus.Success,
            gate.Resolve("ward-auto-collision", allow: false, reason: "operator"));

        WardResolution resolution = await wardTask;

        Assert.False(resolution.Allowed);

        Assert.Equal(WardResolutionOrigin.Human, resolution.Origin);

    }

    [Fact]
    public void RecordAutomaticResolution_Twice_KeepsTheFirstDecision()
    {

        WardGate gate = CreateGate();

        _ = gate.RecordAutomaticResolution(
            "ward-auto-twice",
            allowed: true,
            reason: AutoApproveReason,
            WardResolutionOrigin.AutoApproved);

        WardResolution second = gate.RecordAutomaticResolution(
            "ward-auto-twice",
            allowed: false,
            reason: "second",
            WardResolutionOrigin.AutoDenied);

        Assert.True(second.Allowed);

        Assert.Equal(WardResolutionOrigin.AutoApproved, second.Origin);

    }

    [Fact]
    public async Task Resolve_RecordsTheHumanOrigin()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-origin-human",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(
            ResolveStatus.Success,
            gate.Resolve("ward-origin-human", allow: true, reason: "Operator approved"));

        WardResolution resolution = await wardTask;

        Assert.Equal(WardResolutionOrigin.Human, resolution.Origin);

    }

    [Fact]
    public async Task WardAsync_Timeout_RecordsTheTimedOutOrigin()
    {

        WardGate gate = CreateGate();

        WardResolution resolution = await gate.WardAsync(
            "ward-origin-timeout",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromMilliseconds(75),
            CancellationToken.None);

        Assert.Equal(WardResolutionOrigin.TimedOut, resolution.Origin);

    }

    [Fact]
    public async Task WardAsync_CapacityRejection_RecordsTheAutomaticDenialOrigin()
    {

        int maxActiveWards = ArcanumSettingClamps.MaxActiveWards(
            ArcanumRuntimeDefaults.Ward.MaxActiveWards);

        WardGate gate = CreateGate();

        Task<WardResolution>[] admitted = Enumerable.Range(0, maxActiveWards)
            .Select(i => gate.WardAsync(
                $"ward-origin-capacity-{i}",
                "write_file",
                arguments: null,
                sessionId: null,
                timeout: TimeSpan.FromMinutes(2),
                CancellationToken.None))
            .ToArray();

        WardResolution overflow = await gate.WardAsync(
            "ward-origin-capacity-overflow",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.False(overflow.Allowed);

        Assert.Equal(CapacityReason, overflow.Reason);

        Assert.Equal(WardResolutionOrigin.AutoDenied, overflow.Origin);

        foreach (ActiveWard ward in gate.GetActiveWards())
        {

            Assert.Equal(ResolveStatus.Success, gate.Resolve(ward.WardId, allow: true, reason: "cleanup"));

        }

        _ = await Task.WhenAll(admitted);

    }

    private const string AutoApproveReason = "Auto-approved by operator policy";

    private static WardGate CreateGate(TimeProvider? timeProvider = null) =>
        new(
            new FakeOptionsMonitor(new ArcanumSettings()),
            timeProvider);

    // W3.4 Group B: WardEntry.Arguments holds a JsonDocument that owns pooled native memory.
    // On every terminal path out of _pending (resolve / timeout / caller-cancel) the entry
    // is removed but the JsonDocument is never disposed, leaking native buffers across the
    // process lifetime. Each of the three terminal paths must dispose the Arguments. A
    // disposed JsonDocument throws ObjectDisposedException on RootElement access.

    [Fact]
    public async Task WardAsync_Resolve_disposes_arguments_JsonDocument()
    {

        WardGate gate = CreateGate();

        JsonDocument arguments = JsonDocument.Parse("""{"path":"README.md"}""");

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-dispose-resolve",
            "write_file",
            arguments,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        gate.Resolve("ward-dispose-resolve", allow: true, reason: "ok");

        _ = await wardTask;

        Assert.Throws<ObjectDisposedException>(() => arguments.RootElement.ValueKind);

    }

    [Fact]
    public async Task WardAsync_Timeout_disposes_arguments_JsonDocument()
    {

        WardGate gate = CreateGate();

        JsonDocument arguments = JsonDocument.Parse("""{"path":"README.md"}""");

        _ = await gate.WardAsync(
            "ward-dispose-timeout",
            "write_file",
            arguments,
            sessionId: null,
            timeout: TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        // The timeout path runs in the background (RunTimeoutAsync); give it a moment to
        // remove the entry and dispose the arguments.
        await Task.Delay(150);

        Assert.Throws<ObjectDisposedException>(() => arguments.RootElement.ValueKind);

    }

    [Fact]
    public async Task WardAsync_CallerCancel_disposes_arguments_JsonDocument()
    {

        WardGate gate = CreateGate();

        JsonDocument arguments = JsonDocument.Parse("""{"path":"README.md"}""");

        using CancellationTokenSource cts = new();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-dispose-cancel",
            "write_file",
            arguments,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wardTask);

        // The caller-cancel callback disposes the arguments; on heavily loaded runners the
        // callback can lag the observed task completion, so wait for the disposal rather than
        // assuming it is synchronously visible.
        for (int i = 0; i < 200; i++)
        {
            try
            {
                _ = arguments.RootElement.ValueKind;
                await Task.Delay(25);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        Assert.Throws<ObjectDisposedException>(() => arguments.RootElement.ValueKind);

    }

    [Fact]
    public async Task WardAsync_NullArguments_does_not_throw_on_resolve()
    {

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            "ward-dispose-null",
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ResolveStatus.Success, gate.Resolve("ward-dispose-null", allow: true, reason: "ok"));

        _ = await wardTask;

    }

    [Fact]
    public async Task TimedOutResolution_AfterOperatorResolution_DoesNotOverwriteOperatorDecision()
    {

        const string wardId = "ward-timeout-loses-race";

        WardGate gate = CreateGate();

        Task<WardResolution> wardTask = gate.WardAsync(
            wardId,
            "write_file",
            arguments: null,
            sessionId: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(
            ResolveStatus.Success,
            gate.Resolve(wardId, allow: true, reason: "Operator approved"));

        WardResolution resolution = await wardTask;

        Assert.False(gate.TryResolveTimedOutWard(wardId));

        Assert.True(resolution.Allowed);

        Assert.Equal("Operator approved", resolution.Reason);

        Assert.NotEqual(TimeoutReason, resolution.Reason);

        Assert.Empty(gate.GetActiveWards());

        Assert.Equal(
            ResolveStatus.AlreadyResolved,
            gate.Resolve(wardId, allow: false, reason: "late timeout"));

    }

    private sealed class FakeOptionsMonitor(ArcanumSettings value) : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue { get; } = value;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

    }

}
