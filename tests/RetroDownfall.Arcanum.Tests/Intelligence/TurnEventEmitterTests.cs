using System.Threading.Channels;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnEventEmitterTests
{

    [Fact]
    public async Task EmitAsync_AssignsMonotonicSequences_ViaNextCorrelation()
    {
        await using TurnEventEmitter emitter = new(Guid.NewGuid());

        TurnEventCorrelation first = emitter.NextCorrelation();

        TurnEventCorrelation second = emitter.NextCorrelation();

        Assert.Equal(1, first.Sequence);

        Assert.Equal(2, second.Sequence);

        Assert.True(second.Sequence > first.Sequence);

        await emitter.EmitAsync(new RunStarted(first));

        await emitter.EmitAsync(new TurnStatusChanged(second, "working"));

        emitter.CompleteWithoutTerminal();

        List<TurnEvent> events = await ReadAllAsync(emitter);

        Assert.Equal(2, events.Count);

        Assert.Equal(1, events[0].Correlation.Sequence);

        Assert.Equal(2, events[1].Correlation.Sequence);
    }

    [Fact]
    public async Task EmitAsync_IgnoresSecondTerminal()
    {
        await using TurnEventEmitter emitter = new(Guid.NewGuid());

        await emitter.EmitAsync(new RunCompleted(
            emitter.NextCorrelation(),
            "done",
            Usage: null,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: [],
            SessionId: null,
            StructuredOutputWarning: false));

        Assert.True(emitter.TerminalEmitted);

        await emitter.EmitAsync(new RunFailed(
            emitter.NextCorrelation(),
            new Error(ErrorCodes.Hub.Error, "should not appear"),
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null));

        List<TurnEvent> events = await ReadAllAsync(emitter);

        Assert.Single(events);

        Assert.IsType<RunCompleted>(events[0]);
    }

    [Fact]
    public async Task EmitAsync_PreservesOrderedEmission_UnderConcurrentWriters()
    {
        await using TurnEventEmitter emitter = new(Guid.NewGuid());

        Task[] writers = Enumerable.Range(0, 20)
            .Select(async i =>
            {
                TurnEventCorrelation correlation = emitter.NextCorrelation();

                await emitter
                    .EmitAsync(new TurnStatusChanged(correlation, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ConfigureAwait(false);
            })
            .ToArray();

        await Task.WhenAll(writers);

        await emitter.EmitAsync(new RunCompleted(
            emitter.NextCorrelation(),
            "done",
            Usage: null,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: [],
            SessionId: null,
            StructuredOutputWarning: false));

        List<TurnEvent> events = await ReadAllAsync(emitter);

        Assert.Equal(21, events.Count);

        long previous = 0;

        foreach (TurnEvent evt in events)
        {
            Assert.True(evt.Correlation.Sequence > previous);

            previous = evt.Correlation.Sequence;
        }
    }

    // A consumer that abandons the run disposes the emitter while the pipeline is still producing.
    // The producer's terminal emission (RunAbandoned/RunFailed, raised from inside a catch block)
    // must not throw ObjectDisposedException off the disposed emit gate — that would fault the
    // producer Task with nobody observing it and leave the run with no terminal event at all.
    [Fact]
    public async Task EmitAsync_after_dispose_does_not_throw()
    {
        TurnEventEmitter emitter = new(Guid.NewGuid());

        await emitter.DisposeAsync();

        await emitter.EmitAsync(new RunFailed(
            emitter.NextCorrelation(),
            new Error(ErrorCodes.Hub.Error, "terminal emission after abandonment"),
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: true,
            PartialText: null));

        Assert.False(emitter.TerminalEmitted);
    }

    // W1-1 added an OperationCanceledException catch (guarded by cancellationToken.IsCancellationRequested)
    // to EmitAsync, but nothing before this exercised it directly: the abandonment test's cancellation
    // happens elsewhere in the pump, never inside a blocked WriteAsync. This blocks the channel at
    // capacity 1, cancels the second write's own token while it is parked waiting for room, and asserts
    // EmitAsync completes without throwing.
    [Fact]
    public async Task EmitAsync_TokenCancelledWhileBlockedOnFullChannel_DoesNotThrow()
    {
        await using TurnEventEmitter emitter = new(Guid.NewGuid(), capacity: 1);

        await emitter.EmitAsync(new TurnStatusChanged(emitter.NextCorrelation(), "first"));

        using CancellationTokenSource cts = new();

        Task emitTask = emitter
            .EmitAsync(new TurnStatusChanged(emitter.NextCorrelation(), "second"), cts.Token)
            .AsTask();

        await Task.Delay(100);

        Assert.False(emitTask.IsCompleted);

        await cts.CancelAsync();

        await emitTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pins the catch's scope from the other side: an emit that is merely blocked on a full channel —
    // no cancellation of its own token involved — must keep waiting rather than being swallowed as if
    // it had been cancelled. Only draining the channel, not the passage of time, unblocks it.
    [Fact]
    public async Task EmitAsync_UncancelledTokenOnFullChannel_StaysBlockedUntilDrained()
    {
        await using TurnEventEmitter emitter = new(Guid.NewGuid(), capacity: 1);

        await emitter.EmitAsync(new TurnStatusChanged(emitter.NextCorrelation(), "first"));

        Task emitTask = emitter
            .EmitAsync(new TurnStatusChanged(emitter.NextCorrelation(), "second"), CancellationToken.None)
            .AsTask();

        await Task.Delay(100);

        Assert.False(emitTask.IsCompleted);

        ChannelReader<TurnEvent> reader = emitter.Reader;

        _ = await reader.ReadAsync();

        await emitTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<List<TurnEvent>> ReadAllAsync(TurnEventEmitter emitter)
    {
        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in emitter.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        return events;
    }

}
