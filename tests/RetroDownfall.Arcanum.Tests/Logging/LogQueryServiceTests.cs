using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Tests.Logging;

public sealed class LogQueryServiceTests
{

    [Fact]
    public async Task QueryAsync_applies_filters_and_pagination()
    {

        FakeLogRingBuffer buffer = new(
        [
            MakeEntry(1, Core.Logging.LogLevel.Warning, "daemon", "alpha message"),
            MakeEntry(2, Core.Logging.LogLevel.Information, "api", "beta message"),
            MakeEntry(3, Core.Logging.LogLevel.Error, "daemon", "gamma message"),
        ]);

        LogQueryService service = new(buffer);

        LogQueryRequest request = new(
            MinLevel: Core.Logging.LogLevel.Warning,
            Category: "daemon",
            Search: "gamma",
            Limit: 10);

        LogQueryResult result = await service.QueryAsync(request, CancellationToken.None);

        Assert.Single(result.Entries);

        Assert.Equal("gamma message", result.Entries[0].Message);

        Assert.False(result.HasMore);

    }

    [Fact]
    public async Task StreamAsync_filters_live_entries()
    {

        FakeLogRingBuffer buffer = new([]);

        LogQueryService service = new(buffer);

        using CancellationTokenSource cts = new();

        Task<List<LogEntry>> readTask = ReadFilteredAsync(service, cts.Token);

        buffer.Push(MakeEntry(10, Core.Logging.LogLevel.Information, "api", "keep me"));

        buffer.Push(MakeEntry(11, Core.Logging.LogLevel.Information, "other", "drop me"));

        await Task.Delay(50);

        cts.Cancel();

        List<LogEntry> received = await readTask;

        Assert.Single(received);

        Assert.Equal("keep me", received[0].Message);

    }

    private static LogEntry MakeEntry(long sequence, Core.Logging.LogLevel level, string category, string message) =>
        new(sequence, DateTimeOffset.UtcNow, level, category, message, null, null, null, []);

    private static async Task<List<LogEntry>> ReadFilteredAsync(LogQueryService service, CancellationToken ct)
    {

        List<LogEntry> entries = [];

        LogQueryRequest request = new(Category: "api");

        try
        {

            await foreach (LogEntry item in service.StreamAsync(request, ct))
            {

                entries.Add(item);

            }
        }
        catch (OperationCanceledException)
        {
        }

        return entries;

    }

    private sealed class FakeLogRingBuffer(IReadOnlyList<LogEntry> initial) : ILogRingBuffer
    {

        private readonly List<LogEntry> _entries = initial.ToList();

        private readonly List<ChannelSubscription> _subscriptions = [];

        public void Write(LogEntry entry)
        {

            _entries.Add(entry);

            foreach (ChannelSubscription sub in _subscriptions.ToArray())
            {

                _ = sub.Writer.TryWrite(entry);

            }

        }

        public void Push(LogEntry entry) => Write(entry);

        public IReadOnlyList<LogEntry> GetSnapshot() => _entries.ToArray();

        public async IAsyncEnumerable<LogEntry> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {

            System.Threading.Channels.Channel<LogEntry> channel =
                System.Threading.Channels.Channel.CreateBounded<LogEntry>(new System.Threading.Channels.BoundedChannelOptions(8)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            _subscriptions.Add(new ChannelSubscription(channel.Writer));

            await foreach (LogEntry item in channel.Reader.ReadAllAsync(ct))
            {

                yield return item;

            }

        }

        private sealed record ChannelSubscription(System.Threading.Channels.ChannelWriter<LogEntry> Writer);

    }

}
