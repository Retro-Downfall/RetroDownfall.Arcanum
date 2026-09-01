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

        heldInstallationLock.AssertHeldFor(guardedGrimoireDirectory);

        _heldInstallationLock = heldInstallationLock;

        _guardedGrimoireDirectory = Path.GetFullPath(guardedGrimoireDirectory);

        _canonicalDatabasePath = Path.GetFullPath(canonicalDatabasePath);

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

    private Result<IStoppedHostGrimoireConnectionAuthority> Issue(
        StoppedHostGrimoireOperation operation,
        CovenantSqliteConnectionMode mode)
    {

        try
        {

            _heldInstallationLock.AssertHeldFor(_guardedGrimoireDirectory);

            return Result<IStoppedHostGrimoireConnectionAuthority>.Success(
                new StoppedHostGrimoireConnectionAuthority(
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
            StoppedHostGrimoireConnectionAuthority.RefusalError());

}

internal sealed class StoppedHostGrimoireConnectionAuthority
    : IStoppedHostGrimoireConnectionAuthority
{

    private readonly string _canonicalDatabasePath;

    private readonly string _guardedGrimoireDirectory;

    private readonly ArcanumMaintenanceLock _heldInstallationLock;

    private readonly CovenantSqliteConnectionMode _mode;

    private readonly StoppedHostGrimoireOperation _operation;

    private int _consumedOrDisposed;

    internal StoppedHostGrimoireConnectionAuthority(
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

    internal Result Consume(
        StoppedHostGrimoireOperation expectedOperation,
        CovenantSqliteConnectionMode expectedMode,
        string expectedCanonicalDatabasePath)
    {

        if (Interlocked.CompareExchange(ref _consumedOrDisposed, 1, 0) != 0
            || _operation != expectedOperation
            || _mode != expectedMode
            || !PathsEqual(_canonicalDatabasePath, expectedCanonicalDatabasePath))
        {

            return Refusal();

        }

        return Revalidate(expectedCanonicalDatabasePath);

    }

    internal Result Revalidate(string expectedCanonicalDatabasePath)
    {

        if (!PathsEqual(_canonicalDatabasePath, expectedCanonicalDatabasePath))
        {

            return Refusal();

        }

        try
        {

            _heldInstallationLock.AssertHeldFor(_guardedGrimoireDirectory);

            return Result.Success();

        }
        catch (Exception exception) when (exception is InvalidOperationException
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

    internal static Error RefusalError() =>
        new(
            ErrorCodes.Covenant.InvalidScope,
            "A stopped-host Grimoire connection requires its exact live installation lock authority.");

    private static Result Refusal() => Result.Failure(RefusalError());

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

}
