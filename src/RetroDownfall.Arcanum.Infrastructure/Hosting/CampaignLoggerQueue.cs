using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class CampaignLoggerQueue
{

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ChannelWriter<Guid> _writer;

    private readonly ChannelReader<Guid> _reader;

    public CampaignLoggerQueue()
    {
        _writer = _channel.Writer;

        _reader = _channel.Reader;
    }

    public ValueTask QueueAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        _writer.WriteAsync(conversationId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _reader.ReadAllAsync(cancellationToken);

}
