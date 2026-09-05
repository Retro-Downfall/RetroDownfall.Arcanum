using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Resilience;

/// <summary>
/// The probe scheduler's Task.Delay sat after the work inside the same try, so an exception
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

        // Waiting for the first tick rather than assuming 300ms contains it. A busy machine can
        // schedule the loop later than that, which left AccessCount at 0 and failed the lower half
        // of the range check below for a reason that had nothing to do with backing off.
        await options.FirstAccess.WaitAsync(TimeSpan.FromSeconds(30));

        // The window after the first tick is what a spin would fill. Only the upper bound depends on
        // it, so a slow machine can make this test pass for the right reason but never fail for the
        // wrong one.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Assert.InRange(AccessCount, 1, 5) alone cannot tell "backed off" apart from "faulted after
        // one tick": a regression that lets the tick's exception propagate (e.g. a stray `throw;` in
        // the scheduler's catch) would also leave AccessCount at exactly 1, forever, and still pass
        // that range check. Checking the loop is still alive closes that gap.
        Task? executeTask = service.ExecuteTask;

        Assert.NotNull(executeTask);

        Assert.False(executeTask.IsCompleted, "the scheduler loop must still be alive");

        await service.StopAsync(CancellationToken.None);

        Assert.InRange(options.AccessCount, 1, 5);

        Assert.Equal(0, probe.CallCount);

    }

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<ArcanumSettings>
    {

        private readonly TaskCompletionSource _firstAccess =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _accessCount;

        public int AccessCount => Volatile.Read(ref _accessCount);

        /// <summary>Completes when the scheduler has actually reached its first tick.</summary>
        public Task FirstAccess => _firstAccess.Task;

        public ArcanumSettings CurrentValue
        {
            get
            {

                Interlocked.Increment(ref _accessCount);

                _ = _firstAccess.TrySetResult();

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
