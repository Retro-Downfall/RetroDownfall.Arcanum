using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class CampaignLoggerQueue : ICampaignLoggerQueue
{

    private const int Capacity = 100;

    private readonly ILogger<CampaignLoggerQueue> _logger;

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

    public async ValueTask QueueAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (_reader.Count >= Capacity)
        {
            _logger.LogWarning(
                "Campaign Logger queue is full ({Capacity} items); waiting for backpressure.",
                Capacity);
        }

        await _writer.WriteAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _reader.ReadAllAsync(cancellationToken);

}
