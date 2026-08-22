using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal readonly record struct InstallationResetMaintenanceLockAttempt(
    ArcanumMaintenanceLockAcquisitionDisposition Disposition,
    ArcanumMaintenanceLock? Lock)
{

    internal static InstallationResetMaintenanceLockAttempt From(
        ArcanumMaintenanceLockAcquisitionResult acquisition) =>
        acquisition.Disposition switch
        {
            ArcanumMaintenanceLockAcquisitionDisposition.Acquired =>
                Acquired(acquisition.BorrowAcquiredLock()),
            ArcanumMaintenanceLockAcquisitionDisposition.Contended =>
                Contended(),
            _ => Unsafe(),
        };

    internal static InstallationResetMaintenanceLockAttempt Acquired(
        ArcanumMaintenanceLock maintenanceLock) =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Acquired,
            maintenanceLock);

    internal static InstallationResetMaintenanceLockAttempt Contended() =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Contended,
            Lock: null);

    internal static InstallationResetMaintenanceLockAttempt Unsafe() =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Unsafe,
            Lock: null);

}

internal interface IInstallationResetClientCoordinationLease : IAsyncDisposable
{

    Task<Result> RemoveBlockerIfSafeAsync(CancellationToken cancellationToken);

}

internal delegate Task<Result<IInstallationResetClientCoordinationLease>>
    AcquireInstallationResetClientCoordination(
        InstallationResetScope scope,
        string planId,
        Guid? operationId,
        CancellationToken cancellationToken);

internal sealed class InstallationResetClientCoordinationLease(
    InstallationMaintenanceCoordinationLease lease)
    : IInstallationResetClientCoordinationLease
{

    private readonly InstallationMaintenanceCoordinationLease _lease =
        lease ?? throw new ArgumentNullException(nameof(lease));

    public Task<Result> RemoveBlockerIfSafeAsync(
        CancellationToken cancellationToken) =>
        _lease.RemoveBlockerIfSafeAsync(cancellationToken);

    public ValueTask DisposeAsync() => _lease.DisposeAsync();

}

internal sealed class InstallationResetApplyBoundary : IInstallationResetApplyBoundary
{

    private static readonly TimeSpan LockRetryBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan InitialLockRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan MaximumLockRetryDelay = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task<Result<bool>>> _quitServer;

    private readonly Func<
        FactoryResetRequest,
        CancellationToken,
        Task<Result<DataRetentionApplyResult>>> _applyFactoryReset;

    private readonly IInstallationResetLockedService _resetService;

    private readonly Func<string, InstallationResetMaintenanceLockAttempt>
        _acquireMaintenanceLock;

    private readonly TimeProvider _timeProvider;

    private readonly AcquireInstallationResetClientCoordination
        _acquireClientCoordination;

    private readonly Func<
        InstallationResetApplyRequest,
        InstallationResetPlan,
        Result<InstallationResetHostHandoff>> _createHostHandoff;

    public InstallationResetApplyBoundary(
        ArcanumApiClient apiClient,
        IInstallationResetLockedService resetService,
        IInstallationResetOnlineDataHandoff onlineDataHandoff,
        TimeProvider timeProvider,
        InstallationMaintenanceCoordination maintenanceCoordination)
        : this(
            apiClient.QuitServerAsync,
            apiClient.FactoryResetDataAsync,
            resetService,
            static guardedDirectory =>
                InstallationResetMaintenanceLockAttempt.From(
                    ArcanumMaintenanceLock.AcquireDetailed(guardedDirectory)),
            timeProvider,
            (scope, planId, operationId, cancellationToken) =>
                AcquireClientCoordinationAsync(
                    maintenanceCoordination,
                    scope,
                    planId,
                    operationId,
                    cancellationToken),
            onlineDataHandoff.CreateHostHandoff)
    {

    }

