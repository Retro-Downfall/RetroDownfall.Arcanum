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

        ArcanumSettings settings = new()
        {
            Apprentices = new ApprenticeSettings { ChronicleChannelCapacity = 8 },
        };

        SessionEventHub hub = new(new TestOptionsMonitor<ArcanumSettings>(settings));

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

}
