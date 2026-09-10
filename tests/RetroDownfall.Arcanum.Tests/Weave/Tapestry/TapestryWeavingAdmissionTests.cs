using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using System.Threading.Channels;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed class TapestryWeavingAdmissionTests
{
    [Fact]
    public async Task KeepClosedAndImmediateRecloseReturnToCadenceWithoutWaiters()
    {
        CadenceClock clock = new();

        await using TapestryAdmissionHarness harness = new(clock);

        harness.Configuration.Features.Tapestry = true;

        harness.Configuration.Integrations.Embeddings.Dimensions = 64;

        IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            CadenceClock.Tick first = await clock.NextAsync();

            Assert.Equal(TimeSpan.FromHours(1), first.DueTime);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

            first.Fire();

            CadenceClock.Tick second = await clock.NextAsync();

            Assert.Equal(0, harness.Scopes.Created);

            Assert.Equal(2, harness.Gate.RequestedWorkKinds.Count);

            await closed.DisposeAsync();

            IGrimoireClosingOwner resumed = harness.Inner.BeginOrResumeExclusive(closing.Owner).Value;

            IGrimoireExclusiveClosedLease reopened = (await harness.Inner.CloseConnectionAdmissionAsync(resumed, CancellationToken.None)).Value;

            Assert.True((await reopened.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await reopened.DisposeAsync();

            IGrimoireClosingOwner immediate = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            second.Fire();

            CadenceClock.Tick third = await clock.NextAsync();

            Assert.Equal(0, harness.Scopes.Created);

            Assert.True((await harness.Inner.DrainRequestAndWorkAsync(immediate, CancellationToken.None)).IsSuccess);

            IGrimoireExclusiveClosedLease finalClosed = (await harness.Inner.CloseConnectionAdmissionAsync(immediate, CancellationToken.None)).Value;

            Assert.True((await finalClosed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await finalClosed.DisposeAsync();

            Assert.Equal(3, harness.Gate.RequestedWorkKinds.Count);

            third.Fire();

            _ = await clock.NextAsync();

            Assert.Equal(1, harness.Scopes.Created);

            Assert.Equal(1, harness.Persistence.Begins);

            Assert.Equal(1, harness.Gate.EffectGroupAttempts);

            Assert.Equal(TapestryGenerationStatus.Complete, Assert.Single(harness.Persistence.Generations.Values).Status);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task HostedShutdownJoinsAbandonmentAndEveryAsyncDisposal()
    {
        await using TapestryAdmissionHarness harness = new();

        harness.Configuration.Features.Tapestry = true;

        harness.Configuration.Integrations.Embeddings.Dimensions = 64;

        TapestryAdmissionHarness.Checkpoint provider = new();

        string[] phases = ["abandon", "group-dispose", "scope-dispose", "lease-dispose"];

        Dictionary<string, TapestryAdmissionHarness.Checkpoint> checkpoints = phases.ToDictionary(static phase => phase, static _ => new TapestryAdmissionHarness.Checkpoint());

        harness.OnStep = (step, token) => step == "summarize"
            ? provider.PauseAsync(token)
            : checkpoints.TryGetValue(step, out TapestryAdmissionHarness.Checkpoint? checkpoint)
                ? checkpoint.PauseAsync()
                : Task.CompletedTask;

        await harness.Service.StartAsync(CancellationToken.None);

        Task? stopping = null;

        try
        {
            await provider.WaitAsync();

            stopping = harness.Service.StopAsync(CancellationToken.None);

            foreach (string phase in phases)
            {
                await checkpoints[phase].WaitAsync();

                Assert.False(stopping.IsCompleted, "Shutdown completed before " + phase);

                checkpoints[phase].Release.TrySetResult();
            }

            await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(phases, harness.Events.Where(phases.Contains));

            Assert.Empty(harness.Persistence.Generations);

            Assert.Equal(GrimoireWorkKind.TapestryWeaving, Assert.Single(harness.Gate.RequestedWorkKinds));
        }
        finally
        {
            provider.Release.TrySetResult();

            foreach (TapestryAdmissionHarness.Checkpoint checkpoint in checkpoints.Values)
            {
                checkpoint.Release.TrySetResult();
            }

            await (stopping ?? harness.Service.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task HostCancellationAfterPublicationDoesNotEraseTheCommittedGeneration()
    {
        await using TapestryAdmissionHarness harness = new();

        using CancellationTokenSource cancellation = new();

        harness.OnStep = (step, _) =>
        {
            if (step == "published")
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SweepAsync(cancellation.Token));

        Assert.Equal(TapestryGenerationStatus.Complete, Assert.Single(harness.Persistence.Generations.Values).Status);

        Assert.Equal(["published", "abandon", "group-dispose", "scope-dispose", "lease-dispose"], harness.Events.Where(static step => step is "published" or "abandon" || step.EndsWith("-dispose", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartupCleanupIsRetriedBeforeDiscoveringAnyScope(bool cancel)
    {
        await using TapestryAdmissionHarness harness = new();

        using CancellationTokenSource cancellation = new();

        harness.OnStep = (step, _) =>
        {
            if (step == "cleanup" && harness.Persistence.Cleanups == 1)
            {
                if (cancel)
                {
                    cancellation.Cancel();

                    return Task.FromException(new OperationCanceledException(cancellation.Token));
                }

                return Task.FromException(new IOException("Cleanup unavailable."));
            }

            return Task.CompletedTask;
        };

        Exception? failure = await Record.ExceptionAsync(() => harness.SweepAsync(cancellation.Token));

        Assert.IsType(cancel ? typeof(OperationCanceledException) : typeof(IOException), failure);

        Assert.DoesNotContain("discover", harness.Events);

        harness.Events.Clear();

        _ = await harness.SweepAsync();

        Assert.Equal(["scope-create", "cleanup", "discover"], harness.Events.Take(3));

        Assert.Equal(3, harness.Persistence.Cleanups);
    }

    [Theory]
    [InlineData("leaves:/first", false)]
    [InlineData("begin:/first", false)]
    [InlineData("summarize", false)]
    [InlineData("leaves:/first", true)]
    [InlineData("begin:/first", true)]
    [InlineData("summarize", true)]
    public async Task PerScopeFailuresDoNotPreventLaterScopes(string stage, bool nonHostCancellation)
    {
        await using TapestryAdmissionHarness harness = new();

        harness.Persistence.Scopes = [TapestryAdmissionHarness.First, TapestryAdmissionHarness.Second];

        bool failed = false;

        harness.OnStep = (step, _) =>
        {
            if (step == stage && !failed)
            {
                failed = true;

                return Task.FromException(nonHostCancellation
                    ? new OperationCanceledException("Provider deadline.")
                    : new IOException("Scope unavailable."));
            }

            return Task.CompletedTask;
        };

        IReadOnlyList<TapestryWeaveOutcome> outcomes = await harness.SweepAsync();

        Assert.Equal([TapestryWeaveStatus.Failed, TapestryWeaveStatus.Woven], outcomes.Select(static outcome => outcome.Status));

        Assert.Equal("/second", Assert.Single(harness.Persistence.Generations.Values).ScopeId);

        Assert.Equal(2, harness.Gate.EffectGroupAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCancellationSurvivesFailedAbandonment(bool cleanupCancellation)
    {
        await using TapestryAdmissionHarness harness = new();

        using CancellationTokenSource cancellation = new();

        harness.Persistence.Scopes = [TapestryAdmissionHarness.First, TapestryAdmissionHarness.Second];

        OperationCanceledException primary = new(cancellation.Token);

        harness.OnStep = (step, _) =>
        {
            if (step == "summarize")
            {
                cancellation.Cancel();

                return Task.FromException(primary);
            }

            return step == "abandon"
                ? Task.FromException(cleanupCancellation ? new OperationCanceledException("Cleanup cancelled.") : new IOException("Cleanup failed."))
                : Task.CompletedTask;
        };

        Exception? actual = await Record.ExceptionAsync(() => harness.SweepAsync(cancellation.Token));

        Assert.Same(primary, actual);

        Assert.Equal(1, harness.Events.Count(static step => step == "abandon"));

        Assert.DoesNotContain("begin:/second", harness.Events);

        Assert.Equal(["group-dispose", "scope-dispose", "lease-dispose"], harness.Events.Where(static step => step.EndsWith("-dispose", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RevocationBeforeGenerationCreatesNoStagedGeneration()
    {
        await using TapestryAdmissionHarness harness = new();

        harness.OnStep = (step, _) =>
        {
            if (step == "discover")
            {
                Assert.True(harness.Inner.BeginOrResumeExclusive(Owner()).IsSuccess);
            }

            return Task.CompletedTask;
        };

        TapestrySweepOutcome outcome = await harness.Service.RunSweepAsync(TapestryAdmissionHarness.Settings(), CancellationToken.None);

        Assert.Equal(TapestrySweepStatus.DeferredForMaintenance, outcome.Status);

        Assert.Empty(outcome.Outcomes);

        Assert.Equal(0, harness.Persistence.Begins);

        Assert.Equal(1, harness.Gate.EffectGroupAttempts);

        Assert.DoesNotContain("embed-leaves", harness.Events);

        Assert.DoesNotContain("abandon", harness.Events);

        Assert.DoesNotContain("prune", harness.Events);

        Assert.Equal(1, harness.Persistence.Cleanups);
    }

    [Fact]
    public async Task DeferralKeepsPreviousGenerationCurrentAndRediscoversAfterReopen()
    {
        await using TapestryAdmissionHarness harness = new();

        _ = await harness.SweepAsync();

        TapestryGeneration prior = Assert.Single(harness.Persistence.Generations.Values);

        harness.Persistence.Corpus = "changed";

        IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.Empty(await harness.SweepAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(prior, Assert.Single(harness.Persistence.Generations.Values));

        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.Empty(await harness.SweepAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, harness.Scopes.Created);

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        await closed.DisposeAsync();

        IReadOnlyList<TapestryWeaveOutcome> resumed = await harness.SweepAsync();

        Assert.Equal(TapestryWeaveStatus.Woven, Assert.Single(resumed).Status);

        Assert.NotEqual(prior.GenerationId, Assert.Single(harness.Persistence.Generations.Values).GenerationId);

        Assert.Equal(2, harness.Gate.EffectGroupAttempts);
    }

    [Theory]
    [InlineData("begin:/first")]
    [InlineData("embed-leaves")]
    [InlineData("append-leaves")]
    [InlineData("summarize")]
    [InlineData("embed-summary")]
    [InlineData("append-summary")]
    [InlineData("parents")]
    [InlineData("publish")]
    public async Task OneWinningGroupCoversTheWholeGenerationAndRefusesTheNextScope(string frontier)
    {
        await using TapestryAdmissionHarness harness = new();

        harness.Persistence.Scopes = [TapestryAdmissionHarness.First, TapestryAdmissionHarness.Second];

        TapestryAdmissionHarness.Checkpoint checkpoint = new();

        harness.OnStep = async (step, token) =>
        {
            if (step == frontier)
            {
                await checkpoint.PauseAsync(token);
            }

            token.ThrowIfCancellationRequested();
        };

        Task<IReadOnlyList<TapestryWeaveOutcome>> sweep = harness.SweepAsync();

        try
        {
            await checkpoint.WaitAsync();

            Assert.Equal(1, harness.Gate.EffectGroupAttempts);

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            Task<Result> drain = harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            checkpoint.Release.TrySetResult();

            Assert.Equal(TapestryWeaveStatus.Woven, Assert.Single(await sweep).Status);

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal(1, harness.Persistence.Begins);

            Assert.Equal(2, harness.Gate.EffectGroupAttempts);

            Assert.Equal(TapestryGenerationStatus.Complete, Assert.Single(harness.Persistence.Generations.Values).Status);

            string[] events = harness.Events.ToArray();

            Assert.True(Array.IndexOf(events, "published") < Array.IndexOf(events, "group-dispose"));

            Assert.DoesNotContain("begin:/second", events);

            Assert.DoesNotContain("prune", events);
        }
        finally
        {
            checkpoint.Release.TrySetResult();

            await sweep;
        }
    }

    [Fact]
    public async Task WinningGenerationDrainsThroughPublicationGroupScopeAndLeaseDisposal()
    {
        await using TapestryAdmissionHarness harness = new();

        string[] phases = ["publish", "group-dispose", "scope-dispose", "lease-dispose"];

        Dictionary<string, TapestryAdmissionHarness.Checkpoint> checkpoints = phases.ToDictionary(static phase => phase, static _ => new TapestryAdmissionHarness.Checkpoint());

        harness.OnStep = (step, token) => checkpoints.TryGetValue(step, out TapestryAdmissionHarness.Checkpoint? checkpoint)
            ? checkpoint.PauseAsync(token)
            : Task.CompletedTask;

        Task<IReadOnlyList<TapestryWeaveOutcome>> sweep = harness.SweepAsync();

        try
        {
            await checkpoints["publish"].WaitAsync();

            Assert.Equal(1, harness.Gate.EffectGroupAttempts);

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            Task<Result> drain = harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            foreach (string phase in phases)
            {
                await checkpoints[phase].WaitAsync();

                Assert.False(drain.IsCompleted, "Drain completed before " + phase);

                checkpoints[phase].Release.TrySetResult();
            }

            Assert.Equal(TapestryWeaveStatus.Woven, Assert.Single(await sweep).Status);

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal(phases, harness.Events.Where(phases.Contains));

            string[] events = harness.Events.ToArray();

            Assert.True(Array.IndexOf(events, "group-dispose") < Array.LastIndexOf(events, "cleanup"));

            Assert.True(Array.IndexOf(events, "prune") < Array.IndexOf(events, "scope-dispose"));
        }
        finally
        {
            foreach (TapestryAdmissionHarness.Checkpoint checkpoint in checkpoints.Values)
            {
                checkpoint.Release.TrySetResult();
            }

            await sweep;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledGenerationAwaitsAbandonmentBeforeReleasingGroup(bool cancel)
    {
        await using TapestryAdmissionHarness harness = new();

        using CancellationTokenSource cancellation = new();

        TapestryAdmissionHarness.Checkpoint abandonment = new();

        harness.OnStep = async (step, token) =>
        {
            if (step == "summarize")
            {
                if (cancel)
                {
                    cancellation.Cancel();

                    token.ThrowIfCancellationRequested();
                }

                throw new InvalidOperationException("Provider unavailable.");
            }

            if (step == "abandon")
            {
                Assert.False(token.CanBeCanceled);

                await abandonment.PauseAsync();
            }
        };

        Task<IReadOnlyList<TapestryWeaveOutcome>> sweep = harness.SweepAsync(cancellation.Token);

        try
        {
            await abandonment.WaitAsync();

            Assert.Equal(1, harness.Gate.EffectGroupAttempts);

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            Task<Result> drain = harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            Assert.DoesNotContain("group-dispose", harness.Events);

            abandonment.Release.TrySetResult();

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
            }
            else
            {
                Assert.Equal(TapestryWeaveStatus.Failed, Assert.Single(await sweep).Status);
            }

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Empty(harness.Persistence.Generations);
        }
        finally
        {
            abandonment.Release.TrySetResult();

            _ = await Record.ExceptionAsync(() => sweep);
        }
    }

    [Fact]
    public async Task ClosedGateCreatesNoTapestrySweepScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        Assert.True(inner.BeginOrResumeExclusive(Owner()).IsSuccess);

        RefusingScopeFactory scopes = new();

        ServiceCollection services = new();

        services.AddSingleton<IServiceScopeFactory>(scopes);

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(gate);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<TapestryWeavingService>>(
            NullLogger<TapestryWeavingService>.Instance);

        await using ServiceProvider provider = services.BuildServiceProvider();

        TapestryWeavingService service = ActivatorUtilities.CreateInstance<TapestryWeavingService>(
            provider,
            scopes);

        TapestrySweepOutcome outcome = await service.RunSweepAsync(new EmbeddingSettings(), CancellationToken.None);

        Assert.Equal(0, scopes.Created);

        Assert.Equal(TapestrySweepStatus.DeferredForMaintenance, outcome.Status);

        Assert.Empty(outcome.Outcomes);

        Assert.Equal([GrimoireWorkKind.TapestryWeaving], gate.RequestedWorkKinds);
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));

    private sealed class RefusingScopeFactory : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            throw new InvalidOperationException("Admission must precede scope creation.");
        }
    }

    private sealed class CadenceClock : TimeProvider
    {
        private readonly Channel<Tick> _ticks = Channel.CreateUnbounded<Tick>();

        internal async Task<Tick> NextAsync() =>
            await _ticks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Tick tick = new(callback, state, dueTime);

            _ticks.Writer.TryWrite(tick);

            return tick;
        }

        internal sealed class Tick(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private int _disposed;

            internal TimeSpan DueTime { get; } = dueTime;

            internal void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }
}
