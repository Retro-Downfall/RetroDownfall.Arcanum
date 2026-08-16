using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Owns the installation-private secret behind every Campaign physical-root identity.
/// </summary>
/// <remarks>
/// Stored under its own OS credential account rather than derived from the master API key, because its
/// lifetime is different: rotating the API key must not invalidate every registered Campaign root, and
/// a Covenant reset must not either. Only a full installation reset removes it, and at that point every
/// registration is gone with it (§10.12).
///
/// <para>Generated lazily on first use and cached for the process. The cache matters on the turn path:
/// resolution runs before every session-backed turn, and an OS keychain read per turn would be both
/// slow and, on some platforms, a user-visible prompt.</para>
///
/// <para>Key loss is not an error here. It returns <see langword="false"/>, every Campaign identity
/// becomes unresolvable, and resolution degrades to Global-only until an authenticated repair. That is
/// strictly safer than the alternative of minting a fresh key, which would silently orphan every
/// registered root while continuing to look healthy.</para>
/// </remarks>
internal sealed class CampaignRootIdentityKeyProvider(IOsCredentialStore credentials)
    : ICampaignRootIdentityKeyProvider, IDisposable
{

    /// <summary>The dedicated credential account. Never shared with another Arcanum secret.</summary>
    internal const string Account = ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount;

    private const int KeyBytes = 32;

    private readonly Lock _gate = new();

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private byte[]? _key;

    private bool _disposed;

    /// <inheritdoc/>
    public bool TryCopyRootIdentityKey(Span<byte> destination)
    {

        if (destination.Length < KeyBytes)
        {
            return false;
        }

        lock (_gate)
        {

            if (_disposed)
            {
                return false;
            }

            _key ??= LoadOrCreate();

            if (_key is null)
            {
                return false;
            }

            _key.CopyTo(destination);

            return true;

        }

    }

    /// <inheritdoc/>
    public void Dispose()
    {

        lock (_gate)
        {

            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_key is { } key)
            {

                CryptographicOperations.ZeroMemory(key);

                _key = null;

            }

        }

    }

    private byte[]? LoadOrCreate()
    {

        try
        {

            OsCredentialStoreResult existing = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                Account);

            if (existing.Status is OsCredentialStoreStatus.Ok && existing.Value is { Length: > 0 } stored)
            {

                byte[] decoded = Convert.FromBase64String(stored);

                if (decoded.Length == KeyBytes)
                {
                    return decoded;
                }

                CryptographicOperations.ZeroMemory(decoded);

                Log.Warning(
                    "The Campaign root-identity key is malformed; Campaign path identities stay unresolved until it is repaired.");

                return null;

            }

            if (existing.Status is not OsCredentialStoreStatus.NotFound)
            {

                Log.Warning(
                    "The Campaign root-identity key could not be read ({Status}); Campaign path identities stay unresolved.",
                    existing.Status);

                return null;

            }

            byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

            OsCredentialStoreResult written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                Account,
                Convert.ToBase64String(created));

            if (written.Status is not OsCredentialStoreStatus.Ok)
            {

                CryptographicOperations.ZeroMemory(created);

                Log.Warning(
                    "The Campaign root-identity key could not be created ({Status}); Campaign path identities stay unresolved.",
                    written.Status);

                return null;

            }

            return created;

        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {

            Log.Warning(exception, "The Campaign root-identity key could not be resolved.");

            return null;

        }

    }

}
