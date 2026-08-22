using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal enum InstallationMaintenanceCoordinationDisposition : byte
{

    Unsafe,

    Contended,

    Acquired,

}

internal readonly record struct InstallationMaintenanceCoordinationResult
{

    private InstallationMaintenanceCoordinationResult(
        InstallationMaintenanceCoordinationDisposition disposition,
        InstallationMaintenanceCoordinationLease? lease,
        Error error)
    {

        Disposition = disposition;

        Lease = lease;

        Error = error;

    }

    internal InstallationMaintenanceCoordinationDisposition Disposition { get; }

    internal InstallationMaintenanceCoordinationLease? Lease { get; }

    internal Error Error { get; }

    internal static InstallationMaintenanceCoordinationResult Acquired(
        InstallationMaintenanceCoordinationLease lease) =>
        new(
            InstallationMaintenanceCoordinationDisposition.Acquired,
            lease,
            Error.None);

    internal static InstallationMaintenanceCoordinationResult Contended(
        string message) =>
        new(
            InstallationMaintenanceCoordinationDisposition.Contended,
            lease: null,
            new Error(ErrorCodes.Data.FileLocked, message));

    internal static InstallationMaintenanceCoordinationResult Unsafe(Error error) =>
        new(
            InstallationMaintenanceCoordinationDisposition.Unsafe,
            lease: null,
            error);

    internal InstallationMaintenanceCoordinationLease BorrowAcquiredLease() =>
        Disposition is InstallationMaintenanceCoordinationDisposition.Acquired
        && Lease is { } acquired
            ? acquired
            : throw new InvalidOperationException(
                "This installation-maintenance coordination result has no acquired lease.");

}

internal sealed class InstallationMaintenanceCoordinationLease : IAsyncDisposable
{

    private ArcanumClientMutationLock? _held;

    private readonly string _guardedRoot;

    private readonly ClientMutationBlockerStore _blockerStore;

    private readonly IClientMutationResetEvidenceProbe _resetEvidence;

    private readonly IClientMutationRestoreEvidenceProbe _restoreEvidence;

    internal InstallationMaintenanceCoordinationLease(
        string guardedRoot,
        ArcanumClientMutationLock held,
        ClientMutationBlockerPublication publication,
        ClientMutationBlockerStore blockerStore,
        IClientMutationResetEvidenceProbe resetEvidence,
        IClientMutationRestoreEvidenceProbe restoreEvidence)
    {

        _guardedRoot = guardedRoot;

        _held = held;

        Publication = publication;

        _blockerStore = blockerStore;

        _resetEvidence = resetEvidence;

        _restoreEvidence = restoreEvidence;

    }

    internal ClientMutationBlockerPublication Publication { get; }

    internal async Task<Result> RemoveBlockerIfSafeAsync(
        CancellationToken cancellationToken)
    {

        ArcanumClientMutationLock? held = _held;

        ObjectDisposedException.ThrowIf(held is null, this);

        held.AssertHeldFor(_guardedRoot);

        Result<ActiveInstallationReset?> reset = await _resetEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {

            return Result.Failure(reset.Error);

        }

        if (reset.Value is not null)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The client-mutation blocker remains required by installation-reset evidence."));

        }

        Result<ActiveReplacementRestore?> restore = await _restoreEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (restore.IsFailure)
        {

            return Result.Failure(restore.Error);

        }

        if (restore.Value is not null)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The client-mutation blocker remains required by replacement-restore evidence."));

        }

        return await _blockerStore
            .RemoveAsync(held, Publication, cancellationToken)
            .ConfigureAwait(false);

    }

    public ValueTask DisposeAsync()
    {

        ArcanumClientMutationLock? held = _held;

        _held = null;

        held?.Dispose();

        return ValueTask.CompletedTask;

    }

}

internal enum InstallationStartupCoordinationDisposition : byte
{

    Unsafe,

    Contended,

    Acquired,

}

