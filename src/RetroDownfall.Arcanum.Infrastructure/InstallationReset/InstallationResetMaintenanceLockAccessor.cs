using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetMaintenanceLockAccessor
{

    Result<ArcanumMaintenanceLock> BorrowHeldLock(string guardedDirectory);

}

/// <summary>
/// Process-local publication of the exact installation lock owned by the database hosted service.
/// </summary>
/// <remarks>
/// This accessor never acquires or releases a lock. Borrowers receive the live handle only after its
/// directory identity has been asserted, and ownership remains with the hosted service throughout.
/// </remarks>
internal sealed class InstallationResetMaintenanceLockAccessor
    : IInstallationResetMaintenanceLockAccessor
{

    private readonly object _sync = new();

    private ArcanumMaintenanceLock? _heldInstallationLock;

    internal void AttachHostLock(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        lock (_sync)
        {

            if (_heldInstallationLock is not null
                && !ReferenceEquals(_heldInstallationLock, heldInstallationLock))
            {

                throw new InvalidOperationException(
                    "A different installation maintenance lock is already attached.");

            }

            _heldInstallationLock = heldInstallationLock;

        }

    }

    internal void DetachHostLock(ArcanumMaintenanceLock heldInstallationLock)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        lock (_sync)
        {

            if (_heldInstallationLock is null)
            {

                return;

            }

            if (!ReferenceEquals(_heldInstallationLock, heldInstallationLock))
            {

                throw new InvalidOperationException(
                    "Only the hosted service's attached installation lock can be detached.");

            }

            _heldInstallationLock = null;

        }

    }

    public Result<ArcanumMaintenanceLock> BorrowHeldLock(string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        lock (_sync)
        {

            if (_heldInstallationLock is not { } heldInstallationLock)
            {

                return Unavailable();

            }

            try
            {

                heldInstallationLock.AssertHeldFor(guardedDirectory);

                return heldInstallationLock;

            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ObjectDisposedException)
            {

                return Unavailable();

            }

        }

    }

    private static Result<ArcanumMaintenanceLock> Unavailable() =>
        new Error(
            ErrorCodes.Covenant.Unavailable,
            "The host-owned installation maintenance lock is not available to borrow.");

}
