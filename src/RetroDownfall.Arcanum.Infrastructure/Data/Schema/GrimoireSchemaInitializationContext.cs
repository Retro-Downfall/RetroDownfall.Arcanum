namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The installation-local facts every tier initializer needs, resolved once by the bootstrap caller
/// under its held installation lock and passed unchanged into all three install transactions.
/// </summary>
/// <remarks>
/// These values are deliberately parameters rather than ambient lookups. An initializer runs inside
/// a live transaction on a connection it does not own; letting it reach for a secret store or a
/// clock there would put I/O of unknown duration inside the startup-blocking core transaction, and
/// would make two tiers able to disagree about the same installation.
///
/// <para><see cref="MasterKeyFingerprint"/> is a content-free 32-byte digest, never key material:
/// <c>SHA-256(UTF8("Arcanum.Covenant.MasterKeyFingerprint.v1\0") || keyBytes)</c>.</para>
/// </remarks>
internal sealed record GrimoireSchemaInitializationContext(
    string InstallationIdentity,
    long AuthorityEpoch,
    uint MasterKeyVersion,
    byte[] MasterKeyFingerprint,
    long RecoveryEnvelopeEpoch,
    DateTimeOffset InstalledAtUtc);
