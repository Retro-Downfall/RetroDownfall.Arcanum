using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Resilience;

/// <summary>
/// W8-7: the probe scheduler's Task.Delay sat after the work inside the same try, so an exception
/// thrown before the delay was reached looped immediately with no backoff.
/// </summary>
public sealed class ProviderHealthProbeServiceTests
{

    /// <summary>
    /// Before this fix, an exception from <c>options.CurrentValue</c> — read before any per-provider
    /// try/catch, on the very first line of the probed work — was caught, logged, and immediately
    /// re-entered the loop with no delay at all. A hot spin would reach <c>CurrentValue</c> thousands
    /// of times in the short window this test allows; a proper backoff reaches it once and then
    /// sleeps for the shortest clamped interval, five real seconds.
    /// </summary>
    [Fact]
    public async Task A_tick_that_throws_before_probing_still_backs_off_instead_of_spinning()
    {

        ThrowingOptionsMonitor options = new();

        ProviderHealthTracker tracker = new(NullLogger<ProviderHealthTracker>.Instance);

        NeverCalledProbe probe = new();

        using ProviderHealthProbeService service = new(
            options,
            probe,
            tracker,
            NullLogger<ProviderHealthProbeService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await service.StopAsync(CancellationToken.None);

        Assert.InRange(options.AccessCount, 1, 5);

        Assert.Equal(0, probe.CallCount);

    }

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<ArcanumSettings>
    {

        private int _accessCount;

        public int AccessCount => Volatile.Read(ref _accessCount);

        public ArcanumSettings CurrentValue
        {
            get
            {

                Interlocked.Increment(ref _accessCount);

                throw new InvalidOperationException("Synthetic options failure for W8-7.");

            }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

    }

    /// <summary>Never reached: the throw happens on the first line of ProbeAllProvidersAsync.</summary>
    private sealed class NeverCalledProbe : IProviderHealthProbe
    {

        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _callCount);

            return Task.FromResult(true);

        }

    }

}
