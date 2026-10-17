using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Asks the credential store whether one account exists, without requesting its secret bytes.
/// </summary>
/// <remarks>
/// The owner of an account asks about its own account and passes the name here, so no caller outside
/// the owner derives an account name to find out whether it is there. <see cref="OsCredentialStoreStatus.Ok"/>
/// means present, <see cref="OsCredentialStoreStatus.NotFound"/> proves absent, and anything else is
/// indeterminate: a store with no metadata-only lookup, a backend that is missing or cannot be loaded,
/// and a backend that fails are all <see cref="OsCredentialStoreStatus.Unavailable"/>, never absence and
/// never a reason to fall back to a read that could prompt.
/// </remarks>
internal static class CredentialPresenceProbe
{
    internal static OsCredentialStoreStatus Probe(IOsCredentialStore credentials, string account)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        if (credentials is not IOsCredentialPresenceProbe presence)
        {
            return OsCredentialStoreStatus.Unavailable;
        }

        try
        {
            return presence.ProbePresence(ArcanumCredentialIdentity.Service, account);
        }
        catch (Exception exception) when (InstallationResetCredentialStoreFailures.IsFailure(exception))
        {
            return OsCredentialStoreStatus.Unavailable;
        }
    }
}
