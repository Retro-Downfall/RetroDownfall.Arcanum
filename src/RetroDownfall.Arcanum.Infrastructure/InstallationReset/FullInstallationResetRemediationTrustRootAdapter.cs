using RetroDownfall.Arcanum.Core.DataLifecycle;

using PinnedTrustRootProvider =
    RetroDownfall.Arcanum.Secrets.Security.FullInstallationResetRemediationTrustRootProvider;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class FullInstallationResetRemediationTrustRootAdapter
    : IFullInstallationResetRemediationTrustRootProvider
{

    private readonly PinnedTrustRootProvider _pinned = new();

    public bool TryResolve(
        string issuer,
        out FullInstallationResetRemediationTrustRoot? trustRoot)
    {

        if (!_pinned.TryResolve(issuer, out byte[] subjectPublicKeyInfo))
        {

            trustRoot = null;

            return false;

        }

        trustRoot = new FullInstallationResetRemediationTrustRoot(
            subjectPublicKeyInfo);

        return true;

    }

}
