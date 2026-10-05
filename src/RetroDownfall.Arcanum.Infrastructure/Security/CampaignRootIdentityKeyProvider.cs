using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Copies only an already-persisted Campaign root-identity key for recovery work.
/// </summary>
/// <remarks>
/// Like the ordinary read port, this one never creates a credential. Recovery that cannot prove the
/// existing key must stop before it opens any registered root.
/// </remarks>
internal interface ICampaignRootIdentityRecoveryKeyProvider
{
    /// <summary>Copies the exact 32-byte existing key without generating one.</summary>
    bool TryCopyExistingRootIdentityKey(Span<byte> destination);
}

/// <summary>The outcome of asking for the Campaign root-identity key to exist.</summary>
internal enum CampaignRootIdentityKeyState
{
    /// <summary>The key was already in the credential store.</summary>
    Present,

    /// <summary>The account was absent, no root was registered, and this call wrote and read back a new key.</summary>
    Created,

    /// <summary>The account is absent while registered roots exist, so no replacement was created.</summary>
    Lost,

    /// <summary>The store failed, or holds a value that is not a key, or did not return what was written.</summary>
    Unavailable,
}

/// <summary>
/// The one entry point that may create the Campaign root-identity key.
/// </summary>
/// <remarks>
/// Taking the caller's evidence is what keeps creation safe: the provider cannot look at the Grimoire
/// itself, and a key that is missing while a registered root exists is a lost key, not a new
/// installation. A reader never creates; only the registration flow, which has the Grimoire connection
/// and has just counted the registered roots, does.
/// </remarks>
internal interface ICampaignRootIdentityKeyCreator
{
    /// <summary>
    /// Makes sure the key exists, creating it only when the account is absent and no root is registered.
    /// </summary>
    /// <param name="registeredRootsExist">
    /// True when any Campaign root registration (or marker cleanup intent) is recorded, so a missing key
    /// can only be a lost one.
    /// </param>
    CampaignRootIdentityKeyState OpenOrCreateRootIdentityKey(bool registeredRootsExist);
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
/// <para>Loaded lazily on first use and cached for the process. The cache matters on the turn path:
/// resolution runs before every session-backed turn, and an OS keychain read per turn would be both
/// slow and, on some platforms, a user-visible prompt.</para>
///
/// <para>Only <see cref="OpenOrCreateRootIdentityKey"/> creates the key, and only when the account is
/// absent and the caller reports that no root is registered. Key loss is not an error and not a reason
/// to mint: a missing key returns <see langword="false"/> from every read, every Campaign identity
/// becomes unresolvable, and resolution degrades to Global-only until an authenticated repair. That is
/// strictly safer than minting a fresh key, which would silently orphan every registered root while
/// continuing to look healthy. A reader that creates on first use would do exactly that on an
/// installation whose key had merely been lost, which is why no read port creates.</para>
///
/// <para>A failed resolution is cached exactly like a successful one, because it is the failure path
/// that the cache exists for: a store that is unavailable (headless Linux with no Secret Service) or
/// that refuses the read (a macOS keychain ACL invalidated by a resign) fails on every attempt, so
/// retrying per turn buys nothing and costs a warning per turn — or, on macOS, a confidential-information
/// prompt per turn that the operator cannot dismiss for good. An absent account is cached for ordinary
/// reads too (one probe per process), but it is not a failure: the registration entry point and the
/// recovery read both look again, so a key created since is found without a restart. Recovery from a
/// failed or malformed credential is to repair it, then restart the process. There is no in-process
/// repair entry point yet (§10.12).</para>
/// </remarks>
internal sealed class CampaignRootIdentityKeyProvider(IOsCredentialStore credentials)
    : ICampaignRootIdentityKeyProvider,
        ICampaignRootIdentityRecoveryKeyProvider,
        ICampaignRootIdentityKeyCreator,
        IDisposable
{
    /// <summary>The dedicated credential account. Never shared with another Arcanum secret.</summary>
    internal const string Account = ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount;

    private const int KeyBytes = 32;

    private readonly Lock _gate = new();

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private byte[]? _key;

    private Resolution _resolution;

    private bool _disposed;

    /// <summary>What the process has learned about the credential account so far.</summary>
    private enum Resolution
    {
        /// <summary>No ordinary read has looked yet.</summary>
        Unresolved,

        /// <summary>The key is held in <c>_key</c>.</summary>
        Present,

        /// <summary>The account was absent when last looked at. Not a failure: it can be created.</summary>
        Absent,

        /// <summary>The store refused or the value was damaged; latched so a turn never retries it.</summary>
        Failed,
    }

    private enum ProbeOutcome
    {
        Present,

        Absent,

        Malformed,

        Unreadable,
    }

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

            // Latch on the attempt, not on the result: memoising only success would re-enter the OS
            // credential read on every turn for the whole life of a degraded or keyless installation.
            if (_resolution is Resolution.Unresolved)
            {
                (ProbeOutcome outcome, byte[]? key) = Probe(ordinary: true);

                if (outcome is ProbeOutcome.Present)
                {
                    _key = key;

                    _resolution = Resolution.Present;
                }
                else
                {
                    _resolution = outcome is ProbeOutcome.Absent
                        ? Resolution.Absent
                        : Resolution.Failed;
                }
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

            // A latched failure stays latched, but an absent account is looked at again: recovery is an
            // operator call, and a miss here is never cached, so a key created since is found.
            if (_key is null && _resolution is not Resolution.Failed)
            {
                (ProbeOutcome outcome, byte[]? existing) = Probe(ordinary: false);

                if (outcome is not ProbeOutcome.Present)
                {
                    return false;
                }

                _key = existing;

                _resolution = Resolution.Present;
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
    public CampaignRootIdentityKeyState OpenOrCreateRootIdentityKey(bool registeredRootsExist)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return CampaignRootIdentityKeyState.Unavailable;
            }

            if (_key is not null)
            {
                return CampaignRootIdentityKeyState.Present;
            }

            // Always look: whatever an earlier read latched was true then, and the evidence the caller
            // is acting on is true now.
            (ProbeOutcome outcome, byte[]? existing) = Probe(ordinary: true);

            switch (outcome)
            {
                case ProbeOutcome.Present:

                    _key = existing;

                    _resolution = Resolution.Present;

                    return CampaignRootIdentityKeyState.Present;

                case ProbeOutcome.Absent when registeredRootsExist:

                    _resolution = Resolution.Absent;

                    Log.Warning(
                        "The Campaign root-identity key is missing while Campaign roots are registered; no replacement is created "
                        + "because it would orphan every registered root. Restore the credential, or reset the installation.");

                    return CampaignRootIdentityKeyState.Lost;

                case ProbeOutcome.Absent:

                    return Create();

                default:

                    _resolution = Resolution.Failed;

                    return CampaignRootIdentityKeyState.Unavailable;
            }
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

    /// <summary>
    /// Writes a new key and publishes it only after reading back exactly what was written.
    /// </summary>
    /// <remarks>
    /// An echo of what was handed to the store is not proof of persistence, so nothing is recorded under
    /// a key the store cannot return. Every refusal here leaves the process unresolved, so the next read
    /// looks at the account again instead of caching a state this call did not establish.
    /// </remarks>
    private CampaignRootIdentityKeyState Create()
    {
        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        try
        {
            OsCredentialStoreResult written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                Account,
                Convert.ToBase64String(created));

            if (written.Status is not OsCredentialStoreStatus.Ok)
            {
                Log.Warning(
                    "The Campaign root-identity key could not be created ({Status}); Campaign path identities stay unresolved.",
                    written.Status);

                _resolution = Resolution.Unresolved;

                return CampaignRootIdentityKeyState.Unavailable;
            }

            (ProbeOutcome outcome, byte[]? confirmed) = Probe(ordinary: false);

            if (outcome is not ProbeOutcome.Present
                || confirmed is null
                || !CryptographicOperations.FixedTimeEquals(confirmed, created))
            {
                if (confirmed is not null)
                {
                    CryptographicOperations.ZeroMemory(confirmed);
                }

                Log.Warning(
                    "The Campaign root-identity key could not be read back after it was written; Campaign path identities stay unresolved.");

                _resolution = Resolution.Unresolved;

                return CampaignRootIdentityKeyState.Unavailable;
            }

            _key = confirmed;

            _resolution = Resolution.Present;

            return CampaignRootIdentityKeyState.Created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created);
        }
    }

