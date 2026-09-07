using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Resilience;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Resilience;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Resilience;

/// <summary>
/// The probe scheduler's Task.Delay sat after the work inside the same try, so an exception
/// thrown before the delay was reached looped immediately with no backoff.
/// </summary>
public sealed class ProviderHealthProbeServiceTests
{
    [Fact]
    public async Task DeniedProbeLeavesPreviousHealthUnchanged()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ProbeDouble probe = new(static _ => Task.FromResult(true));

        RecordingHealthTracker tracker = new(initiallyHealthy: false);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(inner);

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(0, probe.CallCount);

        Assert.Equal(0, tracker.PublicationCount);

        Assert.False(tracker.IsHealthy(ProviderName));

        Assert.Equal([GrimoireWorkKind.ProviderHealthProbe], admission.RequestedWorkKinds);

        Assert.Equal(0, admission.EffectGroupAttempts);

        await ReopenAsync(closed);
    }

    [Fact]
    public async Task DeniedEffectGroupLeavesPreviousHealthUnchanged()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        IGrimoireClosingOwner? closingOwner = null;

        admission.BeforeEffectGroupAdmission = () =>
            closingOwner = BeginClosing(inner);

        ProbeDouble probe = new(static _ => Task.FromResult(true));

        RecordingHealthTracker tracker = new(initiallyHealthy: false);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(0, probe.CallCount);

        Assert.Equal(0, tracker.PublicationCount);

        Assert.False(tracker.IsHealthy(ProviderName));

        Assert.Equal(1, admission.EffectGroupAttempts);

        Assert.NotNull(closingOwner);

        IGrimoireExclusiveClosedLease closed = await DrainAndCloseAsync(inner, closingOwner!);

        await ReopenAsync(closed);
    }

    [Fact]
    public async Task WinningProbeDrainsThroughTrackerPublication()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        List<string> phases = [];

        AsyncCheckpoint groupDisposal = new();

        AsyncCheckpoint workDisposal = new();

        admission.BeforeEffectGroupDisposalAsync = () =>
        {
            phases.Add("effect-dispose");

            return new ValueTask(groupDisposal.PauseAsync());
        };

        admission.BeforeWorkLeaseDisposalAsync = () =>
        {
            phases.Add("work-dispose");

            return new ValueTask(workDisposal.PauseAsync());
        };

        TaskCompletionSource allowProbe = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ProbeDouble probe = new(_ =>
        {
            phases.Add("probe");

            allowProbe.Task.GetAwaiter().GetResult();

            return Task.FromResult(true);
        });

        RecordingHealthTracker tracker = new(
            initiallyHealthy: false,
            blockPublication: true,
            beforePublication: _ => phases.Add("publication"));

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        Task pass = Task.Factory.StartNew(
                () => service.ProbeAllProvidersAsync(CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await probe.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        IGrimoireClosingOwner closingOwner = BeginClosing(inner);

        Task<Result> drain = inner.DrainRequestAndWorkAsync(
            closingOwner,
            CancellationToken.None).AsTask();

        try
        {
            Assert.False(drain.IsCompleted);

            allowProbe.TrySetResult();

            await tracker.PublicationReached.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(drain.IsCompleted);

            tracker.ReleasePublication();

            await groupDisposal.Reached.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(drain.IsCompleted);

            groupDisposal.Release();

            await workDisposal.Reached.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(drain.IsCompleted);

            workDisposal.Release();
        }
        finally
        {
            allowProbe.TrySetResult();

            tracker.ReleasePublication();

            groupDisposal.Release();

            workDisposal.Release();
        }

        await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal(1, tracker.HealthyPublications);

        Assert.Equal(1, admission.EffectGroupAttempts);

        Assert.Equal(
            ["probe", "publication", "effect-dispose", "work-dispose"],
            phases);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(inner, closingOwner);

        await ReopenAsync(closed);
    }

    [Fact]
    public async Task RealProbeFailureStillMarksUnhealthy()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ProbeDouble probe = new(static _ =>
            Task.FromException<bool>(new InvalidOperationException("synthetic probe failure")));

        RecordingHealthTracker tracker = new(initiallyHealthy: true);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(1, probe.CallCount);

        Assert.Equal(1, tracker.FailedPublications);

        Assert.False(tracker.IsHealthy(ProviderName));

        Assert.Equal([GrimoireWorkKind.ProviderHealthProbe], admission.RequestedWorkKinds);

        Assert.Equal(1, admission.EffectGroupAttempts);
    }

    [Fact]
    public async Task EachProviderOwnsAnIndependentEffectGroupAndWorkLease()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        List<string> disposalOrder = [];

        admission.BeforeEffectGroupDisposalAsync = () =>
        {
            disposalOrder.Add("effect");

            return ValueTask.CompletedTask;
        };

        admission.BeforeWorkLeaseDisposalAsync = () =>
        {
            disposalOrder.Add("work");

            return ValueTask.CompletedTask;
        };

        ProbeDouble probe = new(static _ => Task.FromResult(true));

        RecordingHealthTracker tracker = new(initiallyHealthy: false);

        ProviderHealthProbeService service = CreateService(
            admission,
            probe,
            tracker,
            "first-provider",
            "second-provider");

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(2, probe.CallCount);

        Assert.Equal(2, tracker.HealthyPublications);

        Assert.Equal(
            [GrimoireWorkKind.ProviderHealthProbe, GrimoireWorkKind.ProviderHealthProbe],
            admission.RequestedWorkKinds);

        Assert.Equal(2, admission.EffectGroupAttempts);

        Assert.Equal(["effect", "work", "effect", "work"], disposalOrder);
    }

    [Fact]
    public async Task HostCancellationDoesNotPublishUnhealthyObservation()
    {
        using CancellationTokenSource stopping = new();

        stopping.Cancel();

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ProbeDouble probe = new(token => Task.FromCanceled<bool>(token));

        RecordingHealthTracker tracker = new(initiallyHealthy: true);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProbeAllProvidersAsync(stopping.Token));

        Assert.Equal(0, probe.CallCount);

        Assert.Equal(0, tracker.PublicationCount);

        Assert.True(tracker.IsHealthy(ProviderName));
    }

    [Fact]
    public async Task InFlightHostCancellationUnwindsEffectThenWorkWithoutPublication()
    {
        using CancellationTokenSource stopping = new();

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        List<string> phases = [];

        admission.BeforeEffectGroupDisposalAsync = () =>
        {
            phases.Add("effect-dispose");

            return ValueTask.CompletedTask;
        };

        admission.BeforeWorkLeaseDisposalAsync = () =>
        {
            phases.Add("work-dispose");

            return ValueTask.CompletedTask;
        };

        ProbeDouble probe = new(token =>
        {
            phases.Add("probe");

            token.WaitHandle.WaitOne();

            return Task.FromCanceled<bool>(token);
        });

        RecordingHealthTracker tracker = new(initiallyHealthy: true);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        Task pass = Task.Factory.StartNew(
                () => service.ProbeAllProvidersAsync(stopping.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await probe.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pass.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, probe.CallCount);

        Assert.Equal(0, tracker.PublicationCount);

        Assert.Equal(["probe", "effect-dispose", "work-dispose"], phases);
    }

    [Fact]
    public async Task ForeignProbeCancellationPublishesUnhealthyObservation()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        List<string> disposalOrder = [];

        admission.BeforeEffectGroupDisposalAsync = () =>
        {
            disposalOrder.Add("effect");

            return ValueTask.CompletedTask;
        };

        admission.BeforeWorkLeaseDisposalAsync = () =>
        {
            disposalOrder.Add("work");

            return ValueTask.CompletedTask;
        };

        ProbeDouble probe = new(static _ =>
            Task.FromException<bool>(new OperationCanceledException(
                "foreign provider cancellation",
                new CancellationToken(canceled: true))));

        RecordingHealthTracker tracker = new(initiallyHealthy: true);

        ProviderHealthProbeService service = CreateService(admission, probe, tracker);

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(1, tracker.FailedPublications);

        Assert.False(tracker.IsHealthy(ProviderName));

        Assert.Equal(["effect", "work"], disposalOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondProviderRefusalPreservesCompletedFirstObservation(
        bool refusalAtEffectGroup)
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        IGrimoireClosingOwner? closingOwner = null;

        if (refusalAtEffectGroup)
        {
            int effectAttempts = 0;

            admission.BeforeEffectGroupAdmission = () =>
            {
                if (Interlocked.Increment(ref effectAttempts) == 2)
                {
                    closingOwner = BeginClosing(inner);
                }
            };
        }

        ProbeDouble probe = new(static _ => Task.FromResult(true));

        RecordingHealthTracker tracker = new(
            initiallyHealthy: false,
            beforePublication: providerName =>
            {
                if (!refusalAtEffectGroup && providerName == "first-provider")
                {
                    closingOwner = BeginClosing(inner);
                }
            });

        ProviderHealthProbeService service = CreateService(
            admission,
            probe,
            tracker,
            "first-provider",
            "second-provider");

        await service.ProbeAllProvidersAsync(CancellationToken.None);

        Assert.Equal(1, probe.CallCount);

        Assert.Equal(1, tracker.PublicationCount);

        Assert.Equal(["first-provider"], tracker.PublishedProviders);

        Assert.Equal(2, admission.RequestedWorkKinds.Count);

        Assert.Equal(refusalAtEffectGroup ? 2 : 1, admission.EffectGroupAttempts);

        Assert.NotNull(closingOwner);

        IGrimoireExclusiveClosedLease closed = await DrainAndCloseAsync(inner, closingOwner!);

        await ReopenAsync(closed);
    }

    /// <summary>
    /// Before this fix, an exception from <c>options.CurrentValue</c> — read before any per-provider
    /// try/catch, on the very first line of the probed work — was caught, logged, and immediately
    /// re-entered the loop with no delay at all. A hot spin would reach <c>CurrentValue</c> thousands
    /// of times in the short window this test allows; a proper backoff reaches it once and then
    /// sleeps for the shortest clamped interval, five real seconds.
    /// </summary>
    [Fact]
    public async Task A_tick_that_throws_before_probing_still_backs_off_instead_of_spinning()
    {

        ThrowingOptionsMonitor options = new();

        ProviderHealthTracker tracker = new(NullLogger<ProviderHealthTracker>.Instance);

        NeverCalledProbe probe = new();

        using ProviderHealthProbeService service = new(
            options,
            probe,
            tracker,
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            NullLogger<ProviderHealthProbeService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Waiting for the first tick rather than assuming 300ms contains it. A busy machine can
        // schedule the loop later than that, which left AccessCount at 0 and failed the lower half
        // of the range check below for a reason that had nothing to do with backing off.
        await options.FirstAccess.WaitAsync(TimeSpan.FromSeconds(30));

        // The window after the first tick is what a spin would fill. Only the upper bound depends on
        // it, so a slow machine can make this test pass for the right reason but never fail for the
        // wrong one.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Assert.InRange(AccessCount, 1, 5) alone cannot tell "backed off" apart from "faulted after
        // one tick": a regression that lets the tick's exception propagate (e.g. a stray `throw;` in
        // the scheduler's catch) would also leave AccessCount at exactly 1, forever, and still pass
        // that range check. Checking the loop is still alive closes that gap.
        Task? executeTask = service.ExecuteTask;

        Assert.NotNull(executeTask);

        Assert.False(executeTask.IsCompleted, "the scheduler loop must still be alive");

        await service.StopAsync(CancellationToken.None);

        Assert.InRange(options.AccessCount, 1, 5);

        Assert.Equal(0, probe.CallCount);

    }

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<ArcanumSettings>
    {

        private readonly TaskCompletionSource _firstAccess =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _accessCount;

        public int AccessCount => Volatile.Read(ref _accessCount);

        /// <summary>Completes when the scheduler has actually reached its first tick.</summary>
        public Task FirstAccess => _firstAccess.Task;

        public ArcanumSettings CurrentValue
        {
            get
            {

                Interlocked.Increment(ref _accessCount);

                _ = _firstAccess.TrySetResult();

                throw new InvalidOperationException("Synthetic options failure for W8-7.");

            }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

    }

    /// <summary>Never reached: the throw happens on the first line of ProbeAllProvidersAsync.</summary>
    private sealed class NeverCalledProbe : IProviderHealthProbe
    {

        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _callCount);

            return Task.FromResult(true);

        }

    }

    private const string ProviderName = "provider-health-test";

    private static ProviderHealthProbeService CreateService(
        IGrimoireConnectionAdmissionGate admission,
        IProviderHealthProbe probe,
        IProviderHealthTracker tracker,
        params string[] providerNames)
    {
        ServiceCollection services = new();

        services.AddSingleton(admission);

        using ServiceProvider provider = services.BuildServiceProvider();

        string[] names = providerNames.Length == 0
            ? [ProviderName]
            : providerNames;

        return ActivatorUtilities.CreateInstance<ProviderHealthProbeService>(
            provider,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Providers = names
                    .Select(static name => new ProviderSettings { Name = name })
                    .ToArray(),
            }),
            probe,
            tracker,
            NullLogger<ProviderHealthProbeService>.Instance);
    }

    private static CovenantExclusiveRecoveryOwner MaintenanceOwner() =>
        new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

    private static IGrimoireClosingOwner BeginClosing(
        GrimoireConnectionAdmissionGate gate)
    {
        Result<IGrimoireClosingOwner> beginning = gate.BeginOrResumeExclusive(MaintenanceOwner());

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        return beginning.Value;
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate)
    {
        IGrimoireClosingOwner closingOwner = BeginClosing(gate);

        return await DrainAndCloseAsync(gate, closingOwner);
    }

    private static async Task<IGrimoireExclusiveClosedLease> DrainAndCloseAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closingOwner)
    {
        Result drained = await gate.DrainRequestAndWorkAsync(
            closingOwner,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        return await CloseAsync(gate, closingOwner);
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closingOwner)
    {
        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            closingOwner,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        return closed.Value;
    }

    private static async Task ReopenAsync(IGrimoireExclusiveClosedLease closed)
    {
        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.Error.Message);

        await closed.DisposeAsync();
    }

    private sealed class ProbeDouble(
        Func<CancellationToken, Task<bool>> run) : IProviderHealthProbe
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal Task Entered => _entered.Task;

        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ProbeAsync(
            ProviderSettings provider,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _callCount);

            _entered.TrySetResult();

            return run(cancellationToken);
        }
    }

    private sealed class RecordingHealthTracker(
        bool initiallyHealthy,
        bool blockPublication = false,
        Action<string>? beforePublication = null) : IProviderHealthTracker
    {
        private readonly object _publishedProvidersGate = new();

        private readonly List<string> _publishedProviders = [];

        private readonly TaskCompletionSource _publicationReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowPublication = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _healthy = initiallyHealthy ? 1 : 0;

        private int _healthyPublications;

        private int _failedPublications;

        internal int HealthyPublications => Volatile.Read(ref _healthyPublications);

        internal int FailedPublications => Volatile.Read(ref _failedPublications);

        internal int PublicationCount => HealthyPublications + FailedPublications;

        internal Task PublicationReached => _publicationReached.Task;

        internal IReadOnlyList<string> PublishedProviders
        {
            get
            {
                lock (_publishedProvidersGate)
                {
                    return _publishedProviders.ToArray();
                }
            }
        }

        public event Action<ProviderHealthStatus>? HealthChanged
        {
            add { }
            remove { }
        }

        public bool IsHealthy(string providerName) => Volatile.Read(ref _healthy) == 1;

        public void MarkFailed(string providerName)
        {
            BeforePublication(providerName);

            _ = Interlocked.Increment(ref _failedPublications);

            Volatile.Write(ref _healthy, 0);

            RecordPublishedProvider(providerName);
        }

        public void MarkHealthy(string providerName)
        {
            BeforePublication(providerName);

            _ = Interlocked.Increment(ref _healthyPublications);

            Volatile.Write(ref _healthy, 1);

            RecordPublishedProvider(providerName);
        }

        public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() =>
            [new ProviderHealthStatus(
                ProviderName,
                IsHealthy(ProviderName),
                DateTimeOffset.UtcNow,
                FailedPublications)];

        internal void ReleasePublication() => _allowPublication.TrySetResult();

        private void BeforePublication(string providerName)
        {
            beforePublication?.Invoke(providerName);

            if (!blockPublication)
            {
                return;
            }

            _publicationReached.TrySetResult();

            _allowPublication.Task.GetAwaiter().GetResult();
        }

        private void RecordPublishedProvider(string providerName)
        {
            lock (_publishedProvidersGate)
            {
                _publishedProviders.Add(providerName);
            }
        }
    }

    private sealed class AsyncCheckpoint
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Reached => _reached.Task;

        internal async Task PauseAsync()
        {
            _reached.TrySetResult();

            await _release.Task;
        }

        internal void Release() => _release.TrySetResult();
    }

}
