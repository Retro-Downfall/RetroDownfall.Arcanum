using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
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
