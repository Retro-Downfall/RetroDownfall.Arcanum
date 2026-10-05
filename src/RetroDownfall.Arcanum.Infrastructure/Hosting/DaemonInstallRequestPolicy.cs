using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// The rule the per-user daemon managers share: a launchd user agent and a systemd user unit always run as the
/// person who installed them, so a request that names another account is refused rather than silently ignored, which
/// would leave the caller believing the daemon runs under an account it does not.
/// </summary>
internal static class DaemonInstallRequestPolicy
{
    internal const string ServiceAccountUnsupportedCode = "DaemonServiceAccountUnsupported";

    /// <summary>
    /// The refusal for a request that carries a service account, or <see langword="null"/> when it carries none.
    /// </summary>
    internal static Error? RefuseServiceAccount(DaemonInstallRequest request, string managerKind)
    {
        if (request.ServiceAccount is null)
        {
            return null;
        }

        return new Error(
            ServiceAccountUnsupportedCode,
            $"{managerKind} always runs as the user who installs it, so it cannot be installed under a different account.");
    }
}
