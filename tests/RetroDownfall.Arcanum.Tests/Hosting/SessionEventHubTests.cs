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

        // Starting the enumerator registers the subscription synchronously, before MoveNextAsync's task
        // is awaited, so the publish below cannot race ahead of it.
        await using IAsyncEnumerator<Entry> entries = hub
            .SubscribeAsync(sessionId, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Task<bool> first = entries.MoveNextAsync().AsTask();

        hub.Publish(sessionId, MakeEntry(sessionId, "hello"));

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("hello", entries.Current.Content);

    }

    /// <summary>
    /// The invariant <c>ChronicleHub</c> already pins twice: a publisher must never mint a per-session
    /// hub. A hub is removed only when its last subscriber leaves, so one created by a publisher has no
    /// removal path at all and lives for the lifetime of the singleton — and the overwhelmingly common
    /// turn (<c>arcanum run</c>, <c>/v1/chat/completions</c>, a daemon job, an Apprentice step) has no SSE
    /// client on <c>GET /api/sessions/{id}/stream</c> at all.
    /// </summary>
    [Fact]
    public void Publish_WithoutASubscriber_StrandsNoPerSessionHub()
    {

        SessionEventHub hub = new(NullLogger<SessionEventHub>.Instance);

        Guid sessionId = Guid.NewGuid();

        hub.Publish(sessionId, MakeEntry(sessionId, "first"));

        hub.Publish(sessionId, MakeEntry(sessionId, "second"));

        Assert.Equal(0, hub.TrackedSessionCount);

    }

    [Fact]
    public async Task Publish_AfterTheLastSubscriberDisconnects_StrandsNoPerSessionHub()
    {

        SessionEventHub hub = new(NullLogger<SessionEventHub>.Instance);

        Guid sessionId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        await using (IAsyncEnumerator<Entry> entries = hub
            .SubscribeAsync(sessionId, cts.Token)
            .GetAsyncEnumerator(cts.Token))
        {

            Task<bool> first = entries.MoveNextAsync().AsTask();

            hub.Publish(sessionId, MakeEntry(sessionId, "live"));

            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, hub.TrackedSessionCount);

        }

        Assert.Equal(0, hub.TrackedSessionCount);

        hub.Publish(sessionId, MakeEntry(sessionId, "after the operator closed the stream"));

        Assert.Equal(0, hub.TrackedSessionCount);

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
