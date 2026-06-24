using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantPacerTests
{

    [Fact]
    public void SetDynamicInterval_clamps_value_and_publishes_event()
    {

        FakeEventBus bus = new();

        ArcanumSettings settings = new()
        {
            Daemon = new DaemonSettings
            {
                Jobs =
                [
                    new UnseenServantJob { Name = "watch", TargetSpell = "patrol" },
                ],
            },
        };

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(settings));

        pacer.SetDynamicInterval("watch", intervalMinutes: 99999);

        UnseenServantJob job = new() { Name = "watch", IntervalMinutes = 10, TargetSpell = "patrol" };

        int effective = pacer.GetEffectiveInterval(job);

        Assert.True(effective < 99999);

        Assert.Single(bus.Published);

        Assert.Equal(DaemonEventType.IntervalChanged, bus.Published[0].EventType);

    }

    [Fact]
    public void GetEffectiveInterval_prefers_override_by_job_name()
    {

        FakeEventBus bus = new();

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        pacer.SetDynamicInterval("scout", intervalMinutes: 15);

        UnseenServantJob job = new() { Name = "scout", IntervalMinutes = 60, TargetSpell = "look" };

        Assert.Equal(15, pacer.GetEffectiveInterval(job));

    }

    private sealed class FakeEventBus : IEventBus
    {

        public List<DaemonEvent> Published { get; } = [];

        public void Publish<T>(T @event) where T : notnull
        {

            if (@event is DaemonEvent daemonEvent)
            {

                Published.Add(daemonEvent);

            }

        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
