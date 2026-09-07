using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class WorkspaceIndexingServiceTests
{
    [SkippableFact]
    public async Task Scheduled_sweep_attached_to_active_incremental_work_stays_due_until_its_full_followup_settles()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string file = _workspace.WriteFile("one.cs", "class One {}");

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        ControlledWorkspaceWeave weave = new();

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers, workAdmission: gate);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerChanged(file);

        service.QueuePendingWatcherEvents(_workspace.Root, CancellationToken.None);

        WorkspaceEmbeddingCall call = await weave.NextAsync();

        DateTimeOffset due = service.GetScheduledSweepSnapshot().NextReconciliation;

        await service.StartAsync(CancellationToken.None);

        await WaitForWorkspaceConditionAsync(() => service.GetScheduledSweepSnapshot().Outstanding == 1);

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        call.Release();

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 1);

        try
        {
            Assert.Equal([inner.CurrentGeneration - 1], gate.ObservedGenerationWaits);

            Assert.Equal(1, service.GetScheduledSweepSnapshot().Outstanding);

            Assert.Equal(due, service.GetScheduledSweepSnapshot().NextReconciliation);

            await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await WaitForWorkspaceConditionAsync(() => service.GetScheduledSweepSnapshot().Outstanding == 0);

            Assert.True(service.GetScheduledSweepSnapshot().NextReconciliation > DateTimeOffset.UtcNow);

            Assert.Equal(1, weave.CallCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [SkippableFact]
    public async Task Closed_backlog_retains_only_two_exact_generation_waiters_and_reclose_cannot_run_suffix()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing);

        ObservingScopeFactory scopes = new(BuildScopeFactory());

        ControlledWorkspaceWeave weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, scopeFactory: scopes, workAdmission: gate);

        for (int index = 0; index < 32; index++)
        {
            _workspace.WriteFile($"workspace-{index}/one.cs", $"class Workspace{index} {{}}");

            Assert.True(service.QueueIndexNow(Path.Combine(_workspace.Root, $"workspace-{index}")).IsSuccess);
        }

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 2);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(2, 30, true), service.GetSchedulerSnapshot());

        Assert.Equal(2, gate.RequestedWorkKinds.Count);

        Assert.Equal(0, scopes.ScopeCount);

        Assert.Equal(0, gate.EffectGroupAttempts);

        IGrimoireClosingOwner? reclosed = null;

        object recloseGate = new();

        gate.AfterOpenGenerationObserved = _ =>
        {
            lock (recloseGate)
            {
                reclosed ??= BeginWorkspaceClosing(inner);
            }
        };

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        await WaitForWorkspaceConditionAsync(() => gate.GenerationWaits == 4 && gate.ActiveGenerationWaiters == 2);

        Assert.Equal(0, scopes.ScopeCount);

        Assert.Equal(4, gate.RequestedWorkKinds.Count);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(2, 30, true), service.GetSchedulerSnapshot());

        await using IGrimoireExclusiveClosedLease secondClosed = await CloseWorkspaceGateAsync(inner, reclosed!);

        Assert.True((await secondClosed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        WorkspaceEmbeddingCall first = await weave.NextAsync();

        WorkspaceEmbeddingCall second = await weave.NextAsync();

        try
        {
            Assert.Equal(["class Workspace0 {}", "class Workspace1 {}"], new[] { first.Text, second.Text }.Order(StringComparer.Ordinal));

            Assert.Equal(2, scopes.ScopeCount);
        }
        finally
        {
            Task stop = service.StopAsync(CancellationToken.None);

            first.Release();

            second.Release();

            await stop.WaitAsync(TimeSpan.FromSeconds(30));

            await reclosed!.DisposeAsync();
        }

        Assert.Equal(0, gate.ActiveGenerationWaiters);

        Assert.Equal(2, weave.CallCount);
    }

    [SkippableFact]
    public async Task KeepClosed_and_canceled_stop_preserve_no_scope_no_effect_and_join_waiter_cleanup()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing);

        FailingScopeFactory scopes = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes, workAdmission: gate);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 1);

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

        Assert.Equal(1, gate.GenerationWaits);

        Assert.Equal(0, scopes.ScopeCount);

        Assert.Equal(0, gate.EffectGroupAttempts);

        await service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, gate.ActiveGenerationWaiters);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());

        Assert.Equal(ErrorCodes.Workspace.IndexingUnavailable, service.QueueIndexNow(_workspace.Root).Error.Code);
    }

    [SkippableFact]
    public async Task Lost_file_frontier_defers_without_mutating_chunks_or_failure_status()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeEffectGroupAdmission = () => closing ??= BeginWorkspaceClosing(inner);

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, workAdmission: gate);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 1);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

        Assert.False(service.GetRuntimeStatus(_workspace.Root).Degraded);

        Assert.Null(service.GetRuntimeStatus(_workspace.Root).LastSuccessfulIndexAt);

        Assert.Equal(1, gate.EffectGroupAttempts);

        await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing!);

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        await DrainWorkspaceSchedulerAsync(service);

        Assert.True(await CountRowsAsync("workspace_file_chunks") > 0);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.Equal(1, gate.GenerationWaits);

        await closing!.DisposeAsync();
    }

    [SkippableFact]
    public async Task Force_all_reconciliation_resumes_only_unprocessed_files_after_a_winning_group()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        _workspace.WriteFile("two.cs", "class Two {}");

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        ControlledWorkspaceWeave weave = new();

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers, workAdmission: gate);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.TriggerError(new InternalBufferOverflowException("force reconciliation"));

        service.QueuePendingWatcherEvents(_workspace.Root, CancellationToken.None);

        WorkspaceEmbeddingCall first = await weave.NextAsync();

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        first.Release();

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 1);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.Single(await GetIndexedRelativePathsAsync());

        await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing);

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        WorkspaceEmbeddingCall second = await weave.NextAsync();

        second.Release();

        await DrainWorkspaceSchedulerAsync(service);

        Assert.Equal(2, weave.CallCount);

        Assert.Equal(3, gate.EffectGroupAttempts);

        Assert.Equal(2, (await GetIndexedRelativePathsAsync()).Count);
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseWorkspaceGateAsync(GrimoireConnectionAdmissionGate gate, IGrimoireClosingOwner closing)
    {
        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;
    }

    private static async Task WaitForWorkspaceConditionAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private sealed class ObservingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _scopeCount;

        internal int ScopeCount => Volatile.Read(ref _scopeCount);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _scopeCount);

            return inner.CreateScope();
        }
    }

    private interface IReleasableWorkspaceWeave
    {
        void ReleaseAll();
    }

    private sealed class ControlledWorkspaceWeave : IWeaveService, IReleasableWorkspaceWeave
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<WorkspaceEmbeddingCall> _ownedCalls = new();

        private int _releaseImmediately;

        private readonly Channel<WorkspaceEmbeddingCall> _calls = Channel.CreateUnbounded<WorkspaceEmbeddingCall>();

        private int _callCount;

        public bool IsAvailable => true;

        internal int CallCount => Volatile.Read(ref _callCount);

        public void ReleaseAll()
        {
            Volatile.Write(ref _releaseImmediately, 1);

            foreach (WorkspaceEmbeddingCall call in _ownedCalls)
            {
                call.Release();
            }
        }

        internal async Task<WorkspaceEmbeddingCall> NextAsync() =>
            await _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            WorkspaceEmbeddingCall call = new(string.Join("", texts), cancellationToken);

            _ownedCalls.Enqueue(call);

            if (Volatile.Read(ref _releaseImmediately) != 0)
            {
                call.Release();
            }

            Assert.True(_calls.Writer.TryWrite(call));

            await call.Released.Task;

            return Result<Embedding<float>[]>.Success(texts.Select(static _ => new Embedding<float>(new float[] { 1f, 0f, 0f })).ToArray());
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class WorkspaceEmbeddingCall(string text, CancellationToken cancellationToken)
    {
        internal string Text { get; } = text;

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => Released.TrySetResult();
    }
}
