using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Effect_winner_is_revoked_after_group_release_even_after_abort(
        bool scopeFirst,
        bool abortBeforeRelease)
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquiredGroup));

        await using IGrimoireExternalEffectGroup group = acquiredGroup!;

        int callbacks = 0;

        using CancellationTokenRegistration registration = work.MaintenanceRevocation.Register(() => Interlocked.Increment(ref callbacks));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(60));

        Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        if (abortBeforeRelease)
        {

            await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

            clock.Advance(OpeningTimeout);

            Assert.Equal("Grimoire.WorkDrainTimeout", (await drain.WaitAsync(BoundedWait)).Error.Code);

            Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

            Assert.Equal(2, gate.CurrentGeneration);

            Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

        }

        if (scopeFirst)
        {

            await work.DisposeAsync();

            Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

            if (!abortBeforeRelease)
            {

                Assert.False(drain.IsCompleted);

            }

        }

        await group.DisposeAsync();

        Assert.True(work.MaintenanceRevocation.IsCancellationRequested);

        Assert.Equal(1, callbacks);

        Assert.False(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? next));

        Assert.Null(next);

        await group.DisposeAsync();

        await work.DisposeAsync();

        await work.DisposeAsync();

        Assert.Equal(1, callbacks);

        if (!abortBeforeRelease)
        {

            Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        }

    }

    [Fact]
    public async Task Deferred_revocation_callback_reenters_from_another_thread_and_failure_does_not_block_drain()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquiredGroup));

        await using IGrimoireExternalEffectGroup group = acquiredGroup!;

        TaskCompletionSource ready = NewBarrier();

        using ManualResetEventSlim callbackEntered = new();

        using ManualResetEventSlim reentered = new();

        Task reader = StartDedicated(() =>
        {

            ready.TrySetResult();

            Assert.True(callbackEntered.Wait(BoundedWait));

            Assert.Equal(1, gate.CurrentGeneration);

            reentered.Set();

        });

        bool callbackObservedUnlockedGate = false;

        int callbacks = 0;

        try
        {

            await ready.Task.WaitAsync(BoundedWait);

            using CancellationTokenRegistration registration = work.MaintenanceRevocation.Register(() =>
            {

                Interlocked.Increment(ref callbacks);

                callbackEntered.Set();

                callbackObservedUnlockedGate = reentered.Wait(BoundedWait);

                throw new InvalidOperationException("Expected consumer callback failure.");

            });

            await using IGrimoireClosingOwner closing = Begin(gate, Owner(61));

            Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            await work.DisposeAsync();

            await StartDedicated(() => group.DisposeAsync().GetAwaiter().GetResult()).WaitAsync(BoundedWait + BoundedWait);

            Assert.Equal(1, callbacks);

            Assert.True(callbackObservedUnlockedGate);

            Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        }
        finally
        {

            callbackEntered.Set();

            await reader.WaitAsync(BoundedWait);

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_exhaustion_preserves_closing_owner_and_does_not_reopen(bool abort)
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        GenerationField(gate) = long.MaxValue;

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        CovenantExclusiveRecoveryOwner owner = Owner(62);

        await using IGrimoireClosingOwner closing = Begin(gate, owner);

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(long.MaxValue, CancellationToken.None);

        if (abort)
        {

            Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

            clock.Advance(OpeningTimeout);

            Assert.Equal("Grimoire.WorkDrainTimeout", (await drain.WaitAsync(BoundedWait)).Error.Code);

            await Assert.ThrowsAsync<OverflowException>(() => gate.AbortClosingAsync(
                closing, static _ => ValueTask.FromResult(true), CancellationToken.None).AsTask());

        }
        else
        {

            await request.DisposeAsync();

            Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

            await Assert.ThrowsAsync<OverflowException>(() => gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None).AsTask());

        }

        Assert.Equal(long.MaxValue, gate.CurrentGeneration);

        Assert.False(nextOpen.IsCompleted);

        Assert.False(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));

        Assert.False(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out _));

        Result<IGrimoireClosingOwner> resumed = gate.BeginOrResumeExclusive(owner);

        Assert.True(resumed.IsSuccess);

        Assert.Same(closing, resumed.Value);

        await request.DisposeAsync();

        using SqliteConnection connection = new();

        Assert.Throws<GrimoireMaintenanceUnavailableException>(() => gate.AcquireOrdinaryOpen(connection));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        await Assert.ThrowsAsync<OverflowException>(() => gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None).AsTask());

        Assert.False(nextOpen.IsCompleted);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_lifo_disposal_does_not_retain_released_ambient_history(bool work)
    {

        await StartDedicated(() =>
        {

            GrimoireConnectionAdmissionGate gate = CreateGate();

            for (int cycle = 0; cycle < 4; cycle++)
            {

                IAsyncDisposable first = AcquireLifetime(gate, work);

                IAsyncDisposable second = AcquireLifetime(gate, work);

                first.DisposeAsync().GetAwaiter().GetResult();

                second.DisposeAsync().GetAwaiter().GetResult();

            }

            Assert.Equal(0, AmbientLifetimeDepth());

            GC.KeepAlive(gate);

        }).WaitAsync(BoundedWait);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_admission_does_not_retain_a_flowed_released_ambient_predecessor(bool work)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        IAsyncDisposable original = AcquireLifetime(gate, work);

        TaskCompletionSource ready = NewBarrier();

        using ManualResetEventSlim released = new();

        Task child = StartDedicated(() =>
        {

            ready.TrySetResult();

            Assert.True(released.Wait(BoundedWait));

            IAsyncDisposable current = AcquireLifetime(gate, work);

            try
            {

                Assert.Equal(1, AmbientLifetimeDepth());

            }
            finally
            {

                current.DisposeAsync().GetAwaiter().GetResult();

            }

        });

        try
        {

            await ready.Task.WaitAsync(BoundedWait);

            await original.DisposeAsync();

        }
        finally
        {

            await original.DisposeAsync();

            released.Set();

            await child.WaitAsync(BoundedWait);

        }

    }

    // This read-only retention census is test-local: no mutation hook, collection timing, or
    // native lifetime is needed to observe released nodes pinned by the current execution flow.
    private static int AmbientLifetimeDepth()
    {

        object ambient = typeof(GrimoireConnectionAdmissionGate)
            .GetField("CurrentOrdinaryLifetime", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        object? lifetime = ambient.GetType().GetProperty("Value")!.GetValue(ambient);

        int depth = 0;

        while (lifetime is not null)
        {

            depth++;

            lifetime = lifetime.GetType().GetProperty("Previous", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifetime);

        }

        return depth;

    }

    private static IAsyncDisposable AcquireLifetime(GrimoireConnectionAdmissionGate gate, bool work)
    {

        if (work)
        {

            Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out IGrimoireWorkLease? lease));

            return lease!;

        }

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        return request!;

    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_generation")]
    private static extern ref long GenerationField(GrimoireConnectionAdmissionGate gate);

    private static Task StartDedicated(Action action) => Task.Factory.StartNew(
        action,
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

}
