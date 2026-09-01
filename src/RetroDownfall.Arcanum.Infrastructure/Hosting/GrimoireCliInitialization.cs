using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireCliInitialization(
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource,
    IServiceScopeFactory scopeFactory,
    IInstallationStartupProbe startupProbe,
    IArcanumClientMutationBoundary clientMutationBoundary) :
    IGrimoireCliInitialization,
    IGrimoireCliStoppedHostInitialization
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>
    /// Acquires exclusive installation ownership without bootstrapping, and retains it until the
    /// caller's entire operation has completed.
    /// </summary>
    /// <remarks>
    /// This boundary is only for commands that intentionally operate on local storage without a host.
    /// Host-facing commands use the authenticated API instead. Contention and unsafe lock evidence both
    /// fail closed; neither is authority to bootstrap or open the Grimoire without ownership.
    /// </remarks>
    public Task<T> RunExclusiveAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        RunExclusiveCoreAsync(
            (provider, _, token) => operation(provider, token),
            bootstrapGrimoire: false,
            cancellationToken);

    /// <summary>
    /// Acquires exclusive installation ownership, bootstraps under that exact handle, and retains it
    /// until the caller's entire operation has completed.
    /// </summary>
    public Task<T> RunExclusiveWithBootstrapAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        RunExclusiveCoreAsync(
            (provider, _, token) => operation(provider, token),
            bootstrapGrimoire: true,
            cancellationToken);

    Task<T> IGrimoireCliStoppedHostInitialization.RunAsync<T>(
        Func<IServiceProvider,
            IStoppedHostGrimoireAuthorityIssuer,
            CancellationToken,
            Task<T>> operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        return RunExclusiveCoreAsync(
            (provider, heldInstallationLock, token) =>
            {

                StoppedHostGrimoireAuthorityIssuer issuer = new(
                    heldInstallationLock,
                    ArcanumPaths.GrimoireDirectory,
                    ArcanumPaths.GrimoireDatabaseFile);

                return operation(provider, issuer, token);

            },
            bootstrapGrimoire: false,
            cancellationToken);

    }

    private async Task<T> RunExclusiveCoreAsync<T>(
        Func<IServiceProvider, ArcanumMaintenanceLock, CancellationToken, Task<T>>
            operation,
        bool bootstrapGrimoire,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ArcanumMaintenanceLockAcquisitionResult acquisition =
                ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

            if (acquisition.Disposition
                is ArcanumMaintenanceLockAcquisitionDisposition.Unsafe)
            {

                throw new InvalidOperationException(
                    "The Arcanum maintenance lock could not be acquired safely because its topology, identity, or owner-only permissions could not be validated.");

            }

            if (acquisition.Disposition
                is ArcanumMaintenanceLockAcquisitionDisposition.Contended)
            {

                throw new InvalidOperationException(
                    "The exclusive Grimoire operation cannot begin while a running host, backup restore, or installation reset owns the maintenance lock.");

            }

            using ArcanumMaintenanceLock cliLock = acquisition.BorrowAcquiredLock();

            ArcanumClientMutationResult<T> mutation = await clientMutationBoundary
                .RunAsync(
                    token => RunUnderBothLocksAsync(
                        operation,
                        bootstrapGrimoire,
                        acquisition,
                        cliLock,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!mutation.IsCompleted)
            {

                throw new InvalidOperationException(
                    mutation.Disposition is ArcanumClientMutationDisposition.Blocked
                        ? "The exclusive Grimoire operation is blocked by active client mutation or installation maintenance evidence. "
                            + mutation.Error.Message
                        : "The exclusive Grimoire operation could not validate client-mutation coordination safely. "
                            + mutation.Error.Message);

            }

            return mutation.Value;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<T> RunUnderBothLocksAsync<T>(
        Func<IServiceProvider, ArcanumMaintenanceLock, CancellationToken, Task<T>>
            operation,
        bool bootstrapGrimoire,
        ArcanumMaintenanceLockAcquisitionResult acquisition,
        ArcanumMaintenanceLock cliLock,
        CancellationToken cancellationToken)
    {

        GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(
            cliLock,
            ArcanumPaths.GrimoireDirectory);

        Result<ActiveInstallationReset?> activeRead = await startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            throw new InvalidOperationException(
                "Installation reset recovery state could not be read safely. "
                + activeRead.Error.Message);

        }

        if (activeRead.Value is not null)
        {

            throw new InvalidOperationException(
                "An installation factory reset is active. Resume it before running a direct Grimoire operation.");

        }

        if (bootstrapGrimoire)
        {

            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(
                    secretStore,
                    passphraseSource,
                    scopeFactory,
                    ArcanumPaths.GrimoireDatabaseFile,
                    ArcanumPaths.GrimoireDirectory,
                    cliLock,
                    expectedInstallationId: null,
                    postRestoreTopology: _ =>
                    {

                        ArcanumMaintenanceLock borrowed =
                            acquisition.BorrowAcquiredLock();

                        if (!ReferenceEquals(borrowed, cliLock))
                        {

                            throw new InvalidOperationException(
                                "Post-restore CLI bootstrap requires the exact acquired maintenance lock.");

                        }

                        GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(
                            borrowed,
                            ArcanumPaths.GrimoireDirectory);

                        return Task.CompletedTask;

                    },
                    cancellationToken)
                .ConfigureAwait(false);

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        return await operation(scope.ServiceProvider, cliLock, cancellationToken)
            .ConfigureAwait(false);

    }
}
