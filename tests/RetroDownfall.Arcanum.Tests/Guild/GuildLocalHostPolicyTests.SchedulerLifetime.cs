using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Local_scheduler_discovery_fault_cancels_and_joins_its_waiting_pump()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        DiscoveryFaultOptions options = new(f.Settings);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), options,
            f.Connections, new LongRunningOperationOwnership(), new ArmingDiscoveryClock(options),
            NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await scheduler.StartAsync(default);

                await scheduler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            });

            Assert.Equal("discovery options failed", failure.Message);

            Assert.True(scheduler.ExecuteTask!.IsCompleted);

            Assert.False(scheduler.IsAvailable);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class DiscoveryFaultOptions(IOptionsMonitor<ArcanumSettings> inner) : IOptionsMonitor<ArcanumSettings>
    {
        internal bool Armed;

        public ArcanumSettings CurrentValue => Armed ? throw new InvalidOperationException("discovery options failed") : inner.CurrentValue;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => inner.OnChange(listener);
    }

    private sealed class ArmingDiscoveryClock(DiscoveryFaultOptions options) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Keep PeriodicTimer fully constructible; fail discovery's first options read,
            // rather than leaving a framework timer partially constructed for finalization.
            ITimer timer = TimeProvider.System.CreateTimer(callback, state, dueTime, period);

            options.Armed = true;

            return timer;
        }
    }
}
