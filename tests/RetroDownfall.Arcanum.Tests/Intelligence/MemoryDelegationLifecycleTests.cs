using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryDelegation;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MemoryDelegationLifecycleTests
{
    [SkippableFact]
    public async Task Only_one_parallel_consumer_wins()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)));

        Assert.Single(results, result => result.IsSuccess);
    }

    [SkippableFact]
    public async Task Wrong_root_or_child_does_not_consume()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, Guid.NewGuid(), Guid.NewGuid(), h.Digest)).IsFailure);

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.Empty, h.Digest)).IsFailure);

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);
    }

    [SkippableFact]
    public async Task Thirty_third_live_grant_refuses()
    {
        await using var h = await Harness.CreateAsync();

        List<MemoryDelegationRoot> roots = [];

        try
        {
            for (int i = 0; i < 32; i++)
            {
                roots.Add(await h.PrepareAsync(Guid.NewGuid()));
            }

            Assert.True((await h.TryPrepareAsync(Guid.NewGuid())).IsFailure);
        }
        finally
        {
            foreach (var root in roots)
            {
                await root.DisposeAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Five_minute_expiry_cancels_without_renewal()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(root.Revocation.IsCancellationRequested);

        Assert.True((await h.Coordinator.AdmitEffectAsync(root)).IsFailure);
    }

    [SkippableFact]
    public async Task Unused_expiry_does_not_cap_parent_work()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(root.IsConsumed);

        Assert.False(h.Source.Lease.Revocation.IsCancellationRequested);

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsFailure);
    }

    [SkippableFact]
    public async Task Revocation_after_child_result_survives_root_continuation()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

        await effect.DisposeAsync();

        await using (await h.Coordinator.BeginMutationAsync()) { }

        Assert.True(root.IsConsumed);

        Assert.True(root.Revocation.IsCancellationRequested);

        Assert.True((await h.Coordinator.AdmitEffectAsync(root)).IsFailure);
    }

    [SkippableFact]
    public async Task Parent_mutation_does_not_wait_for_parent_lifetime()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        await using var mutation = await h.Coordinator.BeginMutationAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(root.Revocation.IsCancellationRequested);
    }

    [SkippableFact]
    public async Task Admitted_effect_drains_before_mutation_and_slot_release()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

        var mutation = h.Coordinator.BeginMutationAsync().AsTask();

        Assert.True(effect.Revocation.IsCancellationRequested);

        Assert.False(mutation.IsCompleted);

        await effect.DisposeAsync();

        await using var ownership = await mutation.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task Mutation_wins_before_dispatch_then_zero_sends()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        await using (await h.Coordinator.BeginMutationAsync()) { }

        int sends = 0;

        if ((await h.Coordinator.AdmitEffectAsync(root)).IsSuccess) sends++;

        Assert.Equal(0, sends);
    }

    [SkippableFact]
    public async Task Disposed_parent_lease_refuses_preparation_and_existing_consumption()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        await h.Source.Lease.DisposeAsync();

        Assert.True((await h.TryPrepareAsync(Guid.NewGuid())).IsFailure);

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsFailure);
    }

    [SkippableFact]
    public async Task Expired_retained_root_releases_source_bytes_and_cannot_renew()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest));

        Assert.Contains("fact", child.Sources.RenderedMemory);

        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperationException>(() => child.Sources);

        Assert.True((await h.TryPrepareAsync()).IsFailure);

        await using var another = await h.PrepareAsync(Guid.NewGuid());
    }

    [SkippableFact]
    public async Task Expiry_holds_slot_until_effect_disposal()
    {
        await using var h = await Harness.CreateAsync();

        List<MemoryDelegationRoot> roots = [];

        List<MemoryDelegationEffect> effects = [];

        try
        {
            for (int i = 0; i < 32; i++)
            {
                Guid id = Guid.NewGuid();

                var root = await h.PrepareAsync(id);

                roots.Add(root);

                Assert.True((await h.Coordinator.ConsumeAsync(root, id, Guid.NewGuid(), h.Digest)).IsSuccess);

                effects.Add(MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root)));
            }

            h.Time.Advance(TimeSpan.FromMinutes(5));

            Assert.True((await h.TryPrepareAsync(Guid.NewGuid())).IsFailure);

            await effects[0].DisposeAsync();

            await using var fresh = await h.PrepareAsync(Guid.NewGuid());
        }
        finally
        {
            foreach (var effect in effects) await effect.DisposeAsync();

            foreach (var root in roots) await root.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Rollback_reopens_fresh_admission_without_reviving_old_root()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var mutation = await h.Coordinator.BeginMutationAsync();

        var preparation = h.TryPrepareAsync(Guid.NewGuid());

        Assert.False(preparation.IsCompleted);

        await mutation.DisposeAsync();

        await using var fresh = MemoryEvaluationSourceHarness.Require(await preparation);

        Assert.True(root.Revocation.IsCancellationRequested);

        Assert.False(fresh.Revocation.IsCancellationRequested);
    }

    [SkippableFact]
    public async Task Forged_parent_registration_refuses_even_when_self_revalidation_succeeds()
    {
        await using var h = await Harness.CreateAsync();

        await using CovenantTurnLease forged = new(new ForgedRegistration(h.Source.Lease.Snapshot));

        Assert.True((await forged.RevalidateAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await h.TryPrepareAsync(lease: forged)).IsFailure);
    }

    private sealed class ForgedRegistration(CovenantOperationLeaseSnapshot snapshot) : ICovenantLeaseRegistration
    {
        public CovenantOperationLeaseSnapshot Snapshot => snapshot;

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    [SkippableFact]
    public async Task Child_completion_drains_only_child_and_preserves_root_continuation()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Guid childId = Guid.NewGuid();

        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, childId, h.Digest));

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(child, childId, h.Digest));

        var completion = child.DisposeAsync().AsTask();

        try
        {
            Assert.True(child.Revocation.IsCancellationRequested);

            Assert.False(completion.IsCompleted);

            Assert.True((await h.Coordinator.AdmitEffectAsync(child, childId, h.Digest)).IsFailure);

            Assert.True((await h.Coordinator.AdmitEffectAsync(child, Guid.NewGuid(), h.Digest)).IsFailure);

            await using var parentEffect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

            Assert.False(root.Revocation.IsCancellationRequested);
        }
        finally
        {
            await effect.DisposeAsync();
        }

        await completion.WaitAsync(TimeSpan.FromSeconds(10));

        await using var continuation = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));
    }

    [SkippableFact]
    public async Task Disposing_parent_cancels_already_admitted_child_and_refuses_publication()
    {
        await using var h = await Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Guid childId = Guid.NewGuid();

        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, childId, h.Digest));

        await using var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(child, childId, h.Digest));

        await h.Source.Lease.DisposeAsync();

        Assert.True(effect.Revocation.IsCancellationRequested);

        Assert.True((await h.Coordinator.AdmitEffectAsync(child, childId, h.Digest)).IsFailure);

        Assert.True((await h.Coordinator.AdmitEffectAsync(root)).IsFailure);
    }

    [SkippableFact]
    public async Task Parent_disposal_after_source_read_refuses_without_leaking_unreturned_root()
    {
        await using var h = await Harness.CreateAsync();

        var reader = new DisposingReader(new MemoryDelegationSourceReader(new FixedCovenantConnectionSource(h.Source.Connection), h.Source.Gate, TimeProvider.System));

        Assert.True((await h.TryPrepareAsync(reader: reader)).IsFailure);

        await h.Source.AdmitAsync();

        await using var root = await h.PrepareAsync();

        Assert.False(root.Revocation.IsCancellationRequested);
    }

    private sealed class DisposingReader(IMemoryDelegationSourceReader inner) : IMemoryDelegationSourceReader
    {
        public async Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
        {
            var result = await inner.ReadAsync(grant, parent, lease, ct);

            await lease.DisposeAsync();

            return result;
        }

        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => inner.ValidateAsync(snapshot, lease, ct);
    }

    [SkippableFact]
    public async Task Repeated_terminal_disposal_cannot_remove_a_new_registration_for_the_same_root_id()
    {
        await using var h = await Harness.CreateAsync();

        var old = await h.PrepareAsync();

        await old.DisposeAsync();

        await using var current = await h.PrepareAsync();

        await old.DisposeAsync();

        var duplicate = await h.TryPrepareAsync();

        if (duplicate.IsSuccess) await duplicate.Value.DisposeAsync();

        Assert.True(duplicate.IsFailure);
    }

    [SkippableTheory]
    [InlineData("maintenance")]
    [InlineData("parent")]
    [InlineData("deadline")]
    [InlineData("caller")]
    public async Task Blocked_preparation_cancels_reader_but_drains_only_after_teardown(string cause)
    {
        await using var h = await Harness.CreateAsync();

        using var caller = new CancellationTokenSource();

        var reader = new BlockingReader(h.CreateReader()) { BlockRead = true };

        var preparation = h.TryPrepareAsync(reader: reader, ct: caller.Token);

        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task? drain = null;

        try
        {
            if (cause == "maintenance") drain = h.Coordinator.CloseForMaintenance();
            else if (cause == "parent") await h.Source.Lease.DisposeAsync();
            else if (cause == "caller") caller.Cancel();
            else h.Time.Advance(TimeSpan.FromMinutes(5));

            await reader.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(preparation.IsCompleted);

            drain ??= h.Coordinator.CloseForMaintenance();

            Assert.False(drain.IsCompleted);
        }
        finally
        {
            reader.Teardown.TrySetResult();
        }

        if (cause == "caller") Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation)).CancellationToken);
        else Assert.Equal(ErrorCodes.MemoryDelegation.StaleSource, (await preparation).Error.Code);

        await drain!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, h.Time.ActiveTimers);

        h.Coordinator.ReopenAfterMaintenance();

        if (cause == "parent") await h.Source.AdmitAsync();

        await using var fresh = await h.PrepareAsync();

        Assert.False(fresh.Revocation.IsCancellationRequested);
    }

    [SkippableTheory]
    [InlineData("consume", "caller")]
    [InlineData("root", "caller")]
    [InlineData("consume", "deadline")]
    [InlineData("consume", "maintenance")]
    [InlineData("root", "deadline")]
    [InlineData("root", "maintenance")]
    [InlineData("child", "deadline")]
    [InlineData("child", "maintenance")]
    [InlineData("child", "completion")]
    [InlineData("child", "caller")]
    public async Task Blocked_validation_cancels_reader_but_drains_only_after_teardown(string operation, string cause)
    {
        await using var h = await Harness.CreateAsync();

        using var caller = new CancellationTokenSource();

        var reader = new BlockingReader(h.CreateReader());

        await using var root = MemoryEvaluationSourceHarness.Require(await h.TryPrepareAsync(reader: reader));

        Guid childId = Guid.NewGuid();

        MemoryDelegationChild? child = operation == "consume" ? null
            : MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, childId, h.Digest));

        reader.BlockValidation = true;

        async Task<Result> ValidateAsync()
        {
            if (operation == "consume")
            {
                var result = await h.Coordinator.ConsumeAsync(root, h.RootId, childId, h.Digest, caller.Token);

                return result.IsFailure ? result.Error : Result.Success();
            }

            var effect = operation == "child"
                ? await h.Coordinator.AdmitEffectAsync(child!, childId, h.Digest, caller.Token)
                : await h.Coordinator.AdmitEffectAsync(root, caller.Token);

            if (effect.IsSuccess) await effect.Value.DisposeAsync();

            return effect.IsFailure ? effect.Error : Result.Success();
        }

        var validation = ValidateAsync();

        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task? drain = null;

        try
        {
            if (cause == "maintenance") drain = h.Coordinator.CloseForMaintenance();
            else if (cause == "completion") drain = child!.DisposeAsync().AsTask();
            else if (cause == "caller") caller.Cancel();
            else h.Time.Advance(TimeSpan.FromMinutes(5));

            await reader.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(validation.IsCompleted);

            drain ??= root.DisposeAsync().AsTask();

            Assert.False(drain.IsCompleted);
        }
        finally
        {
            reader.Teardown.TrySetResult();
        }

        if (cause == "caller") Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation)).CancellationToken);
        else Assert.Equal(ErrorCodes.MemoryDelegation.StaleSource, (await validation).Error.Code);

        await drain!.WaitAsync(TimeSpan.FromSeconds(10));

        reader.BlockValidation = false;

        if (cause == "completion")
        {
            Assert.False(root.Revocation.IsCancellationRequested);

            await using var continuation = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));
        }
        else
        {
            await root.DisposeAsync();

            if (child is not null) Assert.Throws<InvalidOperationException>(() => child.Sources);

            Assert.Equal(0, h.Time.ActiveTimers);

            h.Coordinator.ReopenAfterMaintenance();

            await using var fresh = await h.PrepareAsync();
        }
    }

    private sealed class BlockingReader(IMemoryDelegationSourceReader inner) : IMemoryDelegationSourceReader
    {
        internal bool BlockRead { get; init; }

        internal bool BlockValidation { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Teardown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task BlockAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());

            Entered.TrySetResult();

            await Teardown.Task;

            ct.ThrowIfCancellationRequested();
        }

        public async Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
        {
            var result = await inner.ReadAsync(grant, parent, lease, ct);

            if (BlockRead) await BlockAsync(ct);

            return result;
        }

        public async Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct)
        {
            if (BlockValidation) await BlockAsync(ct);

            return await inner.ValidateAsync(snapshot, lease, ct);
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private Harness(MemoryEvaluationSourceHarness source) => Source = source;

        internal MemoryEvaluationSourceHarness Source { get; }

        internal ManualTimerTimeProvider Time { get; } = new();

        internal MemoryDelegationCoordinator Coordinator { get; private set; } = null!;

        internal Guid RootId { get; } = Guid.NewGuid();

        internal CovenantDigest Digest { get; } = new(new byte[32]);

        internal string SourceId { get; private init; } = "source";

        internal static async Task<Harness> CreateAsync(bool sensitive = false)
        {
            var source = await MemoryEvaluationSourceHarness.CreateAsync();

            string sourceId = sensitive ? Guid.NewGuid().ToString("D") : "source";

            await source.Saga.Store.InsertAsync(sourceId, "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", source.Saga.Embedding(), CancellationToken.None);

            if (sensitive) await source.Saga.LabelSensitiveAsync(Guid.Parse(sourceId));

            await source.AdmitAsync();

            var h = new Harness(source) { SourceId = sourceId };

            h.Coordinator = new(h.Time);

            return h;
        }

        internal async Task<Result<MemoryDelegationRoot>> TryPrepareAsync(Guid? id = null, CovenantTurnLease? lease = null, IMemoryDelegationSourceReader? reader = null, CancellationToken ct = default)
        {
            var request = await Source.RequestAsync(SourceId);

            var parent = MemoryEvaluationSourceHarness.Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
                Source.Invocation.Campaign, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.AllTools, Source.Invocation.ReadAuthorityEpoch));

            return await Coordinator.PrepareAsync(id ?? RootId, new(request.Request.Scope, request.Request.Memory), parent, lease ?? Source.Lease,
                reader ?? CreateReader(), ct);
        }

        internal IMemoryDelegationSourceReader CreateReader() => new MemoryDelegationSourceReader(new FixedCovenantConnectionSource(Source.Connection), Source.Gate, TimeProvider.System);

        internal async Task<MemoryDelegationRoot> PrepareAsync(Guid? id = null) => MemoryEvaluationSourceHarness.Require(await TryPrepareAsync(id));

        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
