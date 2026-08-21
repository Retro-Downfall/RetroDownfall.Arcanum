using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Cli.Commands;

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

    private readonly IInstallationResetService _resetService;

    private readonly IInstallationResetOnlineDataHandoff _onlineDataHandoff;

    private readonly Func<string, IDisposable?> _tryAcquireMaintenanceLock;

    private readonly TimeProvider _timeProvider;

    public InstallationResetApplyBoundary(
        ArcanumApiClient apiClient,
        IInstallationResetService resetService,
        IInstallationResetOnlineDataHandoff onlineDataHandoff,
        TimeProvider timeProvider)
        : this(
            apiClient.QuitServerAsync,
            apiClient.FactoryResetDataAsync,
            resetService,
            onlineDataHandoff,
            static guardedDirectory => ArcanumMaintenanceLock.TryAcquire(guardedDirectory),
            timeProvider)
    {

    }

    internal InstallationResetApplyBoundary(
        Func<CancellationToken, Task<Result<bool>>> quitServer,
        Func<
            FactoryResetRequest,
            CancellationToken,
            Task<Result<DataRetentionApplyResult>>> applyFactoryReset,
        IInstallationResetService resetService,
        IInstallationResetOnlineDataHandoff onlineDataHandoff,
        Func<string, IDisposable?> tryAcquireMaintenanceLock,
        TimeProvider timeProvider)
    {

        _quitServer = quitServer;

        _applyFactoryReset = applyFactoryReset;

        _resetService = resetService;

        _onlineDataHandoff = onlineDataHandoff;

        _tryAcquireMaintenanceLock = tryAcquireMaintenanceLock;

        _timeProvider = timeProvider;

    }

    public async Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.Request.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            Result<InstallationResetOnlineDataHandoff?> active =
                await _onlineDataHandoff
                    .ReadAsync(request, cancellationToken)
                    .ConfigureAwait(false);

            if (active.IsSuccess && active.Value is { } handoff)
            {

                return await ApplyPreparedHandoffAsync(
                    request,
                    handoff,
                    cancellationToken).ConfigureAwait(false);

            }

            if (active.IsSuccess)
            {

                return Result<InstallationResetResult>.Failure(new Error(
                    ErrorCodes.Data.ResetInProgress,
                    "The durable installation reset handoff is no longer available."));

            }

            if (active.IsFailure
                && !string.Equals(
                    active.Error.Code,
                    ErrorCodes.Data.ResetInProgress,
                    StringComparison.Ordinal))
            {

                return Result<InstallationResetResult>.Failure(active.Error);

            }

        }

        return await ApplyOfflineAsync(request, cancellationToken)
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

            return await ApplyOfflineAsync(applyRequest, cancellationToken)
                .ConfigureAwait(false);

        }

        Result<InstallationResetOnlineDataHandoff> prepared =
            await _onlineDataHandoff
                .PrepareAsync(
                    applyRequest,
                    confirmedPlan,
                    cancellationToken)
                .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(prepared.Error);

        }

        return await ApplyPreparedHandoffAsync(
            applyRequest,
            prepared.Value,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<InstallationResetResult>> ApplyPreparedHandoffAsync(
        InstallationResetApplyRequest request,
        InstallationResetOnlineDataHandoff handoff,
        CancellationToken cancellationToken)
    {

        if (!handoff.DataResetCompleted)
        {

            Result<DataRetentionApplyResult> applied = await _applyFactoryReset(
                new FactoryResetRequest(
                    "factory-reset",
                    handoff.DataPlanId,
                    handoff.RequestedOperationId),
                cancellationToken).ConfigureAwait(false);

            if (applied.IsFailure)
            {

                if (string.Equals(
                        applied.Error.Code,
                        ErrorCodes.Data.PlanChanged,
                        StringComparison.Ordinal))
                {

                    Result retired = await _onlineDataHandoff
                        .RetirePreEffectAsync(handoff, cancellationToken)
                        .ConfigureAwait(false);

                    if (retired.IsFailure)
                    {

                        return Result<InstallationResetResult>.Failure(retired.Error);

                    }

                }

                return Result<InstallationResetResult>.Failure(applied.Error);

            }

            Result recorded = await _onlineDataHandoff
                .RecordCompletedAsync(
                    handoff,
                    applied.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(recorded.Error);

            }

        }

        return await ApplyOfflineAsync(request, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result<InstallationResetResult>> ApplyOfflineAsync(
        InstallationResetApplyRequest request,
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

        using IDisposable? maintenanceLock = await AcquireMaintenanceLockAsync(
            cancellationToken).ConfigureAwait(false);

        if (maintenanceLock is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.FileLocked,
                "The Arcanum maintenance lock remained unavailable after the shutdown handoff."));

        }

        return await _resetService.ApplyAsync(request, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<IDisposable?> AcquireMaintenanceLockAsync(
        CancellationToken cancellationToken)
    {

        long startedAt = _timeProvider.GetTimestamp();

        TimeSpan retryDelay = InitialLockRetryDelay;

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? maintenanceLock = _tryAcquireMaintenanceLock(
                ArcanumPaths.GrimoireDirectory);

            if (maintenanceLock is not null)
            {

                return maintenanceLock;

            }

            TimeSpan remaining = LockRetryBudget
                - _timeProvider.GetElapsedTime(startedAt);

            if (remaining <= TimeSpan.Zero)
            {

                return null;

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