internal readonly record struct InstallationStartupCoordinationResult
{

    private InstallationStartupCoordinationResult(
        InstallationStartupCoordinationDisposition disposition,
        InstallationStartupCoordinationLease? lease,
        Error error)
    {

        Disposition = disposition;

        Lease = lease;

        Error = error;

    }

    internal InstallationStartupCoordinationDisposition Disposition { get; }

    internal InstallationStartupCoordinationLease? Lease { get; }

    internal Error Error { get; }

    internal static InstallationStartupCoordinationResult Acquired(
        InstallationStartupCoordinationLease lease) =>
        new(
            InstallationStartupCoordinationDisposition.Acquired,
            lease,
            Error.None);

    internal static InstallationStartupCoordinationResult Contended(
        string message) =>
        new(
            InstallationStartupCoordinationDisposition.Contended,
            lease: null,
            new Error(ErrorCodes.Data.FileLocked, message));

    internal static InstallationStartupCoordinationResult Unsafe(Error error) =>
        new(
            InstallationStartupCoordinationDisposition.Unsafe,
            lease: null,
            error);

    internal InstallationStartupCoordinationLease BorrowAcquiredLease() =>
        Disposition is InstallationStartupCoordinationDisposition.Acquired
        && Lease is { } acquired
            ? acquired
            : throw new InvalidOperationException(
                "This installation-startup coordination result has no acquired lease.");

}

internal sealed class InstallationStartupCoordinationLease : IDisposable, IAsyncDisposable
{

    private ArcanumClientMutationLock? _held;

    private readonly string _guardedRoot;

    private readonly ClientMutationBlockerStore _blockerStore;

    private readonly IClientMutationResetEvidenceProbe _resetEvidence;

    private readonly IClientMutationRestoreEvidenceProbe _restoreEvidence;

    internal InstallationStartupCoordinationLease(
        string guardedRoot,
        ArcanumClientMutationLock held,
        ClientMutationBlockerPublication? publication,
        ActiveInstallationReset? activeReset,
        ActiveReplacementRestore? activeRestore,
        ClientMutationBlockerStore blockerStore,
        IClientMutationResetEvidenceProbe resetEvidence,
        IClientMutationRestoreEvidenceProbe restoreEvidence)
    {

        _guardedRoot = guardedRoot;

        _held = held;

        Publication = publication;

        ActiveReset = activeReset;

        ActiveRestore = activeRestore;

        _blockerStore = blockerStore;

        _resetEvidence = resetEvidence;

        _restoreEvidence = restoreEvidence;

    }

    internal ClientMutationBlockerPublication? Publication { get; }

    internal ActiveInstallationReset? ActiveReset { get; }

    internal ActiveReplacementRestore? ActiveRestore { get; }

    internal bool RequiresRecovery => ActiveReset is not null || ActiveRestore is not null;

    internal bool Protects(ActiveInstallationReset active) =>
        Publication is { Record.Kind: ClientMutationBlockerKind.InstallationReset } publication
        && publication.Record.Scope == active.Scope
        && string.Equals(
            publication.Record.PlanId,
            active.PlanId,
            StringComparison.Ordinal)
        && active.OperationId != Guid.Empty
        && (publication.Record.OperationId is null
            || publication.Record.OperationId == active.OperationId);

    internal async Task<Result> RemoveBlockerIfSafeAsync(
        CancellationToken cancellationToken)
    {

        ArcanumClientMutationLock? held = _held;

        ObjectDisposedException.ThrowIf(held is null, this);

        held.AssertHeldFor(_guardedRoot);

        if (Publication is not { } publication)
        {

            return Result.Success();

        }

        Result<ActiveInstallationReset?> reset = await _resetEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {

            return Result.Failure(reset.Error);

        }

        Result<ActiveReplacementRestore?> restore = await _restoreEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (restore.IsFailure)
        {

            return Result.Failure(restore.Error);

        }

        if (reset.Value is not null || restore.Value is not null)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The client-mutation blocker remains required by installation maintenance evidence."));

        }

        return await _blockerStore
            .RemoveAsync(held, publication, cancellationToken)
            .ConfigureAwait(false);

    }

    public ValueTask DisposeAsync()
    {

        Dispose();

        return ValueTask.CompletedTask;

    }

    public void Dispose()
    {

        ArcanumClientMutationLock? held = _held;

        _held = null;

        held?.Dispose();

    }

}