    internal InstallationResetApplyBoundary(
        Func<CancellationToken, Task<Result<bool>>> quitServer,
        Func<
            FactoryResetRequest,
            CancellationToken,
            Task<Result<DataRetentionApplyResult>>> applyFactoryReset,
        IInstallationResetLockedService resetService,
        Func<string, InstallationResetMaintenanceLockAttempt> acquireMaintenanceLock,
        TimeProvider timeProvider,
        AcquireInstallationResetClientCoordination acquireClientCoordination,
        Func<
            InstallationResetApplyRequest,
            InstallationResetPlan,
            Result<InstallationResetHostHandoff>> createHostHandoff)
    {

        _quitServer = quitServer;

        _applyFactoryReset = applyFactoryReset;

        _resetService = resetService;

        _acquireMaintenanceLock = acquireMaintenanceLock;

        _timeProvider = timeProvider;

        _acquireClientCoordination = acquireClientCoordination
            ?? throw new ArgumentNullException(nameof(acquireClientCoordination));

        _createHostHandoff = createHostHandoff
            ?? throw new ArgumentNullException(nameof(createHostHandoff));

    }

    private static async Task<Result<IInstallationResetClientCoordinationLease>>
        AcquireClientCoordinationAsync(
            InstallationMaintenanceCoordination maintenanceCoordination,
            InstallationResetScope scope,
            string planId,
            Guid? operationId,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(maintenanceCoordination);

        InstallationMaintenanceCoordinationResult acquired =
            await maintenanceCoordination
                .AcquireInstallationResetAsync(
                    scope,
                    planId,
                    operationId,
                    cancellationToken)
                .ConfigureAwait(false);

        return acquired.Disposition
            is InstallationMaintenanceCoordinationDisposition.Acquired
            ? Result<IInstallationResetClientCoordinationLease>.Success(
                new InstallationResetClientCoordinationLease(
                    acquired.BorrowAcquiredLease()))
            : Result<IInstallationResetClientCoordinationLease>.Failure(
                acquired.Error);

    }

