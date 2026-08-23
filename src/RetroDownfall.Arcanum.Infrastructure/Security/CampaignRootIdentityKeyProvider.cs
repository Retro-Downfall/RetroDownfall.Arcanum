using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Copies only an already-persisted Campaign root-identity key for recovery work.
/// </summary>
/// <remarks>
/// Unlike ordinary first registration, this port never creates a credential. Recovery that cannot
/// prove the existing key must stop before it opens any registered root.
/// </remarks>
internal interface ICampaignRootIdentityRecoveryKeyProvider
{

    /// <summary>Copies the exact 32-byte existing key without generating one.</summary>
    bool TryCopyExistingRootIdentityKey(Span<byte> destination);

}

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
///
/// <para>A failed resolution is cached exactly like a successful one, because it is the failure path
/// that the cache exists for: a store that is unavailable (headless Linux with no Secret Service) or
/// that refuses the read (a macOS keychain ACL invalidated by a resign) fails on every attempt, so
/// retrying per turn buys nothing and costs a warning per turn — or, on macOS, a confidential-information
/// prompt per turn that the operator cannot dismiss for good. Recovery is therefore: repair the
/// credential, then restart the process. There is no in-process repair entry point yet (§10.12).</para>
/// </remarks>
internal sealed class CampaignRootIdentityKeyProvider(IOsCredentialStore credentials)
    : ICampaignRootIdentityKeyProvider, ICampaignRootIdentityRecoveryKeyProvider, IDisposable
{

    /// <summary>The dedicated credential account. Never shared with another Arcanum secret.</summary>
    internal const string Account = ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount;

    private const int KeyBytes = 32;

    private readonly Lock _gate = new();

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private byte[]? _key;

    private bool _resolved;

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

            // Latch on the attempt, not on the result: `_key ??= LoadOrCreate()` would memoise only
            // success and re-enter the OS credential read on every turn for the whole life of a
            // degraded installation.
            if (!_resolved)
            {

                _resolved = true;

                _key = LoadOrCreate();

            }

            if (_key is null)
            {
                return false;
            }

            _key.CopyTo(destination);

            return true;

        }

    }

    /// <inheritdoc />
    public bool TryCopyExistingRootIdentityKey(Span<byte> destination)
    {

        if (destination.Length != KeyBytes)
        {
            return false;
        }

        lock (_gate)
        {

            if (_disposed)
            {
                return false;
            }

            if (_key is null && !_resolved)
            {

                byte[]? existing = LoadExisting();

                if (existing is null)
                {
                    return false;
                }

                _key = existing;

                _resolved = true;

            }

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

    private byte[]? LoadExisting()
    {

        try
        {

            OsCredentialStoreResult existing = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                Account);

            if (existing.Status is not OsCredentialStoreStatus.Ok
                || existing.Value is not { Length: > 0 } stored)
            {
                return null;
            }

            byte[] decoded = Convert.FromBase64String(stored);

            if (decoded.Length == KeyBytes)
            {
                return decoded;
            }

            CryptographicOperations.ZeroMemory(decoded);

            return null;

        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {

            Log.Warning(exception, "The existing Campaign root-identity key could not be resolved.");

            return null;

        }

    }

}
