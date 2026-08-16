namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Lends the installation-private key that turns a physical directory into an opaque identity.
/// </summary>
/// <remarks>
/// Keyed rather than a plain hash of the device and inode numbers. An unkeyed digest of
/// <c>(volume, file id)</c> is guessable — those values are small integers an attacker can enumerate —
/// so an unkeyed identity would let anyone who can write a marker file claim a registered root
/// (§10.12).
///
/// <para>Domain-separated from the API key, the envelope keys, the cursor keys, and the diagnostic key.
/// It survives API-key rotation and Covenant reset, because a Campaign's registered root does not move
/// when a credential does; only a full installation reset regenerates it, and that is also the point at
/// which every registration is gone anyway.</para>
///
/// <para>Copy-out rather than expose: the caller writes into a buffer it clears, so no key array
/// reference escapes the provider that owns its zeroization. Key loss returns
/// <see langword="false"/>, which renders every Campaign identity unresolved until an authenticated
/// repair — a safe failure, because an unresolved root grants nothing.</para>
/// </remarks>
public interface ICampaignRootIdentityKeyProvider
{

    /// <summary>
    /// Copies the 32-byte root-identity key, or reports that none is available.
    /// </summary>
    bool TryCopyRootIdentityKey(Span<byte> destination);

}
