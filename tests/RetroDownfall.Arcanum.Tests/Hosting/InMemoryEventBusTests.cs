using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class InMemoryEventBusTests
{

    [Fact]
    public async Task Publish_delivers_events_to_active_subscriber()
    {

        ArcanumSettings settings = new() { EventBus = new EventBusSettings { ChannelCapacity = 8 } };

        InMemoryEventBus bus = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        using CancellationTokenSource cts = new();

        Task<List<DaemonEvent>> consumeTask = ConsumeAsync(bus, cts.Token);

        DaemonEvent evt = new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            "job",
            "spell",
            DaemonEventType.Started,
            "started");

        bus.Publish(evt);

        await Task.Delay(50);

        cts.Cancel();

        List<DaemonEvent> received = await consumeTask;

        Assert.Contains(received, e => e.Message == "started");

    }

    private static async Task<List<DaemonEvent>> ConsumeAsync(InMemoryEventBus bus, CancellationToken ct)
    {

        List<DaemonEvent> events = [];

        try
        {

            await foreach (DaemonEvent item in bus.Subscribe<DaemonEvent>(ct))
            {

                events.Add(item);

            }
        }
        catch (OperationCanceledException)
        {
        }

        return events;

    }

}
