using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireCliInitialization(
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource,
    IServiceScopeFactory scopeFactory) : IGrimoireCliInitialization
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private volatile bool _completed;

    /// <summary>
    /// Bootstraps the Grimoire for a CLI invocation, taking the installation lock only if it is free.
    /// </summary>
    /// <remarks>
    /// Two arms, both deliberate. If the lock is acquired the CLI is the sole owner of this
    /// installation and runs the full bootstrap under it, including Covenant authority preparation.
    /// If it cannot be acquired a live host holds it, and that host already prepared authority at its
    /// own startup, so the CLI proceeds without the lock and without repeating that work.
    ///
    /// <para>Hard-failing on the second arm would be a regression, not a safety improvement.
    /// <c>arcanum ask</c> and <c>arcanum run</c> are expected to work while the host is running, and
    /// they already do so safely through WAL journaling plus <c>busy_timeout</c> rather than through
    /// exclusive ownership. Refusing to start because a host exists would break the ordinary case to
    /// protect a case the concurrency model already covers.</para>
    ///
    /// <para>The first arm therefore also owns interrupted-restore recovery. Both phases run inside the
    /// bootstrap under this lock, and a restore whose topology or authority cannot be proved throws out
    /// of here rather than completing: a CLI that reported success would be a second surface publishing
    /// readiness on a replacement nobody revalidated (§10.19.8).</para>
    /// </remarks>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_completed)
            {
                return;
            }

            using ArcanumMaintenanceLock? cliLock =
                ArcanumMaintenanceLock.TryAcquire(ArcanumPaths.GrimoireDirectory);

            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(
                    secretStore,
                    passphraseSource,
                    scopeFactory,
                    cliLock,
                    cancellationToken)
                .ConfigureAwait(false);

            _completed = true;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