    /// <summary>
    /// Classifies one read of the account. <see cref="ProbeOutcome.Present"/> always carries the key.
    /// </summary>
    /// <param name="ordinary">
    /// True for the readers and the registration path, which say why a key is unusable; false for the
    /// recovery read and the read-back, whose callers report their own outcome.
    /// </param>
    private (ProbeOutcome Outcome, byte[]? Key) Probe(bool ordinary)
    {
        try
        {
            OsCredentialStoreResult existing = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                Account);

            if (existing.Status is OsCredentialStoreStatus.NotFound)
            {
                return (ProbeOutcome.Absent, null);
            }

            if (existing.Status is not OsCredentialStoreStatus.Ok)
            {
                if (ordinary)
                {
                    Log.Warning(
                        "The Campaign root-identity key could not be read ({Status}); Campaign path identities stay unresolved.",
                        existing.Status);
                }

                return (ProbeOutcome.Unreadable, null);
            }

            // An Ok with an empty value is malformed, not absent: an absent account reports NotFound.
            if (existing.Value is not { Length: > 0 } stored)
            {
                return (ProbeOutcome.Malformed, null);
            }

            byte[] decoded = Convert.FromBase64String(stored);

            if (decoded.Length == KeyBytes)
            {
                return (ProbeOutcome.Present, decoded);
            }

            CryptographicOperations.ZeroMemory(decoded);

            if (ordinary)
            {
                Log.Warning(
                    "The Campaign root-identity key is malformed; Campaign path identities stay unresolved until it is repaired.");
            }

            return (ProbeOutcome.Malformed, null);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            Log.Warning(
                exception,
                ordinary
                    ? "The Campaign root-identity key could not be resolved."
                    : "The existing Campaign root-identity key could not be resolved.");

            return (ProbeOutcome.Malformed, null);
        }
    }
}
