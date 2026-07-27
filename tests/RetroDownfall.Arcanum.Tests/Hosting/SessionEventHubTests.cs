using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class SessionEventHubTests
{

    [Fact]
    public async Task Publish_delivers_entry_to_session_subscriber()
    {
        SessionEventHub hub = new(NullLogger<SessionEventHub>.Instance);

        Guid sessionId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        Task<Entry?> readTask = ReadOneAsync(hub, sessionId, cts.Token);

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Content = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        hub.Publish(sessionId, entry);

        Entry? received = await readTask;

        Assert.NotNull(received);

        Assert.Equal("hello", received!.Content);

    }

    private static async Task<Entry?> ReadOneAsync(
        SessionEventHub hub,
        Guid sessionId,
        CancellationToken ct)
    {

        await foreach (Entry item in hub.SubscribeAsync(sessionId, ct))
        {

            return item;

        }

        return null;

    }

    [Fact]
    public async Task Publish_logs_warning_when_subscriber_channel_is_full()
    {
        CapturingLogger<SessionEventHub> logger = new();

        SessionEventHub hub = new(logger);

        Guid sessionId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        IAsyncEnumerator<Entry> enumerator = hub
            .SubscribeAsync(sessionId, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        try
        {

            Task<bool> firstMove = enumerator.MoveNextAsync().AsTask();

            hub.Publish(sessionId, MakeEntry(sessionId, "seed"));

            Assert.True(await firstMove);

            int capacity = ArcanumSettingClamps.ChronicleChannelCapacity(
                ArcanumRuntimeDefaults.Apprentices.ChronicleChannelCapacity);
            for (int i = 0; i < capacity; i++)
            {

                hub.Publish(sessionId, MakeEntry(sessionId, i.ToString()));

            }

            hub.Publish(sessionId, MakeEntry(sessionId, "overflow"));

            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("dropped 1 event(s)", StringComparison.Ordinal)
                    && e.Message.Contains(sessionId.ToString(), StringComparison.Ordinal));

        }

        finally
        {

            await enumerator.DisposeAsync();

            cts.Dispose();

        }

    }

    private static Entry MakeEntry(Guid sessionId, string content) => new()
    {

        Id = Guid.NewGuid(),

        SessionId = sessionId,

        Content = content,

        CreatedAt = DateTimeOffset.UtcNow,

    };

    private sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {

        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {

            get
            {

                lock (_entries)
                {

                    return _entries.ToList();

                }

            }

        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            lock (_entries)
            {

                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));

            }

        }

        private sealed class NoopDisposable : IDisposable
        {

            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }

        }

    }

    private sealed record LogEntry(LogLevel Level, string Message);

}
