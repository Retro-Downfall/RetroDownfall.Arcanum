using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class CampaignLoggerQueue : ICampaignLoggerQueue
{

    private const int Capacity = 100;

    private readonly ILogger<CampaignLoggerQueue> _logger;

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ChannelWriter<Guid> _writer;

    private readonly ChannelReader<Guid> _reader;

    public CampaignLoggerQueue(ILogger<CampaignLoggerQueue> logger)
    {

        _logger = logger;

        _writer = _channel.Writer;

        _reader = _channel.Reader;

    }

    /// <inheritdoc />
    public bool TryQueue(Guid conversationId)
    {

        if (!_pending.TryAdd(conversationId, 0))
        {

            // Already pending — coalesce as success so producers never block and duplicates collapse.
            return true;

        }

        if (_writer.TryWrite(conversationId))
        {

            return true;

        }

        _pending.TryRemove(conversationId, out _);

        _logger.LogWarning(
            "Campaign Logger queue rejected session {SessionId}; capacity {Capacity} exhausted. Session remains eligible for a later sweep.",
            conversationId,
            Capacity);

        return false;

    }

    public async IAsyncEnumerable<Guid> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await foreach (Guid id in _reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {

            _pending.TryRemove(id, out _);

            yield return id;

        }

    }

    /// <summary>Pending coalescing marker count for assertions in the test suite.</summary>
    internal int PendingCountForTesting => _pending.Count;

    /// <summary>Fills the channel without going through coalescing, for capacity tests.</summary>
    internal bool TryWriteRawForTesting(Guid conversationId) => _writer.TryWrite(conversationId);

}
