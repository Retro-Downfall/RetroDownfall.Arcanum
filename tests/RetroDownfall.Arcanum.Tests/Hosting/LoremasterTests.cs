using System.Collections.Concurrent;

using System.Data.Common;

using System.Runtime.CompilerServices;

using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection(HostedServiceLifetimeCollection.Name)]
public sealed class LoremasterTests
{
    [Fact]
    public async Task WinningSweepDrainsThroughScopeAndLeaseDisposal()
    {
        LoremasterHarness harness = new();

        string[] phases = ["sweep", "scope-dispose", "lease-dispose"];

        Dictionary<string, Checkpoint> checkpoints = phases.ToDictionary(
            static phase => phase,
            static _ => new Checkpoint());

        harness.OnStepAsync = (step, token) => checkpoints.TryGetValue(step, out Checkpoint? checkpoint)
            ? checkpoint.PauseAsync(token)
            : Task.CompletedTask;

        harness.Gate.Recording.BeforeWorkLeaseDisposalAsync = () =>
            new ValueTask(harness.StepAsync("lease-dispose", CancellationToken.None));

        Task sweep = harness.Service.RunSweepAsync(1, CancellationToken.None);

        await checkpoints["sweep"].Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await using IGrimoireClosingOwner closing = harness.Gate.Inner
            .BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = harness.Gate.Inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        try
        {
            foreach (string phase in phases)
            {
                await checkpoints[phase].Reached.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.False(drain.IsCompleted, "Maintenance drain completed before " + phase + ".");

                checkpoints[phase].Release();
            }

            await sweep.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal([GrimoireWorkKind.LoremasterSummarization], harness.Gate.Recording.RequestedWorkKinds);

            Assert.Equal(phases, harness.Events.Where(phases.Contains));
        }
        finally
        {
            foreach (Checkpoint checkpoint in checkpoints.Values)
            {
                checkpoint.Release();
            }

            _ = await Record.ExceptionAsync(() => sweep);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalSkipConcludesIdentityWithoutEffectGroup(bool sessionMissing)
    {
        LoremasterHarness harness = new();

        harness.Repository.ReturnSession = !sessionMissing;

        if (!sessionMissing)
        {
            harness.Repository.Entries.Clear();
        }

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.NextStepAsync(sessionMissing ? "header" : "entries");

        await harness.NextStepAsync("scope-dispose");

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, harness.Queue.PendingCountForTesting);

        Assert.Equal(0, harness.Intelligence.Calls);

        Assert.Equal(0, harness.Repository.Rollups);

        Assert.Equal(0, harness.Gate.Recording.EffectGroupAttempts);
    }

    [Fact]
    public async Task ForeignProviderCancellationIsGenuineFailure()
    {
        LoremasterHarness harness = new();

        harness.Intelligence.NextException = new OperationCanceledException("Provider deadline.");

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.NextStepAsync("provider");

        await harness.NextStepAsync("scope-dispose");

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, harness.Intelligence.Calls);

        Assert.Equal(0, harness.Repository.Rollups);

        Assert.Equal(0, harness.Queue.PendingCountForTesting);

        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Level == LogLevel.Warning
                && message.Exception is OperationCanceledException);
    }

    [Fact]
    public async Task GenuineRollupFailureConcludesIdentityWithoutAdvancingWatermark()
    {
        LoremasterHarness harness = new();

        harness.Repository.RollupException = new IOException("Grimoire unavailable.");

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.NextStepAsync("rollup");

        await harness.NextStepAsync("scope-dispose");

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, harness.Intelligence.Calls);

        Assert.Equal(0, harness.Repository.Rollups);

        Assert.Null(harness.Repository.Session.LastSummarizedMessageAt);

        Assert.Null(harness.Repository.Session.Summary);

        Assert.Equal(0, harness.Queue.PendingCountForTesting);

        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Level == LogLevel.Warning
                && message.Exception is IOException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineProviderFailureConcludesIdentityWithoutRollup(bool throws)
    {
        LoremasterHarness harness = new();

        if (throws)
        {
            harness.Intelligence.NextException = new IOException("Provider unavailable.");
        }
        else
        {
            harness.Intelligence.NextResult = Result<PromptTurnResult>.Failure(
                new Error("Provider.Unavailable", "Provider unavailable."));
        }

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.NextStepAsync("provider");

        await harness.NextStepAsync("scope-dispose");

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, harness.Intelligence.Calls);

        Assert.Equal(0, harness.Repository.Rollups);

        Assert.Equal(0, harness.Queue.PendingCountForTesting);

        Assert.Equal(1, harness.Gate.Recording.EffectGroupAttempts);

        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HostCancellationIsNotFailure()
    {
        LoremasterHarness harness = new();

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        Checkpoint provider = new();

        harness.Gate.Recording.BeforeEffectGroupDisposalAsync = () =>
            new ValueTask(harness.StepAsync("group-dispose", CancellationToken.None));

        harness.Gate.Recording.BeforeWorkLeaseDisposalAsync = () =>
            new ValueTask(harness.StepAsync("lease-dispose", CancellationToken.None));

        harness.OnStepAsync = (step, token) => step == "provider"
            ? provider.PauseAsync(token)
            : Task.CompletedTask;

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await provider.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, harness.Intelligence.Calls);

        Assert.Equal(0, harness.Repository.Rollups);

        Assert.Equal(1, harness.Queue.PendingCountForTesting);

        Assert.DoesNotContain(
            harness.Logger.Messages,
            static message => message.Level >= LogLevel.Warning);

        Assert.Contains("group-dispose", harness.Events);

        Assert.Contains("scope-dispose", harness.Events);

        Assert.Contains("lease-dispose", harness.Events);

        IGrimoireClosingOwner closing = harness.Gate.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await harness.Gate.Inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        await closing.DisposeAsync();
    }

    [Fact]
    public async Task ImmediateRecloseRetainsIdentityUntilAStableReopen()
    {
        LoremasterHarness harness = new();

        IGrimoireClosingOwner initialClosing = harness.Gate.Inner.BeginOrResumeExclusive(Owner()).Value;

        IGrimoireClosingOwner? immediateClosing = null;

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            _ = await harness.Gate.NextWaitAsync();

            TaskCompletionSource<IGrimoireClosingOwner> reclosed =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            harness.Gate.Recording.AfterOpenGenerationObserved = _ =>
            {
                Result<IGrimoireClosingOwner> result = harness.Gate.Inner.BeginOrResumeExclusive(Owner());

                if (result.IsSuccess)
                {
                    reclosed.TrySetResult(result.Value);
                }
                else
                {
                    reclosed.TrySetException(new InvalidOperationException(result.Error.Message));
                }
            };

            await ReopenAsync(harness.Gate.Inner, initialClosing);

            immediateClosing = await reclosed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            _ = await harness.Gate.NextWaitAsync();

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.Equal(0, harness.Intelligence.Calls);

            harness.Gate.Recording.AfterOpenGenerationObserved = static _ => { };

            await ReopenAsync(harness.Gate.Inner, immediateClosing);

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            await harness.NextStepAsync("rollup");

            await harness.NextStepAsync("scope-dispose");

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Intelligence.Calls);

            Assert.Equal(1, harness.Repository.Rollups);

            Assert.Equal(0, harness.Queue.PendingCountForTesting);

            Assert.Equal(1, harness.Gate.Recording.EffectGroupAttempts);
        }
        finally
        {
            harness.Gate.Recording.AfterOpenGenerationObserved = static _ => { };

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            if (immediateClosing is not null)
            {
                await immediateClosing.DisposeAsync();
            }

            await initialClosing.DisposeAsync();
        }
    }

    [Fact]
    public async Task KeepClosedRetainsOneWaiterAndOneSession()
    {
        LoremasterHarness harness = new();

        CovenantExclusiveRecoveryOwner owner = Owner();

        IGrimoireClosingOwner closing = harness.Gate.Inner.BeginOrResumeExclusive(owner).Value;

        Assert.True((await harness.Gate.Inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await harness.Gate.Inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        IGrimoireClosingOwner? resumed = null;

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            _ = await harness.Gate.NextWaitAsync();

            Assert.True((await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.KeepClosed,
                CancellationToken.None)).IsSuccess);

            Assert.Equal(1, harness.Gate.Recording.ActiveGenerationWaiters);

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.Equal(0, harness.Intelligence.Calls);

            await closed.DisposeAsync();

            resumed = harness.Gate.Inner.BeginOrResumeExclusive(owner).Value;

            await ReopenAlreadyClosedAsync(harness.Gate.Inner, resumed);

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            await harness.NextStepAsync("rollup");

            await harness.NextStepAsync("scope-dispose");

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Intelligence.Calls);

            Assert.Equal(1, harness.Repository.Rollups);

            Assert.Equal(0, harness.Queue.PendingCountForTesting);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            await closed.DisposeAsync();

            if (resumed is not null)
            {
                await resumed.DisposeAsync();
            }

            await closing.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReopenDirectlyResignalsHeldSessionOnce()
    {
        LoremasterHarness harness = new();

        IGrimoireClosingOwner closing = harness.Gate.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            _ = await harness.Gate.NextWaitAsync();

            Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            await ReopenAsync(harness.Gate.Inner, closing);

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            await harness.NextStepAsync("rollup");

            await harness.NextStepAsync("scope-dispose");

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Intelligence.Calls);

            Assert.Equal(1, harness.Repository.Rollups);

            Assert.Equal(0, harness.Queue.PendingCountForTesting);

            Assert.Equal(1, harness.Gate.Recording.EffectGroupAttempts);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            await closing.DisposeAsync();
        }
    }

    [Fact]
    public async Task WinningSummaryDrainsThroughRollup()
    {
        LoremasterHarness harness = new();

        harness.Gate.Recording.BeforeWorkLeaseDisposalAsync = () =>
            new ValueTask(harness.StepAsync("lease-dispose", CancellationToken.None));

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.NextStepAsync("sweep");

        await harness.NextStepAsync("scope-dispose");

        await harness.NextStepAsync("lease-dispose");

        harness.Events.Clear();

        string[] phases = ["provider", "rollup", "group-dispose", "scope-dispose", "lease-dispose"];

        Dictionary<string, Checkpoint> checkpoints = phases.ToDictionary(
            static phase => phase,
            static _ => new Checkpoint());

        harness.OnStepAsync = (step, token) => checkpoints.TryGetValue(step, out Checkpoint? checkpoint)
            ? checkpoint.PauseAsync(token)
            : Task.CompletedTask;

        harness.Gate.Recording.BeforeEffectGroupDisposalAsync = () =>
            new ValueTask(harness.StepAsync("group-dispose", CancellationToken.None));

        harness.Gate.Recording.BeforeWorkLeaseDisposalAsync = () =>
            new ValueTask(harness.StepAsync("lease-dispose", CancellationToken.None));

        Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

        await checkpoints["provider"].Reached;

        await using IGrimoireClosingOwner closing = harness.Gate.Inner
            .BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = harness.Gate.Inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        try
        {
            foreach (string phase in phases)
            {
                await checkpoints[phase].Reached.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.False(drain.IsCompleted, "Maintenance drain completed before " + phase + ".");

                checkpoints[phase].Release();
            }

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            IGrimoireExclusiveClosedLease closed = (await harness.Gate.Inner
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

            Assert.True((await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Intelligence.Calls);

            Assert.Equal(1, harness.Repository.Rollups);

            Assert.Equal(harness.Repository.Session.Id, harness.Repository.LastRollupSessionId);

            Assert.Equal("summary", harness.Repository.Session.Summary);

            Assert.Equal(0, harness.Queue.PendingCountForTesting);

            Assert.Equal(1, harness.Gate.Recording.EffectGroupAttempts);

            Assert.Equal(phases, harness.Events.Where(phases.Contains));
        }
        finally
        {
            foreach (Checkpoint checkpoint in checkpoints.Values)
            {
                checkpoint.Release();
            }

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DeniedEffectGroupRetainsExactIdentityWithoutProviderOrRollup()
    {
        LoremasterHarness harness = new();

        await harness.Service.StartAsync(CancellationToken.None);

        IGrimoireClosingOwner? closing = null;

        TaskCompletionSource groupAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await harness.NextStepAsync("sweep");

            harness.Gate.Recording.BeforeEffectGroupAdmission = () =>
            {
                closing = harness.Gate.Inner.BeginOrResumeExclusive(Owner()).Value;

                groupAttempt.TrySetResult();
            };

            Assert.True(harness.Queue.TryQueue(harness.Repository.Session.Id));

            Task provider = harness.NextStepAsync("provider");

            Task winner = await Task.WhenAny(groupAttempt.Task, provider)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(groupAttempt.Task, winner);

            _ = await harness.Gate.NextWaitAsync();

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            Assert.Equal(0, harness.Intelligence.Calls);

            Assert.Equal(0, harness.Repository.Rollups);

            Assert.Equal(1, harness.Gate.Recording.EffectGroupAttempts);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null)
            {
                await closing.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DeniedSessionRetainsExactIdentity()
    {
        LoremasterHarness harness = new();

        Guid sessionId = harness.Repository.Session.Id;

        await using IGrimoireClosingOwner closing = harness.Gate.Inner
            .BeginOrResumeExclusive(Owner()).Value;

        Assert.True(harness.Queue.TryQueue(sessionId));

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await harness.Gate.NextAcquireAsync());

            Task<GrimoireWorkKind> consumeAdmission = harness.Gate.NextAcquireAsync();

            Task firstScope = harness.Scopes.FirstCreated;

            Task winner = await Task.WhenAny(consumeAdmission, firstScope)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(consumeAdmission, winner);

            Assert.Equal(GrimoireWorkKind.LoremasterSummarization, await consumeAdmission);

            _ = await harness.Gate.NextWaitAsync();

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            Assert.True(harness.Queue.TryQueue(sessionId));

            Assert.Equal(1, harness.Queue.PendingCountForTesting);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.Equal(0, harness.Repository.HeaderReads);

            Assert.Equal(0, harness.Intelligence.Calls);

            Assert.Equal(0, harness.Repository.Rollups);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, harness.Gate.Recording.ActiveGenerationWaiters);
    }

    [Fact]
    public async Task DeniedSweepCreatesNoScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        RefusingScopeFactory scopes = new();

        CampaignLoggerQueue queue = new(NullLogger<CampaignLoggerQueue>.Instance);

        Loremaster service = new(
            scopes,
            queue,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            gate,
            NullLogger<Loremaster>.Instance);

        await service.RunSweepAsync(1, CancellationToken.None);

        Assert.Equal(0, scopes.Created);

        Assert.Equal([GrimoireWorkKind.LoremasterSummarization], gate.RequestedWorkKinds);
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));

    private static async Task ReopenAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await closed.DisposeAsync();
    }

    private static async Task ReopenAlreadyClosedAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        IGrimoireExclusiveClosedLease closed = (await gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await closed.DisposeAsync();
    }

    private sealed class LoremasterHarness
    {
        private readonly ConcurrentDictionary<string, Channel<bool>> _steps = new(StringComparer.Ordinal);

        internal LoremasterHarness()
        {
            Gate = new LoremasterAdmissionGate();

            Queue = new CampaignLoggerQueue(NullLogger<CampaignLoggerQueue>.Instance);

            Repository = new LoremasterRepository(this);

            Intelligence = new LoremasterIntelligence(this);

            Scopes = new LoremasterScopeFactory(this);

            Logger = new RecordingLogger<Loremaster>();

            Service = new Loremaster(
                Scopes,
                Queue,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                Gate,
                Logger);
        }

        internal LoremasterAdmissionGate Gate { get; }

        internal CampaignLoggerQueue Queue { get; }

        internal LoremasterRepository Repository { get; }

        internal LoremasterIntelligence Intelligence { get; }

        internal LoremasterScopeFactory Scopes { get; }

        internal RecordingLogger<Loremaster> Logger { get; }

        internal Loremaster Service { get; }

        internal ConcurrentQueue<string> Events { get; } = new();

        internal Func<string, CancellationToken, Task> OnStepAsync { get; set; } =
            static (_, _) => Task.CompletedTask;

        internal async Task StepAsync(string step, CancellationToken cancellationToken)
        {
            Events.Enqueue(step);

            StepChannel(step).Writer.TryWrite(true);

            await OnStepAsync(step, cancellationToken);
        }

        internal Task NextStepAsync(string step) =>
            StepChannel(step).Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        private Channel<bool> StepChannel(string step) =>
            _steps.GetOrAdd(step, static _ => Channel.CreateUnbounded<bool>());
    }

    private sealed class LoremasterScopeFactory(LoremasterHarness harness) : IServiceScopeFactory
    {
        private readonly TaskCompletionSource _firstCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Created { get; private set; }

        internal Task FirstCreated => _firstCreated.Task;

        public IServiceScope CreateScope()
        {
            Created++;

            harness.Events.Enqueue("scope-create");

            _firstCreated.TrySetResult();

            return new Scope(harness);
        }

        private sealed class Scope(LoremasterHarness harness) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = new Services(harness);

            public void Dispose() => harness.Events.Enqueue("scope-dispose-sync");

            public async ValueTask DisposeAsync()
            {
                await harness.StepAsync("scope-dispose", CancellationToken.None);
            }
        }

        private sealed class Services(LoremasterHarness harness) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IGrimoireRepository))
                {
                    return harness.Repository;
                }

                if (serviceType == typeof(IArcanumIntelligenceProvider))
                {
                    return harness.Intelligence;
                }

                if (serviceType == typeof(IAttachmentMemoryProvenanceStore))
                {
                    return harness.Repository;
                }

                return null;
            }
        }
    }

    private sealed class LoremasterRepository(LoremasterHarness harness) :
        IGrimoireRepository,
        IAttachmentMemoryProvenanceStore
    {
        internal Session Session { get; } = new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UnsummarizedEntryCount = 1,
        };

        internal List<Entry> Entries { get; } =
        [
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "Retain the exact queued identity.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        ];

        internal int HeaderReads { get; private set; }

        internal int Rollups { get; private set; }

        internal Guid? LastRollupSessionId { get; private set; }

        internal bool ReturnSession { get; set; } = true;

        internal Exception? RollupException { get; set; }

        public async Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold,
            DateTime idleCutoff,
            CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("sweep", cancellationToken);

            return [];
        }

        public async Task<Session?> GetSessionHeaderAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            HeaderReads++;

            await harness.StepAsync("header", cancellationToken);

            return ReturnSession && id == Session.Id ? Session.CloneHeader() : null;
        }

        public async Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId,
            DateTime watermark,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("entries", cancellationToken);

            return Entries.Take(batchSize).ToList();
        }

        public async Task UpdateSessionCampaignRollupAsync(
            Guid sessionId,
            string summary,
            DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("rollup", cancellationToken);

            if (RollupException is not null)
            {
                throw RollupException;
            }

            Rollups++;

            LastRollupSessionId = sessionId;

            Session.Summary = summary;

            Session.LastSummarizedMessageAt = lastSummarizedMessageAt;
        }

        public Task<IReadOnlyList<AttachmentMemoryProvenance>> ListConsultationsAsync(
            Guid sessionId,
            DateTimeOffset afterExclusive,
            DateTimeOffset throughInclusive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttachmentMemoryProvenance>>([]);

        public Task RecordConsultationsAsync(
            Guid sourceEntryId,
            IReadOnlyList<AttachmentMemoryProvenance> provenance,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId,
            string fullContent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt,
            string assistantText,
            string modelUsed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PurgeSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Session?> GetSessionAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId,
            int takeLast,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteEntryAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SetEntryPinnedAsync(
            Guid sessionId,
            Guid entryId,
            bool pinned,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> GetPinnedEntryCountAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SessionExistsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId,
            long totalTokens,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId,
            long totalTokens,
            decimal costUsd,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<decimal> GetTodaySpendAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AdvanceCampaignLogWatermarkAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> ReadLoreAsync(
            string key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LoreDto> ScribeLoreAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteLoreAsync(
            string key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LoreDto?> GetLoreAsync(
            string key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> SearchArchivesAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordWorkspaceContextAsync(
            WorkspaceContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LoremasterIntelligence(LoremasterHarness harness) : IArcanumIntelligenceProvider
    {
        internal int Calls { get; private set; }

        internal Result<PromptTurnResult> NextResult { get; set; } =
            Result<PromptTurnResult>.Success(new PromptTurnResult("summary", null));

        internal Exception? NextException { get; set; }

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            Calls++;

            await harness.StepAsync("provider", cancellationToken);

            if (NextException is not null)
            {
                throw NextException;
            }

            return NextResult;
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            await Task.CompletedTask;

            yield break;
        }
    }

    private sealed class LoremasterAdmissionGate : IGrimoireConnectionAdmissionGate
    {
        private readonly Channel<GrimoireWorkKind> _acquisitions = Channel.CreateUnbounded<GrimoireWorkKind>();

        private readonly Channel<long> _waits = Channel.CreateUnbounded<long>();

        internal LoremasterAdmissionGate()
        {
            Inner = new GrimoireConnectionAdmissionGate(TimeProvider.System);

            Recording = new RecordingGrimoireWorkAdmissionGate(Inner);
        }

        internal GrimoireConnectionAdmissionGate Inner { get; }

        internal RecordingGrimoireWorkAdmissionGate Recording { get; }

        public long CurrentGeneration => Recording.CurrentGeneration;

        internal Task<GrimoireWorkKind> NextAcquireAsync() =>
            _acquisitions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        internal Task<long> NextWaitAsync() =>
            _waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public bool TryAcquireRequestLease(
            GrimoireRequestKind kind,
            out IGrimoireRequestLease? lease) => Recording.TryAcquireRequestLease(kind, out lease);

        public bool TryAcquireWorkLease(
            GrimoireWorkKind kind,
            out IGrimoireWorkLease? lease)
        {
            _acquisitions.Writer.TryWrite(kind);

            return Recording.TryAcquireWorkLease(kind, out lease);
        }

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) =>
            Recording.AcquireOrdinaryOpen(connection);

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
            CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null,
            DbConnection? scopedConnection = null) =>
            Recording.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

        public ValueTask<Result> DrainRequestAndWorkAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            Recording.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            Recording.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

        public ValueTask<Result> AbortClosingAsync(
            IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
            CancellationToken cancellationToken) =>
            Recording.AbortClosingAsync(closingOwner, proveNoDestructiveEffectAsync, cancellationToken);

        public async Task<long> WaitForNextOpenGenerationAsync(
            long observedGeneration,
            CancellationToken cancellationToken)
        {
            _waits.Writer.TryWrite(observedGeneration);

            return await Recording.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);
        }

        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
            AcquireExpiredLeaseAdoptionInterlockAsync(
                CovenantExclusiveRecoveryOwner candidateOwner,
                Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync,
                CancellationToken cancellationToken) =>
            Recording.AcquireExpiredLeaseAdoptionInterlockAsync(
                candidateOwner,
                revalidateDurableOwnerAsync,
                cancellationToken);
    }

    private sealed class Checkpoint
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Reached => _reached.Task;

        internal async Task PauseAsync(CancellationToken cancellationToken)
        {
            _reached.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<LogMessage> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(new LogMessage(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogMessage(LogLevel Level, string Message, Exception? Exception);

    private sealed class RefusingScopeFactory : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            throw new InvalidOperationException("Admission must precede scope creation.");
        }
    }
}