internal sealed class InstallationMaintenanceCoordination
{

    private readonly string _guardedRoot;

    private readonly ClientMutationBlockerStore _blockerStore;

    private readonly IClientMutationResetEvidenceProbe _resetEvidence;

    private readonly IClientMutationRestoreEvidenceProbe _restoreEvidence;

    private readonly Func<
        string,
        ArcanumClientMutationLockAcquisitionResult> _acquire;

    internal InstallationMaintenanceCoordination(
        string guardedRoot,
        ClientMutationBlockerStore blockerStore,
        IClientMutationResetEvidenceProbe resetEvidence,
        IClientMutationRestoreEvidenceProbe restoreEvidence,
        Func<string, ArcanumClientMutationLockAcquisitionResult>? acquire = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        _guardedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(guardedRoot));

        _blockerStore = blockerStore
            ?? throw new ArgumentNullException(nameof(blockerStore));

        _resetEvidence = resetEvidence
            ?? throw new ArgumentNullException(nameof(resetEvidence));

        _restoreEvidence = restoreEvidence
            ?? throw new ArgumentNullException(nameof(restoreEvidence));

        _acquire = acquire ?? ArcanumClientMutationLock.AcquireDetailed;

    }

    internal Task<InstallationMaintenanceCoordinationResult>
        AcquireInstallationResetAsync(
            InstallationResetScope scope,
            string planId,
            Guid? operationId,
            CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (scope is not InstallationResetScope.Global
            and not InstallationResetScope.All)
        {

            throw new ArgumentOutOfRangeException(
                nameof(scope),
                "A durable client-mutation blocker is reserved for global and all installation reset scopes.");

        }

        return AcquireAsync(
            new ClientMutationBlockerRecord(
                ClientMutationBlockerStore.CurrentVersion,
                Guid.NewGuid(),
                ClientMutationBlockerKind.InstallationReset,
                scope,
                planId,
                operationId),
            cancellationToken);

    }

    internal Task<InstallationMaintenanceCoordinationResult>
        AcquireReplacementRestoreAsync(
            ArcanumMaintenanceLock heldMaintenanceLock,
            Guid operationId,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldMaintenanceLock);

        if (operationId == Guid.Empty)
        {

            throw new ArgumentException(
                "A replacement restore requires a nonempty operation identity.",
                nameof(operationId));

        }

        heldMaintenanceLock.AssertHeldFor(_guardedRoot);

        return AcquireAsync(
            new ClientMutationBlockerRecord(
                ClientMutationBlockerStore.CurrentVersion,
                Guid.NewGuid(),
                ClientMutationBlockerKind.ReplacementRestore,
                Scope: null,
                PlanId: null,
                operationId),
            cancellationToken);

    }

    internal async Task<InstallationStartupCoordinationResult>
        AcquireHostStartupAsync(
            ArcanumMaintenanceLock heldMaintenanceLock,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldMaintenanceLock);

        heldMaintenanceLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        ArcanumClientMutationLockAcquisitionResult acquisition =
            _acquire(_guardedRoot);

        if (acquisition.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Contended)
        {

            return InstallationStartupCoordinationResult.Contended(
                "Another client mutation or installation maintenance operation owns the client-mutation mutex.");

        }

        if (acquisition.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Unsafe)
        {

            return InstallationStartupCoordinationResult.Unsafe(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The client-mutation mutex could not be acquired safely."));

        }

        ArcanumClientMutationLock held = acquisition.BorrowAcquiredLock();

        try
        {

            Result<ClientMutationBlockerPublication?> inspected =
                await _blockerStore
                    .InspectAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (inspected.IsFailure)
            {

                held.Dispose();

                return InstallationStartupCoordinationResult.Unsafe(
                    inspected.Error);

            }

            Result<ActiveInstallationReset?> reset = await _resetEvidence
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);

            if (reset.IsFailure)
            {

                held.Dispose();

                return InstallationStartupCoordinationResult.Unsafe(reset.Error);

            }

            Result<ActiveReplacementRestore?> restore = await _restoreEvidence
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);

            if (restore.IsFailure)
            {

                held.Dispose();

                return InstallationStartupCoordinationResult.Unsafe(restore.Error);

            }

            if (reset.Value is not null && restore.Value is not null)
            {

                held.Dispose();

                return InstallationStartupCoordinationResult.Unsafe(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "Installation reset and replacement restore evidence are both active."));

            }

            ClientMutationBlockerPublication? publication = inspected.Value;

            if (publication is { } existing)
            {

                bool belongsToActiveReset = reset.Value is { } active
                    && BlockerMatchesReset(existing.Record, active);

                bool belongsToActiveRestore = restore.Value is { } activeRestore
                    && reset.Value is null
                    && existing.Record.Kind
                        is ClientMutationBlockerKind.ReplacementRestore
                    && existing.Record.OperationId == activeRestore.OperationId;

                if (!belongsToActiveReset && !belongsToActiveRestore)
                {

                    if (reset.Value is not null || restore.Value is not null)
                    {

                        held.Dispose();

                        return InstallationStartupCoordinationResult.Unsafe(new Error(
                            ErrorCodes.Data.RecoveryRequired,
                            "The durable client-mutation blocker does not match active installation maintenance evidence."));

                    }

                    Result removed = await _blockerStore
                        .RemoveAsync(held, existing, cancellationToken)
                        .ConfigureAwait(false);

                    if (removed.IsFailure)
                    {

                        held.Dispose();

                        return InstallationStartupCoordinationResult.Unsafe(
                            removed.Error);

                    }

                    publication = null;

                }

            }
            else if (reset.Value is { } active
                     && active.Scope is InstallationResetScope.Global
                         or InstallationResetScope.All)
            {

                Result<ClientMutationBlockerPublication> published =
                    await _blockerStore
                        .PublishAsync(
                            held,
                            new ClientMutationBlockerRecord(
                                ClientMutationBlockerStore.CurrentVersion,
                                Guid.NewGuid(),
                                ClientMutationBlockerKind.InstallationReset,
                                active.Scope,
                                active.PlanId,
                                active.OperationId),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (published.IsFailure)
                {

                    held.Dispose();

                    return InstallationStartupCoordinationResult.Unsafe(
                        published.Error);

                }

                publication = published.Value;

            }
            else if (restore.Value is { } activeRestore)
            {

                Result<ClientMutationBlockerPublication> published =
                    await _blockerStore
                        .PublishAsync(
                            held,
                            new ClientMutationBlockerRecord(
                                ClientMutationBlockerStore.CurrentVersion,
                                Guid.NewGuid(),
                                ClientMutationBlockerKind.ReplacementRestore,
                                Scope: null,
                                PlanId: null,
                                activeRestore.OperationId),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (published.IsFailure)
                {

                    held.Dispose();

                    return InstallationStartupCoordinationResult.Unsafe(
                        published.Error);

                }

                publication = published.Value;

            }

            cancellationToken.ThrowIfCancellationRequested();

            return InstallationStartupCoordinationResult.Acquired(
                new InstallationStartupCoordinationLease(
                    _guardedRoot,
                    held,
                    publication,
                    reset.Value,
                    restore.Value,
                    _blockerStore,
                    _resetEvidence,
                    _restoreEvidence));

        }
        catch
        {

            held.Dispose();

            throw;

        }

    }

    private async Task<InstallationMaintenanceCoordinationResult> AcquireAsync(
        ClientMutationBlockerRecord requested,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        ArcanumClientMutationLockAcquisitionResult acquisition =
            _acquire(_guardedRoot);

        if (acquisition.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Contended)
        {

            return InstallationMaintenanceCoordinationResult.Contended(
                "Another client mutation or installation maintenance operation owns the client-mutation mutex.");

        }

        if (acquisition.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Unsafe)
        {

            return InstallationMaintenanceCoordinationResult.Unsafe(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The client-mutation mutex could not be acquired safely."));

        }

        ArcanumClientMutationLock held = acquisition.BorrowAcquiredLock();

        try
        {

            Result<ClientMutationBlockerPublication?> inspected =
                await _blockerStore
                    .InspectAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (inspected.IsFailure)
            {

                held.Dispose();

                return InstallationMaintenanceCoordinationResult.Unsafe(
                    inspected.Error);

            }

            Result<ActiveInstallationReset?> reset = await _resetEvidence
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);

            if (reset.IsFailure)
            {

                held.Dispose();

                return InstallationMaintenanceCoordinationResult.Unsafe(
                    reset.Error);

            }

            ClientMutationBlockerPublication publication;

            if (inspected.Value is { } existing)
            {

                if (!CanAdopt(existing.Record, requested, reset.Value))
                {

                    held.Dispose();

                    return InstallationMaintenanceCoordinationResult.Contended(
                        "A different installation maintenance operation owns the durable client-mutation blocker.");

                }

                publication = existing;

            }
            else
            {

                bool resetIdentityRefused = requested.Kind
                    is ClientMutationBlockerKind.InstallationReset
                    ? reset.Value is { } active
                        ? !Matches(active, requested)
                        : requested.OperationId is not null
                    : reset.Value is not null;

                if (resetIdentityRefused)
                {

                    held.Dispose();

                    return InstallationMaintenanceCoordinationResult.Contended(
                        "The requested reset identity does not match the exact active installation reset evidence.");

                }

                Result<ActiveReplacementRestore?> restore = await _restoreEvidence
                    .InspectAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (restore.IsFailure)
                {

                    held.Dispose();

                    return InstallationMaintenanceCoordinationResult.Unsafe(
                        restore.Error);

                }

                if (restore.Value is not null)
                {

                    held.Dispose();

                    return InstallationMaintenanceCoordinationResult.Contended(
                        "A replacement restore is active for this installation.");

                }

                Result<ClientMutationBlockerPublication> published =
                    await _blockerStore
                        .PublishAsync(held, requested, cancellationToken)
                        .ConfigureAwait(false);

                if (published.IsFailure)
                {

                    held.Dispose();

                    return InstallationMaintenanceCoordinationResult.Unsafe(
                        published.Error);

                }

                publication = published.Value;

            }

            cancellationToken.ThrowIfCancellationRequested();

            return InstallationMaintenanceCoordinationResult.Acquired(
                new InstallationMaintenanceCoordinationLease(
                    _guardedRoot,
                    held,
                    publication,
                    _blockerStore,
                    _resetEvidence,
                    _restoreEvidence));

        }
        catch
        {

            held.Dispose();

            throw;

        }

    }

    private static bool CanAdopt(
        ClientMutationBlockerRecord existing,
        ClientMutationBlockerRecord requested,
        ActiveInstallationReset? active) =>
        existing.Kind == requested.Kind
        && existing.Scope == requested.Scope
        && string.Equals(
            existing.PlanId,
            requested.PlanId,
            StringComparison.Ordinal)
        && (existing.OperationId is { } existingOperation
            ? requested.OperationId == existingOperation
                && active is not null
                && Matches(active, requested)
            : requested.OperationId is null
                ? active is null
                : active is not null && Matches(active, requested));

    private static bool Matches(
        ActiveInstallationReset active,
        ClientMutationBlockerRecord requested) =>
        active.Scope == requested.Scope
        && string.Equals(active.PlanId, requested.PlanId, StringComparison.Ordinal)
        && active.OperationId != Guid.Empty
        && active.OperationId == requested.OperationId;

    private static bool BlockerMatchesReset(
        ClientMutationBlockerRecord blocker,
        ActiveInstallationReset active) =>
        blocker.Kind is ClientMutationBlockerKind.InstallationReset
        && blocker.Scope == active.Scope
        && string.Equals(blocker.PlanId, active.PlanId, StringComparison.Ordinal)
        && active.OperationId != Guid.Empty
        && (blocker.OperationId is null
            || blocker.OperationId == active.OperationId);

}
