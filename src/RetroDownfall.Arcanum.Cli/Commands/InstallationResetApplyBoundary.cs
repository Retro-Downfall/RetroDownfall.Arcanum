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

    private readonly IInstallationResetService _resetService;

    private readonly Func<string, IDisposable?> _tryAcquireMaintenanceLock;

    private readonly TimeProvider _timeProvider;

    public InstallationResetApplyBoundary(
        ArcanumApiClient apiClient,
        IInstallationResetService resetService,
        TimeProvider timeProvider)
        : this(
            apiClient.QuitServerAsync,
            resetService,
            static guardedDirectory => ArcanumMaintenanceLock.TryAcquire(guardedDirectory),
            timeProvider)
    {

    }

    internal InstallationResetApplyBoundary(
        Func<CancellationToken, Task<Result<bool>>> quitServer,
        IInstallationResetService resetService,
        Func<string, IDisposable?> tryAcquireMaintenanceLock,
        TimeProvider timeProvider)
    {

        _quitServer = quitServer;

        _resetService = resetService;

        _tryAcquireMaintenanceLock = tryAcquireMaintenanceLock;

        _timeProvider = timeProvider;

    }

    public async Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

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
