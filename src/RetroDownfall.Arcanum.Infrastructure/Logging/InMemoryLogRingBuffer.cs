using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

/// <summary>
/// Bounded in-memory ring buffer for <see cref="LogEntry"/> with fan-out channels for streaming.
/// </summary>
/// <remarks>
/// The ring-buffer capacity and the fan-out channel capacity are read once at construction
/// (from <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/>). Recreating the ring
/// buffer or its channels on a config reload would drop in-flight entries and is intentionally
/// avoided; changes to <c>Logs.RingBufferCapacity</c> and <c>EventBus.ChannelCapacity</c>
/// require a restart to take effect here.
/// </remarks>
internal sealed class InMemoryLogRingBuffer : ILogRingBuffer
{

    private readonly Lock _lock = new();

    private readonly Dictionary<Guid, ChannelWriter<LogEntry>> _writers = new();

    private readonly int _capacity;

    // Log stream fan-out channels share the same bounded capacity tuning as the event bus.
    private readonly BoundedChannelOptions _channelOptions;

    private readonly LogEntry[] _entries;

    private long _sequence;

    private int _count;

    private int _head;

    public InMemoryLogRingBuffer(IOptionsMonitor<ArcanumSettings> optionsMonitor)
    {

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        _capacity = ArcanumSettingClamps.LogRingBufferCapacity(
            settings.Logs?.RingBufferCapacity ?? new LogSettings().RingBufferCapacity);

        _entries = new LogEntry[_capacity];

        _channelOptions = new BoundedChannelOptions(
            ArcanumSettingClamps.EventBusChannelCapacity(
                settings.EventBus?.ChannelCapacity ?? new EventBusSettings().ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        };
    }

    public void Write(LogEntry entry)
    {

        long sequence = Interlocked.Increment(ref _sequence);

        LogEntry stored = entry with { Sequence = sequence };

        List<ChannelWriter<LogEntry>> writers;

        lock (_lock)
        {

            if (_count < _capacity)
            {

                int index = (_head + _count) % _capacity;

                _entries[index] = stored;

                _count++;
            }
            else
            {

                _entries[_head] = stored;

                _head = (_head + 1) % _capacity;
            }

            if (_writers.Count == 0)
            {

                return;

            }

            writers = _writers.Values.ToList();

        }

        foreach (ChannelWriter<LogEntry> writer in writers)
        {

            _ = writer.TryWrite(stored);
        }
    }

    public IReadOnlyList<LogEntry> GetSnapshot()
    {

        lock (_lock)
        {

            if (_count == 0)
            {

                return Array.Empty<LogEntry>();
            }

            LogEntry[] snapshot = new LogEntry[_count];

            for (int i = 0; i < _count; i++)
            {

                int index = (_head + i) % _capacity;

                snapshot[i] = _entries[index];
            }

            return snapshot;
        }
    }

    public async IAsyncEnumerable<LogEntry> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {

        Channel<LogEntry> channel = Channel.CreateBounded<LogEntry>(_channelOptions);

        Guid subscriptionId = Guid.NewGuid();

        lock (_lock)
        {

            _writers[subscriptionId] = channel.Writer;
        }

        try
        {

            await foreach (LogEntry item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {

                yield return item;
            }
        }
        finally
        {

            lock (_lock)
            {

                if (_writers.Remove(subscriptionId, out ChannelWriter<LogEntry>? writer))
                {

                    writer.Complete();
                }
            }
        }
    }

}
