using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantPacerTests
{
    [Fact]
    public async Task SetDynamicInterval_clamps_value_and_publishes_event()
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

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(settings), CreateScopeFactory(), NullLogger<UnseenServantPacer>.Instance);

        await pacer.SetDynamicIntervalAsync("watch", intervalMinutes: 99999);

        UnseenServantJob job = new() { Name = "watch", IntervalMinutes = 10, TargetSpell = "patrol" };

        int effective = pacer.GetEffectiveInterval(job);

        Assert.True(effective < 99999);

        Assert.Single(bus.Published);

        Assert.Equal(DaemonEventType.IntervalChanged, bus.Published[0].EventType);
    }

    [Fact]
    public async Task GetEffectiveInterval_prefers_composite_override()
    {
        FakeEventBus bus = new();

        ArcanumSettings settings = new()
        {
            Daemon = new DaemonSettings
            {
                Jobs =
                [
                    new UnseenServantJob { Name = "scout", TargetSpell = "look" },
                ],
            },
        };

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(settings), CreateScopeFactory(), NullLogger<UnseenServantPacer>.Instance);

        await pacer.SetDynamicIntervalAsync("scout", intervalMinutes: 15);

        UnseenServantJob job = new() { Name = "scout", IntervalMinutes = 60, TargetSpell = "look" };

        Assert.Equal(15, pacer.GetEffectiveInterval(job));
    }

    [Fact]
    public async Task SetDynamicInterval_is_a_no_op_for_a_job_not_in_configuration()
    {
        FakeEventBus bus = new();

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), CreateScopeFactory(), NullLogger<UnseenServantPacer>.Instance);

        await pacer.SetDynamicIntervalAsync("unconfigured", intervalMinutes: 15);

        UnseenServantJob job = new() { Name = "unconfigured", IntervalMinutes = 60, TargetSpell = "look" };

        Assert.Equal(60, pacer.GetEffectiveInterval(job));

        Assert.Empty(bus.Published);
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        ServiceCollection services = new();

        ServiceProvider provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IServiceScopeFactory>();
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

    /// <summary>
    /// The pacer cannot apply an override for a name absent from Arcanum:Daemon:Jobs, so it must say
    /// so — <c>adjust_initiative</c> reported success for a job it never touched.
    /// </summary>
    [Fact]
    public async Task SetDynamicInterval_reports_failure_for_a_job_not_in_configuration()
    {
        FakeEventBus bus = new();

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), CreateScopeFactory(), NullLogger<UnseenServantPacer>.Instance);

        Assert.False(await pacer.SetDynamicIntervalAsync("unconfigured", intervalMinutes: 15));
    }

    [Fact]
    public async Task SetDynamicInterval_reports_success_for_a_configured_job()
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

        UnseenServantPacer pacer = new(bus, new TestOptionsMonitor<ArcanumSettings>(settings), CreateScopeFactory(), NullLogger<UnseenServantPacer>.Instance);

        Assert.True(await pacer.SetDynamicIntervalAsync("watch", intervalMinutes: 15));
    }
}
