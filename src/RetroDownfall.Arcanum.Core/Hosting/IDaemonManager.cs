using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Hosting;

public interface IDaemonManager
{
    /// <summary>
    /// <see langword="true"/> when <see cref="InstallAsync"/> cannot succeed without an explicit
    /// <see cref="DaemonInstallRequest.ServiceAccount"/>. A Windows Service created without an account runs as
    /// LocalSystem, whose profile, data directory and API key are not the invoking user's, so the Windows manager
    /// refuses to install one. The launchd agent and systemd user unit managers always run as the invoking user and
    /// need none.
    /// </summary>
    bool RequiresServiceAccount { get; }

    Task<Result> InstallAsync(DaemonInstallRequest request, CancellationToken cancellationToken);

    Task<Result> UninstallAsync(CancellationToken cancellationToken);

    Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a daemon install is asked to do. <see cref="ServiceAccount"/> is the account a Windows Service runs under;
/// it is <see langword="null"/> for the managers that run the daemon as the invoking user.
/// </summary>
public sealed record DaemonInstallRequest(DaemonServiceCredential? ServiceAccount)
{
    /// <summary>
    /// An install that names no account, which is every install on macOS and Linux.
    /// </summary>
    public static DaemonInstallRequest ForInvokingUser { get; } = new((DaemonServiceCredential?)null);
}

/// <summary>
/// The account and password a Windows Service is created under. Its <see cref="ToString"/> never includes the
/// password, so a request that is logged or reported in an exception does not leak it.
/// </summary>
public sealed class DaemonServiceCredential(string accountName, string password)
{
    public string AccountName { get; } = accountName;

    public string Password { get; } = password;

    public override string ToString() =>
        $"{nameof(DaemonServiceCredential)} {{ {nameof(AccountName)} = {AccountName}, {nameof(Password)} = <redacted> }}";
}
