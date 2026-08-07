using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Ordered semantic event emitter. All writers (including Ward/HITL callbacks) go through
/// <see cref="EmitAsync"/> so sequence numbers stay monotonic.
/// </summary>
internal sealed class TurnEventEmitter : IAsyncDisposable
{

    private readonly Channel<TurnEvent> _channel;
    private readonly SemaphoreSlim _emitGate = new(1, 1);
    private readonly Guid _runId;
    private long _sequence;
    private int _terminalEmitted;
    private bool _completed;

    public TurnEventEmitter(Guid runId, int capacity = 256)
    {
        _runId = runId;
        _channel = Channel.CreateBounded<TurnEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public Guid RunId => _runId;

    public ChannelReader<TurnEvent> Reader => _channel.Reader;

    public bool TerminalEmitted => Volatile.Read(ref _terminalEmitted) == 1;

    public TurnEventCorrelation NextCorrelation(
        int providerAttempt = 0,
        int modelRound = 0,
        string? modelCallId = null,
        string? toolCallId = null)
    {
        long sequence = Interlocked.Increment(ref _sequence);

        return new TurnEventCorrelation(
            _runId,
            sequence,
            providerAttempt,
            modelRound,
            modelCallId,
            toolCallId,
            DateTimeOffset.UtcNow);
    }

    public async ValueTask EmitAsync(TurnEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        bool gateHeld = false;

        try
        {
            // Inside the try: a consumer that abandons the run disposes this emitter while the
            // producer is still emitting, and both WaitAsync and Release throw ObjectDisposedException
            // on a disposed semaphore. Terminal emission from a producer's catch block must never
            // throw, or the run ends as a faulted, unobserved background task.
            await _emitGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            gateHeld = true;

            if (TerminalEmitted)
            {
                return;
            }

            await _channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);

            if (evt.IsTerminal)
            {
                _ = Interlocked.Exchange(ref _terminalEmitted, 1);
                _ = _channel.Writer.TryComplete();
                _completed = true;
            }
        }
        catch (ChannelClosedException)
        {
            // Producer raced with completion — ignore further writes.
        }
        catch (ObjectDisposedException)
        {
            // The consumer abandoned the run and disposed the emitter; nothing is listening.
        }
        finally
        {
            if (gateHeld)
            {
                try
                {
                    _ = _emitGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed under us mid-emit — the gate no longer guards anything.
                }
            }
        }
    }

    public void CompleteWithoutTerminal()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _ = _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        CompleteWithoutTerminal();
        _emitGate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

}
