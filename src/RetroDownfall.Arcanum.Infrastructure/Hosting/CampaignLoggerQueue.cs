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

    private readonly Channel<Guid> _resignalChannel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly ChannelWriter<Guid> _writer;

    private readonly ChannelReader<Guid> _reader;

    private readonly ChannelWriter<Guid> _resignalWriter;

    private readonly ChannelReader<Guid> _resignalReader;

    public CampaignLoggerQueue(ILogger<CampaignLoggerQueue> logger)
    {
        _logger = logger;

        _writer = _channel.Writer;

        _reader = _channel.Reader;

        _resignalWriter = _resignalChannel.Writer;

        _resignalReader = _resignalChannel.Reader;
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
        Task<bool>? pendingResignal = null;

        Task<bool>? pendingNormal = null;

        while (true)
        {
            while (_resignalReader.TryRead(out Guid resignalId))
            {
                yield return resignalId;
            }

            if (_reader.TryRead(out Guid normalId))
            {
                yield return normalId;

                continue;
            }

            pendingResignal ??= _resignalReader.WaitToReadAsync(cancellationToken).AsTask();

            pendingNormal ??= _reader.WaitToReadAsync(cancellationToken).AsTask();

            Task completed = await Task.WhenAny(pendingResignal, pendingNormal).ConfigureAwait(false);

            if (completed == pendingResignal)
            {
                _ = await pendingResignal.ConfigureAwait(false);

                pendingResignal = null;
            }

            if (completed == pendingNormal)
            {
                _ = await pendingNormal.ConfigureAwait(false);

                pendingNormal = null;
            }
        }
    }

    /// <summary>Releases the coalescing marker after the consumer reaches a terminal outcome.</summary>
    internal bool Conclude(Guid sessionId) => _pending.TryRemove(sessionId, out _);

    /// <summary>Returns the sole consumer's retained identity through its priority lane.</summary>
    internal bool TryResignalHeld(Guid sessionId) =>
        _pending.ContainsKey(sessionId) && _resignalWriter.TryWrite(sessionId);

    /// <summary>Pending coalescing marker count for assertions in the test suite.</summary>
    internal int PendingCountForTesting => _pending.Count;

    /// <summary>Fills the channel without going through coalescing, for capacity tests.</summary>
    internal bool TryWriteRawForTesting(Guid conversationId) => _writer.TryWrite(conversationId);
}
