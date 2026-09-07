using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Final_request_release_at_the_internal_zero_publication_window_cannot_be_missed(int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable request = AcquireLifetime(gate, work: false);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(99));

        object closure = PrivateField(gate, "_closure")!;

        object cold = PrivateField(gate, "_sync")!;

        Assert.Equal(1, ReadCensus(gate, work: false));

        MethodInfo? publish = GateMethod("PublishStageOneZeroWhileLocked");

        Assert.NotNull(publish);

        Task? terminal = null;

        // Enter the real production suffix after the drain's initial nonzero observation.
        // Order 1 forces release into the otherwise tiny gap before signal publication.
        await RaceDedicated(
            () =>
            {

                lock (cold)
                {

                    terminal = Assert.IsAssignableFrom<Task>(publish.Invoke(gate, [closure]));

                }

            },
            () => request.DisposeAsync().GetAwaiter().GetResult(), order);

        await terminal!.WaitAsync(BoundedWait);

        Assert.Same(Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate)).Task, terminal);

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_release_before_first_drain_allocates_no_closure_waiter(bool work)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable lease = AcquireLifetime(gate, work);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(93));

        await lease.DisposeAsync();

        Assert.Null(PublishedZeroSignal(gate));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Assert.Null(PublishedZeroSignal(gate));

    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Last_request_and_work_releases_complete_the_same_published_zero_signal(int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable request = AcquireLifetime(gate, work: false);

        await using IAsyncDisposable work = AcquireLifetime(gate, work: true);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(94));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        TaskCompletionSource signal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

        Assert.False(signal.Task.IsCompleted);

        await RaceDedicated(
            () => request.DisposeAsync().GetAwaiter().GetResult(),
            () => work.DisposeAsync().GetAwaiter().GetResult(), order);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Assert.Equal(0, ReadCensus(gate, work: false));

        Assert.Equal(0, ReadCensus(gate, work: true));

        Assert.True(signal.Task.IsCompletedSuccessfully);

        Assert.Same(signal, PublishedZeroSignal(gate));

    }

    [Fact]
    public async Task Late_aborted_closure_signal_cannot_complete_a_new_closure_or_validate_the_old_owner()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        await using IAsyncDisposable old = AcquireLifetime(gate, work: false);

        await using IGrimoireClosingOwner first = Begin(gate, Owner(95));

        Task<Result> firstDrain = gate.DrainRequestAndWorkAsync(first, CancellationToken.None).AsTask();

        TaskCompletionSource oldSignal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(OpeningTimeout);

        Assert.Equal("Grimoire.WorkDrainTimeout", (await firstDrain.WaitAsync(BoundedWait)).Error.Code);

        Task<Result> oldPendingCall = gate.DrainRequestAndWorkAsync(first, CancellationToken.None).AsTask();

        Assert.True((await gate.AbortClosingAsync(first, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Null(PublishedZeroSignal(gate));

        await old.DisposeAsync();

        await using IAsyncDisposable current = AcquireLifetime(gate, work: false);

        await using IGrimoireClosingOwner second = Begin(gate, Owner(96));

        Task<Result> newDrain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        TaskCompletionSource currentSignal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

        Assert.NotSame(oldSignal, currentSignal);

        // Simulate a release that captured the old signal and was suspended through abort/reclose.
        // Completing that exact object must neither grant its old owner nor settle the new one.
        oldSignal.TrySetResult();

        Assert.Equal("Grimoire.AdmissionLifecycleConflict", (await oldPendingCall.WaitAsync(BoundedWait)).Error.Code);

        Assert.False(newDrain.IsCompleted);

        Assert.False(currentSignal.Task.IsCompleted);

        Assert.True((await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None)).IsFailure);

        await current.DisposeAsync();

        Assert.True((await newDrain.WaitAsync(BoundedWait)).IsSuccess);

        Assert.True(currentSignal.Task.IsCompletedSuccessfully);

    }

    [Fact]
    public async Task Request_release_publishes_shared_ambient_death_before_waiting_to_unlink()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable request = AcquireLifetime(gate, work: false);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(97));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        object lifetime = PrivateProperty(request, "Lifetime")!;

        object shard = PrivateProperty(request, "Shard")!;

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(shard, held, release);

        Task? disposer = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            disposer = StartDedicated(() => request.DisposeAsync().GetAwaiter().GetResult());

            await StartDedicated(() => Assert.True(SpinWait.SpinUntil(
                () => Assert.IsType<bool>(PrivateProperty(lifetime, "IsReleased")), BoundedWait))).WaitAsync(BoundedWait + BoundedWait);

            Assert.False(disposer.IsCompleted);

            Assert.Equal(1, ReadCensus(gate, work: false));

            AssertFinisherRefused(gate);

            Assert.False(drain.IsCompleted);

        }
        finally
        {

            release.Set();

            await holder.WaitAsync(BoundedWait);

            if (disposer is not null)
            {

                await disposer.WaitAsync(BoundedWait);

            }

        }

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

    }

    [Fact]
    public async Task Request_revocation_callback_runs_after_cold_and_shard_locks_are_released()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.QuiesceableStream, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        object shard = PrivateProperty(request, "Shard")!;

        TaskCompletionSource ready = NewBarrier();

        using ManualResetEventSlim callback = new();

        using ManualResetEventSlim reentered = new();

        Task reader = StartDedicated(() =>
        {

            ready.TrySetResult();

            Assert.True(callback.Wait(BoundedWait));

            Assert.Equal(1, gate.CurrentGeneration);

            lock (shard)
            {

                reentered.Set();

            }

        });

        bool observedUnlocked = false;

        try
        {

            await ready.Task.WaitAsync(BoundedWait);

            using CancellationTokenRegistration registration = request.MaintenanceRevocation.Register(() =>
            {

                callback.Set();

                observedUnlocked = reentered.Wait(BoundedWait);

                throw new InvalidOperationException("Expected callback failure.");

            });

            IGrimoireClosingOwner? owner = null;

            await StartDedicated(() => owner = Begin(gate, Owner(98))).WaitAsync(BoundedWait + BoundedWait);

            await using IGrimoireClosingOwner closing = owner!;

            Assert.True(observedUnlocked);

            await request.DisposeAsync();

            Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        }
        finally
        {

            callback.Set();

            await reader.WaitAsync(BoundedWait);

        }

    }

    [Fact]
    public void Zero_signal_compiled_protocol_has_a_full_fence_before_recheck_and_decrements_before_signal_load()
    {

        MethodInfo drain = typeof(GrimoireConnectionAdmissionGate).GetMethod("DrainRequestAndWorkAsync")!;

        Type stateMachine = drain.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;

        MethodInfo moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;

        List<MethodBase> drainCalls = OrderedCalls(moveNext);

        Assert.Contains(drainCalls, static call => call.Name == "PublishStageOneZeroWhileLocked");

        List<MethodBase> publicationCalls = OrderedCalls(GateMethod("PublishStageOneZeroWhileLocked"));

        int publication = publicationCalls.FindIndex(static call => call.DeclaringType == typeof(Interlocked)
            && call.Name == "Exchange" && call.IsGenericMethod
            && call.GetGenericArguments().Single() == typeof(TaskCompletionSource));

        Assert.True(publication >= 0);

        Assert.True(publicationCalls.FindIndex(static call => call.Name == "SignalStageOneZero") > publication);

        foreach (string release in new[] { "ReleaseRequest", "CompleteWorkLeaseIfDrainedWhileLocked" })
        {

            List<MethodBase> calls = OrderedCalls(GateMethod(release));

            int decrement = calls.FindIndex(static call => call.DeclaringType == typeof(Interlocked) && call.Name == "Decrement");

            Assert.True(decrement >= 0);

            Assert.True(calls.FindIndex(static call => call.Name == "SignalStageOneZero") > decrement);

        }

        List<MethodBase> signalCalls = OrderedCalls(GateMethod("SignalStageOneZero"));

        Assert.Equal(typeof(Volatile), signalCalls[0].DeclaringType);

        Assert.Equal("Read", signalCalls[0].Name);

        Assert.Equal("IsStageOneZero", signalCalls[1].Name);

        List<MethodBase> reserveCalls = OrderedCalls(GateMethod("ReserveCount"));

        Assert.Contains(reserveCalls, static call => call.DeclaringType == typeof(Interlocked) && call.Name == "CompareExchange");

        Assert.DoesNotContain(reserveCalls, static call => call.DeclaringType == typeof(Interlocked) && call.Name == "Increment");

    }

    private static MethodInfo GateMethod(string name) => typeof(GrimoireConnectionAdmissionGate)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;

    // Inspect executable call ordering, not source spelling. Runtime races cannot on their own
    // distinguish a release/acquire pair from the store-load fence required on every target CPU.
    private static List<MethodBase> OrderedCalls(MethodInfo method)
    {

        Dictionary<short, OpCode> opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opcode => opcode.Value);

        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;

        List<MethodBase> calls = [];

        for (int offset = 0; offset < il.Length;)
        {

            short code = il[offset++];

            if (code == 0xfe)
            {

                code = unchecked((short)(0xfe00 | il[offset++]));

            }

            OpCode opcode = opcodes[code];

            if (opcode.OperandType == OperandType.InlineMethod)
            {

                calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, offset))!);

            }

            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
                _ => 4,
            };

        }

        return calls;

    }

}
