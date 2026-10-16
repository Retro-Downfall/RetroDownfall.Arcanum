using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Runs one piece of work with the installation lock a stopped-host process already holds lent to the
/// offline-transition journal's phase authority for the length of that work.
/// </summary>
/// <remarks>
/// A stopped-host reset reads and finishes the transition journal in its own process, and the journal's
/// phase authority borrows the lock from the accessor the serving host attaches its own to. There is no
/// host here, so the lock is lent through the issuer: it mints a one-shot loan authority from the lock
/// it was created over (<see cref="IStoppedHostGrimoireAuthorityIssuer.IssueStoppedHostFactoryErasureLockLoan"/>),
/// the loan attaches that exact lock for this call and detaches it when the call ends, and a loan is
/// refused beside a lock the accessor already holds. The reset service receives this type instead of
/// the accessor, so the service keeps receiving the exact held lock explicitly and never looks one up.
/// </remarks>
internal sealed class StoppedHostLockLender(
    InstallationResetMaintenanceLockAccessor accessor,
    string canonicalDatabasePath)
{
    private readonly InstallationResetMaintenanceLockAccessor _accessor =
        accessor ?? throw new ArgumentNullException(nameof(accessor));

    private readonly string _canonicalDatabasePath =
        string.IsNullOrWhiteSpace(canonicalDatabasePath)
            ? throw new ArgumentException("A database path is required.", nameof(canonicalDatabasePath))
            : canonicalDatabasePath;

    /// <summary>
    /// Lends the issuer's held lock, hands the borrowed lock to <paramref name="work"/>, and takes the
    /// loan back when the work returns.
    /// </summary>
    /// <param name="issuer">The issuer minted over the lock this process holds.</param>
    /// <param name="guardedRoot">The guarded directory the lock is held for; the loan is refused for any other.</param>
    /// <param name="work">The work to run while the lock is lent.</param>
    internal async Task<Result<T>> RunAsync<T>(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        string guardedRoot,
        Func<ArcanumMaintenanceLock, Task<Result<T>>> work)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        ArgumentNullException.ThrowIfNull(work);

        Result<IStoppedHostGrimoireConnectionAuthority> issued =
            issuer.IssueStoppedHostFactoryErasureLockLoan();

        if (issued.IsFailure)
        {
            return Result<T>.Failure(issued.Error);
        }

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<StoppedHostLockLoan> lent = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            authority,
            _accessor,
            _canonicalDatabasePath);

        if (lent.IsFailure)
        {
            return Result<T>.Failure(lent.Error);
        }

        await using StoppedHostLockLoan loan = lent.Value;

        try
        {
            loan.HeldLock.AssertHeldFor(guardedRoot);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return Result<T>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The lent installation lock is not the lock of this installation root."));
        }

        return await work(loan.HeldLock).ConfigureAwait(false);
    }
}
