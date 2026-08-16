using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one thing that connects <c>Arcanum:Features:Covenant</c> to the in-memory availability
/// snapshot every Covenant gate reads.
/// </summary>
/// <remarks>
/// Memory only, deliberately. A disable that had to reach SQLCipher to take effect would be
/// unavailable exactly when an operator most wants it — a locked, degraded, or key-less database —
/// and "turn it off" is the one control that must never fail closed in the permissive direction.
/// The gate reads a field; this writes that field; nothing between them touches storage or a secret
/// (§10.18).
///
/// <para>Callbacks are serialized and each one republishes the monitor's <em>current</em> value
/// rather than the value it was handed. <see cref="IOptionsMonitor{TOptions}"/> promises no ordering,
/// and two rapid edits delivered out of order would otherwise leave the installation enabled after
/// the operator's last action was to disable it. Reading the current value under the lock makes the
/// last observed configuration win regardless of delivery order.</para>
///
/// <para>Disable becomes visible at the next in-memory gate read. A turn already in flight aborts at
/// its required pre-dispatch revalidation rather than being cancelled from here, because a provider
/// call that has already left the process cannot be recalled by a configuration edit.</para>
/// </remarks>
internal sealed class CovenantFeatureConfigurationPublisher : IHostedService, IDisposable
{

    private readonly CovenantAvailability _availability;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly Lock _gate = new();

    private IDisposable? _subscription;

    private bool _disposed;

    public CovenantFeatureConfigurationPublisher(
        CovenantAvailability availability,
        IOptionsMonitor<ArcanumSettings> options)
    {

        ArgumentNullException.ThrowIfNull(availability);

        ArgumentNullException.ThrowIfNull(options);

        _availability = availability;

        _options = options;

    }

    /// <summary>
    /// Publishes the configured value once, then keeps publishing on every later change.
    /// </summary>
    /// <remarks>
    /// The initial publication is not redundant with the snapshot's <c>false</c> default. An
    /// installation that starts with the feature already enabled has to reach an enabled snapshot
    /// without an edit, and a publisher that only reacted to changes would leave it disabled until
    /// somebody happened to touch <c>arcanum.json</c>.
    /// </remarks>
    public void Start()
    {

        lock (_gate)
        {

            if (_disposed || _subscription is not null)
            {

                return;

            }

            _subscription = _options.OnChange((_, _) => Republish());

        }

        Republish();

    }

    public Task StartAsync(CancellationToken cancellationToken)
    {

        Start();

        return Task.CompletedTask;

    }

    public Task StopAsync(CancellationToken cancellationToken)
    {

        Dispose();

        return Task.CompletedTask;

    }

    public void Dispose()
    {

        IDisposable? subscription;

        lock (_gate)
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            subscription = _subscription;

            _subscription = null;

        }

        subscription?.Dispose();

    }

    private void Republish()
    {

        lock (_gate)
        {

            if (_disposed)
            {

                return;

            }

            _ = _availability.PublishFeatureEnabled(_options.CurrentValue.Features.Covenant);

        }

    }

}
