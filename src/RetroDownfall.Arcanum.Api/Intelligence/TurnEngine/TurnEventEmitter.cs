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

        await _emitGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
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
        finally
        {
            _ = _emitGate.Release();
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
