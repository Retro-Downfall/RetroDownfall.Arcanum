using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal enum StoppedHostGrimoireOperation : byte
{
    InstallationResetPlanRead = 1,

    InstallationResetWorkspaceResolution = 2,

    InstallationResetIdentityRead = 3,

    InstallationResetHostToolsEvidenceRead = 4,

    InstallationResetApply = 5,

    MarkerPairReset = 6,

    /// <summary>
    /// The nested protected erasure of a global installation reset, which runs over the process's own
    /// ordinary composition and so opens no stopped-host connection at all.
    /// </summary>
    /// <remarks>
    /// The one operation here that is not a connection. What it authorizes is the loan of the held
    /// installation lock to the transition-journal accessor for the length of one erasure
    /// (<see cref="StoppedHostGrimoireAuthorityIssuer.LendHeldLock"/>), and no connection factory method
    /// accepts it.
    /// </remarks>
    InstallationResetFactoryErasure = 7,
}

internal sealed class StoppedHostGrimoireAuthorityIssuer
    : IStoppedHostGrimoireAuthorityIssuer
{
    private readonly string _canonicalDatabasePath;

    private readonly string _guardedGrimoireDirectory;

    private readonly ArcanumMaintenanceLock _heldInstallationLock;

    internal StoppedHostGrimoireAuthorityIssuer(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedGrimoireDirectory,
        string canonicalDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedGrimoireDirectory);

        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDatabasePath);

        string canonicalGuardedDirectory = Path.GetFullPath(
            guardedGrimoireDirectory);

        string canonicalPath = Path.GetFullPath(canonicalDatabasePath);

        if (!HasCanonicalRootBinding(
                canonicalGuardedDirectory,
                canonicalPath))
        {
            throw new InvalidOperationException(
                "The stopped-host Grimoire path must belong to the guarded directory.");
        }

        heldInstallationLock.AssertHeldFor(canonicalGuardedDirectory);

        _heldInstallationLock = heldInstallationLock;

        _guardedGrimoireDirectory = canonicalGuardedDirectory;

        _canonicalDatabasePath = canonicalPath;
    }

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetPlanReadAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetPlanRead,
            CovenantSqliteConnectionMode.ReadOnly);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetWorkspaceResolutionAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetWorkspaceResolution,
            CovenantSqliteConnectionMode.ReadOnly);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetIdentityReadAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetIdentityRead,
            CovenantSqliteConnectionMode.ReadOnly);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetHostToolsEvidenceRead,
            CovenantSqliteConnectionMode.ReadOnly);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetApplyAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetApply,
            CovenantSqliteConnectionMode.ReadWrite);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostMarkerPairResetAuthority() =>
        Issue(
            StoppedHostGrimoireOperation.MarkerPairReset,
            CovenantSqliteConnectionMode.ReadWrite);

    public Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostFactoryErasureLockLoan() =>
        Issue(
            StoppedHostGrimoireOperation.InstallationResetFactoryErasure,
            CovenantSqliteConnectionMode.ReadWrite);

    /// <summary>
    /// Spends a factory-erasure loan authority: lends the held installation lock to the accessor the
    /// offline-transition phase authority borrows it from, until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// The phase authority only ever borrows the lock (it opens a journal "under the host's lock"), and
    /// in a stopped-host process nothing attaches one. Looking the lock up ambiently would make the
    /// existence of a held lock enough to start an erasure, so the lock is instead lent by the one
    /// object that holds it and can prove it live: the issuer that minted this authority, from the lock
    /// it was created over. The loan is one-shot (the authority is consumed), is bound to the exact
    /// operation and database path, and ends with the handle, in a <c>finally</c> of the caller's.
    ///
    /// <para>It refuses to nest. If any lock is already attached to the accessor, a loan inside another
    /// loan or beside a serving host's own attachment would detach what it did not attach, so it answers
    /// <see cref="ErrorCodes.Data.ControlPathUnavailable"/> and changes nothing. A lock that is no longer
    /// held is refused the way every stopped-host authority refuses one.</para>
    /// </remarks>
    internal static Result<IAsyncDisposable> LendHeldLock(
        IStoppedHostGrimoireConnectionAuthority authority,
        InstallationResetMaintenanceLockAccessor accessor,
        string expectedCanonicalDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        if (authority is not StoppedHostGrimoireConnectionAuthority stoppedHost)
        {
            return Result<IAsyncDisposable>.Failure(RefusalError());
        }

        Result consumed = stoppedHost.Consume(
            StoppedHostGrimoireOperation.InstallationResetFactoryErasure,
            CovenantSqliteConnectionMode.ReadWrite,
            expectedCanonicalDatabasePath);

        if (consumed.IsFailure)
        {
            return Result<IAsyncDisposable>.Failure(consumed.Error);
        }

        if (accessor.BorrowHeldLock(stoppedHost.GuardedGrimoireDirectory).IsSuccess)
        {
            return Result<IAsyncDisposable>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "An installation maintenance lock is already attached, so the factory-erasure loan was refused."));
        }

        try
        {
            accessor.AttachHostLock(
                stoppedHost.HeldInstallationLock,
                stoppedHost.GuardedGrimoireDirectory);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or IOException
            or UnauthorizedAccessException)
        {
            // Either the lock stopped being held between the consumption above and this line, or another
            // lock was attached in that instant. Both leave nothing attached by this call.
            return Result<IAsyncDisposable>.Failure(RefusalError());
        }

        return Result<IAsyncDisposable>.Success(
            new FactoryErasureLockLoan(accessor, stoppedHost.HeldInstallationLock));
    }

    private sealed class FactoryErasureLockLoan(
        InstallationResetMaintenanceLockAccessor accessor,
        ArcanumMaintenanceLock heldInstallationLock) : IAsyncDisposable
    {
        private int _detached;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _detached, 1) == 0)
            {
                accessor.DetachHostLock(heldInstallationLock);
            }

            return ValueTask.CompletedTask;
        }
    }

    private Result<IStoppedHostGrimoireConnectionAuthority> Issue(
        StoppedHostGrimoireOperation operation,
        CovenantSqliteConnectionMode mode)
    {
        try
        {
            _heldInstallationLock.AssertHeldFor(_guardedGrimoireDirectory);

            return Result<IStoppedHostGrimoireConnectionAuthority>.Success(
                StoppedHostGrimoireConnectionAuthority.Create(
                    _heldInstallationLock,
                    _guardedGrimoireDirectory,
                    _canonicalDatabasePath,
                    operation,
                    mode));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or IOException
            or UnauthorizedAccessException)
        {
            return Refused();
        }
    }

    private static Result<IStoppedHostGrimoireConnectionAuthority> Refused() =>
        Result<IStoppedHostGrimoireConnectionAuthority>.Failure(
            RefusalError());

    internal static Result ConsumeAuthority(
        IStoppedHostGrimoireConnectionAuthority authority,
        StoppedHostGrimoireOperation expectedOperation,
        CovenantSqliteConnectionMode expectedMode,
        string expectedCanonicalDatabasePath) =>
        authority is StoppedHostGrimoireConnectionAuthority stoppedHost
            ? stoppedHost.Consume(
                expectedOperation,
                expectedMode,
                expectedCanonicalDatabasePath)
            : Result.Failure(RefusalError());

    internal static Result RevalidateAuthority(
        IStoppedHostGrimoireConnectionAuthority authority,
        string expectedCanonicalDatabasePath) =>
        authority is StoppedHostGrimoireConnectionAuthority stoppedHost
            ? stoppedHost.Revalidate(expectedCanonicalDatabasePath)
            : Result.Failure(RefusalError());

    private static Error RefusalError() =>
        new(
            ErrorCodes.Covenant.InvalidScope,
            "A stopped-host Grimoire connection requires its exact live installation lock authority.");

    private static bool HasCanonicalRootBinding(
        string guardedGrimoireDirectory,
        string canonicalDatabasePath)
    {
        string? databaseDirectory = Path.GetDirectoryName(
            Path.GetFullPath(canonicalDatabasePath));

        return databaseDirectory is not null
            && PathsEqual(guardedGrimoireDirectory, databaseDirectory);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed class StoppedHostGrimoireConnectionAuthority
        : IStoppedHostGrimoireConnectionAuthority
    {
        private readonly string _canonicalDatabasePath;

        private readonly string _guardedGrimoireDirectory;

        private readonly ArcanumMaintenanceLock _heldInstallationLock;

        private readonly CovenantSqliteConnectionMode _mode;

        private readonly StoppedHostGrimoireOperation _operation;

        private int _consumedOrDisposed;

        private StoppedHostGrimoireConnectionAuthority(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedGrimoireDirectory,
            string canonicalDatabasePath,
            StoppedHostGrimoireOperation operation,
            CovenantSqliteConnectionMode mode)
        {
            _heldInstallationLock = heldInstallationLock;

            _guardedGrimoireDirectory = guardedGrimoireDirectory;

            _canonicalDatabasePath = canonicalDatabasePath;

            _operation = operation;

            _mode = mode;
        }

        internal string GuardedGrimoireDirectory => _guardedGrimoireDirectory;

        internal ArcanumMaintenanceLock HeldInstallationLock => _heldInstallationLock;

        internal static IStoppedHostGrimoireConnectionAuthority Create(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedGrimoireDirectory,
            string canonicalDatabasePath,
            StoppedHostGrimoireOperation operation,
            CovenantSqliteConnectionMode mode) =>
            new StoppedHostGrimoireConnectionAuthority(
                heldInstallationLock,
                guardedGrimoireDirectory,
                canonicalDatabasePath,
                operation,
                mode);

        internal Result Consume(
            StoppedHostGrimoireOperation expectedOperation,
            CovenantSqliteConnectionMode expectedMode,
            string expectedCanonicalDatabasePath)
        {
            if (Interlocked.CompareExchange(ref _consumedOrDisposed, 1, 0) != 0
                || !HasCanonicalRootBinding(
                    _guardedGrimoireDirectory,
                    _canonicalDatabasePath)
                || _operation != expectedOperation
                || _mode != expectedMode
                || !PathsEqual(
                    _canonicalDatabasePath,
                    expectedCanonicalDatabasePath))
            {
                return Refusal();
            }

            return Revalidate(expectedCanonicalDatabasePath);
        }

        internal Result Revalidate(string expectedCanonicalDatabasePath)
        {
            if (!HasCanonicalRootBinding(
                    _guardedGrimoireDirectory,
                    _canonicalDatabasePath)
                || !PathsEqual(
                    _canonicalDatabasePath,
                    expectedCanonicalDatabasePath))
            {
                return Refusal();
            }

            try
            {
                _heldInstallationLock.AssertHeldFor(_guardedGrimoireDirectory);

                return Result.Success();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ObjectDisposedException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return Refusal();
            }
        }

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.CompareExchange(ref _consumedOrDisposed, 1, 0);

            return ValueTask.CompletedTask;
        }

        private static Result Refusal() => Result.Failure(RefusalError());
    }
}
