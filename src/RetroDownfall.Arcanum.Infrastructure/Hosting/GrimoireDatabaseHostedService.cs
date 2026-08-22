using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal static class InstallationResetHostStartupAdmission
{

    public static bool AllowsRecoveryHost(
        ActiveInstallationReset active) =>
        active.Scope is InstallationResetScope.Global or InstallationResetScope.All
        && active.Phase is InstallationResetPhase.Prepared
        && active.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure
        && !active.OnlineDataCompletionDurable;

}

[ExcludeFromCodeCoverage] // Reason: IHostedService DB bootstrap
public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource,
    IInstallationStartupProbe? startupProbe = null)
    : IHostedService, IDisposable
{

    private readonly IInstallationStartupProbe _startupProbe =
        startupProbe ?? InstallationStartupProbe.CreateDefault();

    private InstallationResetMaintenanceLockAccessor _maintenanceLockAccessor = new();

    private IInstallationResetStartupRecovery? _startupRecovery;

    private InstallationResetApiAdmission _apiAdmission = new();

    private HostLockSerilogFileSink? _fileSink;

    private Func<CancellationToken, Task<string?>>? _masterKeyBootstrap;

    private string? _generatedMasterApiKey;

    private Func<IDisposable?>? _postTopologyStartupAction;

    private IDisposable? _postTopologyStartupLease;

    private InstallationMaintenanceCoordination? _startupCoordination;

    private InstallationStartupCoordinationLease? _startupCoordinationLease;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private readonly object _maintenanceLockSync = new();

    private ArcanumMaintenanceLock? _maintenanceLock;

    private bool _maintenanceLockAttached;

    private bool _startedSuccessfully;

    private string _maintenanceDirectory = ArcanumPaths.GrimoireDirectory;

    private string _databasePath = ArcanumPaths.GrimoireDatabaseFile;

    internal GrimoireDatabaseHostedService(
        IServiceScopeFactory scopeFactory,
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        string maintenanceDirectory,
        IInstallationStartupProbe? startupProbe = null)
        : this(scopeFactory, secretStore, passphraseSource, startupProbe)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceDirectory);

        _maintenanceDirectory = maintenanceDirectory;

        _databasePath = Path.Combine(
            maintenanceDirectory,
            Path.GetFileName(ArcanumPaths.GrimoireDatabaseFile));

    }

    internal GrimoireDatabaseHostedService(
        IServiceScopeFactory scopeFactory,
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        string maintenanceDirectory,
        InstallationResetMaintenanceLockAccessor maintenanceLockAccessor,
        IInstallationResetStartupRecovery startupRecovery,
        InstallationResetApiAdmission? apiAdmission = null,
        InstallationMaintenanceCoordination? startupCoordination = null)
        : this(
            scopeFactory,
            secretStore,
            passphraseSource,
            maintenanceDirectory,
            startupProbe: null)
    {

        _maintenanceLockAccessor = maintenanceLockAccessor
            ?? throw new ArgumentNullException(nameof(maintenanceLockAccessor));

        _startupRecovery = startupRecovery
            ?? throw new ArgumentNullException(nameof(startupRecovery));

        _apiAdmission = apiAdmission ?? new InstallationResetApiAdmission();

        _startupCoordination = startupCoordination;

    }

    internal GrimoireDatabaseHostedService(
        IServiceScopeFactory scopeFactory,
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        string maintenanceDirectory,
        InstallationResetMaintenanceLockAccessor maintenanceLockAccessor,
        IInstallationResetStartupRecovery startupRecovery,
        HostLockSerilogFileSink fileSink,
        Func<CancellationToken, Task<string?>> masterKeyBootstrap,
        InstallationResetApiAdmission? apiAdmission = null,
        InstallationMaintenanceCoordination? startupCoordination = null)
        : this(
            scopeFactory,
            secretStore,
            passphraseSource,
            maintenanceDirectory,
            maintenanceLockAccessor,
            startupRecovery,
            apiAdmission)
    {

        _fileSink = fileSink
            ?? throw new ArgumentNullException(nameof(fileSink));

        _masterKeyBootstrap = masterKeyBootstrap
            ?? throw new ArgumentNullException(nameof(masterKeyBootstrap));

        _startupCoordination = startupCoordination;

    }

    public string? TakeGeneratedMasterApiKey() =>
        Interlocked.Exchange(ref _generatedMasterApiKey, null);

    public void ConfigurePostTopologyStartupAction(
        Func<IDisposable?> startupAction)
    {

        ArgumentNullException.ThrowIfNull(startupAction);

        lock (_maintenanceLockSync)
        {

            if (_maintenanceLock is not null)
            {

                throw new InvalidOperationException(
                    "Post-topology startup actions must be configured before the host starts.");

            }

            if (_postTopologyStartupAction is not null)
            {

                throw new InvalidOperationException(
                    "A post-topology startup action is already configured.");

            }

            _postTopologyStartupAction = startupAction;

        }

    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            await StartUnderLifecycleGateAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _lifecycleGate.Release();

        }

    }

    private async Task StartUnderLifecycleGateAsync(CancellationToken cancellationToken)
    {
        // Held for the host's lifetime so installation-wide maintenance cannot overlap hosted
        // writers. Startup fails closed when another process owns the lock.
        ArcanumMaintenanceLockAcquisitionResult acquisition;

        bool alreadyStarted;

        try
        {

            acquisition = TryAcquireAndAttachHostLock(out alreadyStarted);

        }
        catch (Exception exception)
        {

            await MarkFailedAsync(exception).ConfigureAwait(false);

            throw;

        }

        if (alreadyStarted)
        {

            throw new InvalidOperationException(
                "The Grimoire database hosted service is already started.");

        }

        if (acquisition.Disposition
            is not ArcanumMaintenanceLockAcquisitionDisposition.Acquired)
        {

            InvalidOperationException exception = acquisition.Disposition
                is ArcanumMaintenanceLockAcquisitionDisposition.Contended
                ? new InvalidOperationException(
                    "The Arcanum maintenance lock is held by a reset, restore, or another host.")
                : new InvalidOperationException(
                    "The Arcanum maintenance lock topology, identity, or owner-only permissions could not be validated safely.");

            await MarkFailedAsync(exception).ConfigureAwait(false);

            throw exception;

        }

        ArcanumMaintenanceLock heldInstallationLock =
            acquisition.BorrowAcquiredLock();

        InstallationStartupCoordinationLease? startupCoordinationLease = null;

        try
        {

            GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(
                heldInstallationLock,
                _maintenanceDirectory);

            if (_startupCoordination is not null)
            {

                InstallationStartupCoordinationResult coordinated =
                    await _startupCoordination
                        .AcquireHostStartupAsync(
                            heldInstallationLock,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (coordinated.Disposition
                    is not InstallationStartupCoordinationDisposition.Acquired)
                {

                    throw new InvalidOperationException(
                        coordinated.Disposition
                            is InstallationStartupCoordinationDisposition.Contended
                            ? "Host startup is blocked by another client mutation or installation maintenance operation. "
                                + coordinated.Error.Message
                            : "Host startup could not validate client-mutation coordination safely. "
                                + coordinated.Error.Message);

                }

                startupCoordinationLease = coordinated.BorrowAcquiredLease();

            }

            Result<ActiveInstallationReset?> activeRead;

            Guid? expectedInstallationId = null;

            if (_startupRecovery is not null)
            {

                Result<InstallationResetStartupRecoveryState> recovered = await _startupRecovery
                    .RecoverBeforeBootstrapAsync(heldInstallationLock, cancellationToken)
                    .ConfigureAwait(false);

                activeRead = recovered.IsSuccess
                    ? Result<ActiveInstallationReset?>.Success(recovered.Value.ActiveReset)
                    : Result<ActiveInstallationReset?>.Failure(recovered.Error);

                if (recovered.IsSuccess)
                {

                    expectedInstallationId = recovered.Value.ExpectedInstallationId;

                }

            }
            else
            {

                activeRead = await _startupProbe
                    .ReadActiveResetAsync(cancellationToken)
                    .ConfigureAwait(false);

            }

            if (activeRead.IsFailure)
            {

                throw new InvalidOperationException(
                    "Installation reset recovery state could not be read safely. "
                    + activeRead.Error.Message);

            }

            if (activeRead.Value is { } active
                && !InstallationResetHostStartupAdmission.AllowsRecoveryHost(active))
            {

                throw new InvalidOperationException(
                    "An installation factory reset is active. Resume it before starting the host.");

            }

            // The one lock this process owns, borrowed rather than re-acquired. Nesting a second
            // FileShare.None acquisition inside the startup the first one guards would deadlock.
            //
            // Interrupted-restore recovery runs inside this call rather than before it. Both phases
            // need the guarded directory the bootstrap was given, and the authenticated one has to run
            // before the live root is created — which is a decision only the bootstrapper is in a
            // position to sequence (§10.19.8).
            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(
                    secretStore,
                    passphraseSource,
                    scopeFactory,
                    _databasePath,
                    _maintenanceDirectory,
                    heldInstallationLock,
                    expectedInstallationId,
                    token => ActivatePostRestoreTopologyAsync(
                        heldInstallationLock,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (activeRead.Value is { } admittedRecovery)
            {

                if (startupCoordinationLease is not null
                    && !startupCoordinationLease.Protects(admittedRecovery))
                {

                    throw new InvalidOperationException(
                        "The recovery host does not own a durable client-mutation blocker for the exact active reset identity.");

                }

                // Publish only after schema convergence and expected installation-UUID verification.
                // Once published it stays closed through Kestrel drain and host shutdown.
                _apiAdmission.PublishRecovery(admittedRecovery);

            }
            else if (startupCoordinationLease is not null)
            {

                Result removed = await startupCoordinationLease
                    .RemoveBlockerIfSafeAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (removed.IsFailure)
                {

                    throw new InvalidOperationException(
                        "Host startup could not retire the terminal client-mutation blocker safely. "
                        + removed.Error.Message);

                }

                startupCoordinationLease.Dispose();

                startupCoordinationLease = null;

            }

            lock (_maintenanceLockSync)
            {

                _startupCoordinationLease = startupCoordinationLease;

                startupCoordinationLease = null;

                _startedSuccessfully = true;

            }
        }
        catch (Exception ex)
        {
            // Ensure WaitUntilReadyAsync cannot hang if bootstrap throws before MarkReady.
            try
            {

                await MarkFailedAsync(ex).ConfigureAwait(false);

            }
            finally
            {

                startupCoordinationLease?.Dispose();

                ReleaseMaintenanceLockUnderLifecycleGate();

            }

            throw;
        }
    }

    // W3.4 Group D #9: checkpoint the WAL on graceful shutdown so the -wal/-shm sidecar files
    // do not persist across restarts. Best-effort: failures are logged inside the helper and
    // never block shutdown.
    public async Task StopAsync(CancellationToken cancellationToken)
    {

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            try
            {

                if (ClaimOwnedShutdownCheckpoint())
                {

                    await GrimoireDatabaseBootstrapper
                        .CheckpointOnShutdownAsync(
                            passphraseSource,
                            _databasePath,
                            cancellationToken)
                        .ConfigureAwait(false);

                }

            }
            finally
            {

                ReleaseMaintenanceLockUnderLifecycleGate();

            }
        }
        finally
        {

            _lifecycleGate.Release();

        }
    }

    public void Dispose()
    {

        _lifecycleGate.Wait();

        try
        {

            ReleaseMaintenanceLockUnderLifecycleGate();

        }
        finally
        {

            _lifecycleGate.Release();

        }

    }

    private bool ClaimOwnedShutdownCheckpoint()
    {

        ArcanumMaintenanceLock? heldInstallationLock;

        bool attached;

        bool startedSuccessfully;

        lock (_maintenanceLockSync)
        {

            heldInstallationLock = _maintenanceLock;

            attached = _maintenanceLockAttached;

            startedSuccessfully = _startedSuccessfully;

            _startedSuccessfully = false;

        }

        if (!startedSuccessfully
            || !attached
            || heldInstallationLock is null)
        {

            return false;

        }

        Result<ArcanumMaintenanceLock> borrowed =
            _maintenanceLockAccessor.BorrowHeldLock(_maintenanceDirectory);

        return borrowed.IsSuccess
            && ReferenceEquals(borrowed.Value, heldInstallationLock);

    }

    private void ReleaseMaintenanceLockUnderLifecycleGate()
    {

        ArcanumMaintenanceLock? heldInstallationLock;

        bool attached;

        IDisposable? startupLease;

        InstallationStartupCoordinationLease? startupCoordinationLease;

        lock (_maintenanceLockSync)
        {

            heldInstallationLock = _maintenanceLock;

            attached = _maintenanceLockAttached;

            _maintenanceLock = null;

            _maintenanceLockAttached = false;

            _startedSuccessfully = false;

            startupLease = _postTopologyStartupLease;

            _postTopologyStartupLease = null;

            startupCoordinationLease = _startupCoordinationLease;

            _startupCoordinationLease = null;

        }

        if (heldInstallationLock is null)
        {

            try
            {

                startupLease?.Dispose();

            }
            finally
            {

                try
                {

                    _fileSink?.Deactivate();

                }
                finally
                {

                    startupCoordinationLease?.Dispose();

                }

            }

            return;

        }

        try
        {

            startupLease?.Dispose();

        }
        finally
        {

            try
            {

                _fileSink?.Deactivate();

            }
            finally
            {
                try
                {

                    startupCoordinationLease?.Dispose();

                }
                finally
                {

                    try
                    {

                        if (attached)
                        {

                            _maintenanceLockAccessor.DetachHostLock(heldInstallationLock);

                        }

                    }
                    finally
                    {

                        _ = Interlocked.Exchange(ref _generatedMasterApiKey, null);

                        heldInstallationLock.Dispose();

                    }

                }

            }

        }

    }

    private ArcanumMaintenanceLockAcquisitionResult TryAcquireAndAttachHostLock(
        out bool alreadyStarted)
    {

        lock (_maintenanceLockSync)
        {

            alreadyStarted = _maintenanceLock is not null;

            if (alreadyStarted)
            {

                return ArcanumMaintenanceLockAcquisitionResult.Unsafe();

            }

            ArcanumMaintenanceLockAcquisitionResult acquisition =
                ArcanumMaintenanceLock.AcquireDetailed(_maintenanceDirectory);

            if (acquisition.Disposition
                is not ArcanumMaintenanceLockAcquisitionDisposition.Acquired)
            {

                return acquisition;

            }

            ArcanumMaintenanceLock heldInstallationLock =
                acquisition.BorrowAcquiredLock();

            bool attached = false;

            try
            {

                _maintenanceLockAccessor.AttachHostLock(
                    heldInstallationLock,
                    _maintenanceDirectory);

                attached = true;

                _maintenanceLock = heldInstallationLock;

                _maintenanceLockAttached = true;

                return acquisition;

            }
            finally
            {

                if (!attached)
                {

                    heldInstallationLock.Dispose();

                }

            }

        }


    }

    private async Task ActivatePostRestoreTopologyAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken)
    {

        Result<ArcanumMaintenanceLock> borrowed =
            _maintenanceLockAccessor.BorrowHeldLock(_maintenanceDirectory);

        if (borrowed.IsFailure
            || !ReferenceEquals(borrowed.Value, heldInstallationLock))
        {

            throw new InvalidOperationException(
                "Post-topology startup mutation requires the exact attached host lock.");

        }

        GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(
            heldInstallationLock,
            _maintenanceDirectory);

        _fileSink?.Activate(heldInstallationLock, _maintenanceDirectory);

        if (_postTopologyStartupAction is not null)
        {

            IDisposable? startupLease = _postTopologyStartupAction();

            lock (_maintenanceLockSync)
            {

                _postTopologyStartupLease = startupLease;

            }

        }

        if (_masterKeyBootstrap is not null
            && await _masterKeyBootstrap(cancellationToken).ConfigureAwait(false) is string generated)
        {

            _generatedMasterApiKey = generated;

        }

    }

    private async Task MarkFailedAsync(Exception exception)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IGrimoireDbReadiness readiness =
            scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>();

        readiness.MarkFailed(exception);

    }
}