    public async Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken)
        => await ApplyAsync(
                request,
                hostHandoff: null,
                onlineCompletionDurable: false,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        InstallationResetHostHandoff? hostHandoff,
        bool onlineCompletionDurable,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.Request.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            if (hostHandoff is { } handoff)
            {

                Result online = onlineCompletionDurable
                    ? Result.Success()
                    : await CompleteOnlineDataResetAsync(
                        handoff,
                        coordinationLease: null,
                        cancellationToken).ConfigureAwait(false);

                return online.IsFailure
                    ? Result<InstallationResetResult>.Failure(online.Error)
                    : await ApplyOfflineAsync(
                        request,
                        handoff,
                        cancellationToken).ConfigureAwait(false);

            }

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ResetInProgress,
                "The authenticated installation reset host handoff is unavailable."));

        }

        return await ApplyOfflineAsync(
                request,
                handoff: null,
                cancellationToken)
            .ConfigureAwait(false);

    }

    public async Task<Result<InstallationResetResult>> ApplyFreshAsync(
        InstallationResetPlanRequest request,
        InstallationResetPlan confirmedPlan,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(confirmedPlan);

        InstallationResetApplyRequest applyRequest = new(
            request,
            confirmedPlan.PlanId);

        if (request.Scope is InstallationResetScope.Workspace)
        {

            return await ApplyOfflineAsync(
                    applyRequest,
                    handoff: null,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        Result<IInstallationResetClientCoordinationLease> coordinated =
            await _acquireClientCoordination(
                    request.Scope,
                    confirmedPlan.PlanId,
                    operationId: null,
                    cancellationToken)
                .ConfigureAwait(false);

        if (coordinated.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(coordinated.Error);

        }

        Result<InstallationResetHostHandoff> created = _createHostHandoff(
            applyRequest,
            confirmedPlan);

        if (created.IsFailure)
        {

            await using IInstallationResetClientCoordinationLease failedLease =
                coordinated.Value;

            Result removed = await failedLease
                .RemoveBlockerIfSafeAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<InstallationResetResult>.Failure(
                removed.IsFailure ? removed.Error : created.Error);

        }

        InstallationResetHostHandoff handoff = created.Value;

        await using (IInstallationResetClientCoordinationLease coordinationLease =
                     coordinated.Value)
        {

            Result online = await CompleteOnlineDataResetAsync(
                    handoff,
                    coordinationLease,
                    cancellationToken)
                .ConfigureAwait(false);

            if (online.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(online.Error);

            }

        }

        return await ApplyOfflineAsync(
                applyRequest,
                handoff,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result> CompleteOnlineDataResetAsync(
        InstallationResetHostHandoff handoff,
        IInstallationResetClientCoordinationLease? coordinationLease,
        CancellationToken cancellationToken)
    {

        string dataPlanId = handoff.AcceptedBinding.DataPlanIds.Single();

        Result<DataRetentionApplyResult> applied = await _applyFactoryReset(
            new FactoryResetRequest(
                "factory-reset",
                dataPlanId,
                handoff.RequestedOperationId,
                handoff),
            cancellationToken).ConfigureAwait(false);

        if (applied.IsFailure)
        {

            if (string.Equals(
                    applied.Error.Code,
                    ErrorCodes.Data.PlanChanged,
                    StringComparison.Ordinal)
                && coordinationLease is not null)
            {

                Result removed = await coordinationLease
                    .RemoveBlockerIfSafeAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (removed.IsFailure)
                {

                    return Result.Failure(removed.Error);

                }

            }

            return Result.Failure(applied.Error);

        }

        return Result.Success();

    }

    private async Task<Result<InstallationResetResult>> ApplyOfflineAsync(
        InstallationResetApplyRequest request,
        InstallationResetHostHandoff? handoff,
        CancellationToken cancellationToken)
    {

        Result<bool> shutdown = await _quitServer(cancellationToken)
            .ConfigureAwait(false);

        if (shutdown.IsFailure
            && !string.Equals(
                shutdown.Error.Code,
                ErrorCodes.Connection.Unreachable,
                StringComparison.Ordinal)
            && !string.Equals(
                shutdown.Error.Code,
                ErrorCodes.Security.MissingApiKey,
                StringComparison.Ordinal))
        {

            return Result<InstallationResetResult>.Failure(shutdown.Error);

        }

        InstallationResetMaintenanceLockAttempt acquisition =
            await AcquireMaintenanceLockAsync(
            cancellationToken).ConfigureAwait(false);

        using ArcanumMaintenanceLock? maintenanceLock = acquisition.Lock;

        if (acquisition.Disposition
            is ArcanumMaintenanceLockAcquisitionDisposition.Unsafe)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The Arcanum maintenance lock could not be acquired safely because its topology, identity, or owner-only permissions could not be validated."));

        }

        if (maintenanceLock is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.FileLocked,
                "The Arcanum maintenance lock remained unavailable after the shutdown handoff."));

        }

        if (request.Request.Scope is InstallationResetScope.Workspace)
        {

            return await _resetService.ApplyUnderMaintenanceLockAsync(
                    request,
                    maintenanceLock,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        Result<IInstallationResetClientCoordinationLease> coordinated =
            await _acquireClientCoordination(
                    request.Request.Scope,
                    request.ExpectedPlanId,
                    handoff?.RequestedOperationId,
                    cancellationToken)
                .ConfigureAwait(false);

        if (coordinated.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(coordinated.Error);

        }

        await using IInstallationResetClientCoordinationLease coordinationLease =
            coordinated.Value;

        Result<InstallationResetResult> applied = await _resetService
            .ApplyUnderMaintenanceLockAsync(
                request,
                maintenanceLock,
                cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            return applied;

        }

        Result removed = await coordinationLease
            .RemoveBlockerIfSafeAsync(cancellationToken)
            .ConfigureAwait(false);

        return removed.IsSuccess
            ? applied
            : Result<InstallationResetResult>.Failure(removed.Error);

    }

    private async Task<InstallationResetMaintenanceLockAttempt>
        AcquireMaintenanceLockAsync(
        CancellationToken cancellationToken)
    {

        long startedAt = _timeProvider.GetTimestamp();

        TimeSpan retryDelay = InitialLockRetryDelay;

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            InstallationResetMaintenanceLockAttempt acquisition =
                _acquireMaintenanceLock(
                ArcanumPaths.GrimoireDirectory);

            if (acquisition.Disposition
                is not ArcanumMaintenanceLockAcquisitionDisposition.Contended)
            {

                return acquisition;

            }

            TimeSpan remaining = LockRetryBudget
                - _timeProvider.GetElapsedTime(startedAt);

            if (remaining <= TimeSpan.Zero)
            {

                return InstallationResetMaintenanceLockAttempt.Contended();

            }

            TimeSpan boundedDelay = retryDelay <= remaining
                ? retryDelay
                : remaining;

            await Task.Delay(
                boundedDelay,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);

            retryDelay = TimeSpan.FromTicks(
                Math.Min(
                    retryDelay.Ticks * 2,
                    MaximumLockRetryDelay.Ticks));

        }

    }

}
