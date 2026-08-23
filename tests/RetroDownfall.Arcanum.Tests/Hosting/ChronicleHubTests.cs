using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ChronicleHubTests
{

    [Fact]
    public async Task Publish_and_subscribe_are_scoped_per_apprentice()
    {
        ChronicleHub hub = new();

        Guid apprenticeA = Guid.NewGuid();

        Guid apprenticeB = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        Task<ApprenticeEvent?> readA = ReadOneAsync(hub, apprenticeA, cts.Token);

        ApprenticeEvent evtA = new()
        {
            Type = ApprenticeEventType.ApprenticeStarted,
            ApprenticeId = apprenticeA,
            Timestamp = DateTimeOffset.UtcNow,
            Name = "A",
        };

        ApprenticeEvent evtB = new()
        {
            Type = ApprenticeEventType.ApprenticeStarted,
            ApprenticeId = apprenticeB,
            Timestamp = DateTimeOffset.UtcNow,
            Name = "B",
        };

        hub.Publish(apprenticeA, evtA);

        hub.Publish(apprenticeB, evtB);

        ApprenticeEvent? receivedA = await readA;

        Assert.NotNull(receivedA);

        Assert.Equal("A", receivedA!.Name);

    }

    [Fact]
    public void Publish_WithoutASubscriber_StrandsNoPerApprenticeHub()
    {

        ChronicleHub hub = new();

        Guid apprenticeId = Guid.NewGuid();

        // A headless run (no Studio SSE client attached) publishes every lifecycle event with nobody
        // listening; the singleton hub must not retain per-apprentice state for it.
        hub.Publish(apprenticeId, Event(apprenticeId));

        hub.Publish(apprenticeId, Event(apprenticeId));

        Assert.Equal(0, hub.TrackedApprenticeCount);

    }

    [Fact]
    public async Task Publish_AfterTheLastSubscriberDisconnects_StrandsNoPerApprenticeHub()
    {

        ChronicleHub hub = new();

        Guid apprenticeId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        await using (IAsyncEnumerator<ApprenticeEvent> chronicle =
            hub.SubscribeAsync(apprenticeId, cts.Token).GetAsyncEnumerator())
        {

            Task<bool> first = chronicle.MoveNextAsync().AsTask();

            hub.Publish(apprenticeId, Event(apprenticeId));

            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, hub.TrackedApprenticeCount);

        }

        Assert.Equal(0, hub.TrackedApprenticeCount);

        // The apprentice keeps running after the operator closes the stream.
        hub.Publish(apprenticeId, Event(apprenticeId));

        Assert.Equal(0, hub.TrackedApprenticeCount);

    }

    private static ApprenticeEvent Event(Guid apprenticeId) =>
        new()
        {
            Type = ApprenticeEventType.ApprenticeStarted,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Name = "headless",
        };

    private static async Task<ApprenticeEvent?> ReadOneAsync(
        ChronicleHub hub,
        Guid apprenticeId,
        CancellationToken ct)
    {

        await foreach (ApprenticeEvent item in hub.SubscribeAsync(apprenticeId, ct))
        {

            return item;

        }

        return null;

    }

}
