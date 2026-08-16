using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The six things Arcanum will ever hand an operator as an opaque authenticated token.
/// </summary>
/// <remarks>
/// Closed and exhaustive, with fixed codes and fixed labels. The label is domain separation: two
/// purposes derive different keys, so a cursor can never be replayed as a mutation preflight even
/// though both are base64url strings of similar length (§10.12).
///
/// <para>The split at code four is deliberate. Purposes one through three describe Covenant dataset
/// state and are re-keyed whenever that dataset is reset, restored, or reinitialized. Purposes four
/// through six describe installation-level recovery and must survive a Covenant dataset that no longer
/// exists, so they derive from the core installation identity and recovery epoch instead.</para>
/// </remarks>
public enum CovenantEnvelopePurpose : byte
{

    /// <summary>An opaque pagination cursor over a Covenant read.</summary>
    Cursor = 1,

    /// <summary>A prepared operator mutation, bound to the state it was prepared against.</summary>
    OperatorPreflight = 2,

    /// <summary>An admitted agent retirement awaiting a Forbidden-Art Ward decision.</summary>
    WardRetirement = 3,

    /// <summary>A prepared Covenant schema-family reinitialize.</summary>
    FamilyReinitialize = 4,

    /// <summary>A prepared Campaign physical-root registration, repair, or takeover.</summary>
    CampaignPathIdentity = 5,

    /// <summary>A prepared resolution of a legacy-unresolved Session's immutable binding.</summary>
    SessionCampaignBinding = 6,

}

/// <summary>
/// Why a token was refused, with no detail an attacker could use to steer the next attempt.
/// </summary>
/// <remarks>
/// Every cryptographic failure — wrong key, wrong epoch, tampered ciphertext, forged tag — collapses
/// into <see cref="Invalid"/>. Distinguishing them would turn the decoder into an oracle. The
/// remaining codes describe conditions the caller can legitimately act on: a token that simply aged
/// out, or one whose purpose says it was meant for a different route.
/// </remarks>
public enum CovenantEnvelopeDecodeFailure : byte
{

    /// <summary>Malformed, out of bounds, or failed authentication. Deliberately undifferentiated.</summary>
    Invalid = 1,

    /// <summary>Authenticated, but its lifetime has elapsed.</summary>
    Expired = 2,

    /// <summary>Authenticated, but issued for a different purpose than this route accepts.</summary>
    PurposeMismatch = 3,

}

/// <summary>
/// One decoded, authenticated envelope body.
/// </summary>
/// <remarks>
/// The timestamps are returned from the authenticated plaintext, never from the header, even though
/// the two are proven equal during decode. Returning the header copy would mean a caller reasoning
/// about a value that was only ever covered as associated data.
/// </remarks>
public sealed record CovenantEnvelopeBody(
    CovenantEnvelopePurpose Purpose,
    uint MasterKeyVersion,
    long EnvelopeEpoch,
    ulong Counter,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    byte[] Payload);

/// <summary>
/// The nonsecret identity of the key material currently backing one envelope purpose family.
/// </summary>
public sealed record CovenantEnvelopeKeySnapshot(
    uint MasterKeyVersion,
    long CanonicalEnvelopeEpoch,
    long RecoveryEnvelopeEpoch,
    string InstallationIdentity,
    Guid? DatasetGeneration);

/// <summary>
/// The hard bounds every envelope is parsed and issued under.
/// </summary>
/// <remarks>
/// Enforced before any allocation sized from attacker-supplied length fields. A decoder that
/// allocated first and validated second would be a denial-of-service surface reachable by anyone who
/// can reach an authenticated route with a wrong key.
/// </remarks>
public static class CovenantEnvelopeLimits
{

    /// <summary>The exact ASCII magic every envelope starts with.</summary>
    public const string Magic = "ACVE";

    /// <summary>The only wire version this build issues or accepts.</summary>
    public const byte Version = 1;

    /// <summary>The exact header length, which is also the associated-data length.</summary>
    public const int HeaderBytes = 46;

    /// <summary>AES-GCM nonce length, in bytes.</summary>
    public const int NonceBytes = 12;

    /// <summary>AES-GCM authentication tag length, in bytes.</summary>
    public const int TagBytes = 16;

    /// <summary>The two eight-byte timestamps the plaintext repeats ahead of its payload.</summary>
    public const int BodyTimeBytes = 16;

    /// <summary>The largest caller payload one envelope may carry.</summary>
    public const int MaxPayloadBytes = 2048;

    /// <summary>The largest encoded token this decoder will look at.</summary>
    public const int MaxTokenCharacters = 4096;

    /// <summary>The issuance ordinal at which a purpose family must re-key rather than continue.</summary>
    public const ulong CounterRolloverBound = uint.MaxValue;

    /// <summary>The longest lifetime an envelope may be issued for.</summary>
    public static TimeSpan MaxLifetime => TimeSpan.FromHours(1);

    /// <summary>
    /// The fixed domain-separation label for one purpose.
    /// </summary>
    public static string Label(CovenantEnvelopePurpose purpose) =>
        purpose switch
        {
            CovenantEnvelopePurpose.Cursor => "Arcanum.Covenant.Cursor.v1",
            CovenantEnvelopePurpose.OperatorPreflight => "Arcanum.Covenant.OperatorPreflight.v1",
            CovenantEnvelopePurpose.WardRetirement => "Arcanum.Covenant.WardRetirement.v1",
            CovenantEnvelopePurpose.FamilyReinitialize => "Arcanum.Covenant.FamilyReinitialize.v1",
            CovenantEnvelopePurpose.CampaignPathIdentity => "Arcanum.Campaign.PathIdentity.v1",
            CovenantEnvelopePurpose.SessionCampaignBinding => "Arcanum.Session.CampaignBinding.v1",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

    /// <summary>
    /// Whether this purpose is keyed by Covenant dataset state rather than installation recovery state.
    /// </summary>
    public static bool IsDatasetKeyed(CovenantEnvelopePurpose purpose) =>
        purpose is CovenantEnvelopePurpose.Cursor
            or CovenantEnvelopePurpose.OperatorPreflight
            or CovenantEnvelopePurpose.WardRetirement;

}

/// <summary>
/// The exact nonsecret facts one committed authority transition establishes.
/// </summary>
/// <remarks>
/// Validated on construction and carried whole. A transition that could be published field by field
/// would let a reader observe a new dataset generation beside the old envelope epoch, which is exactly
/// the pairing that would let a token minted before a reset authenticate after it.
/// </remarks>
public sealed record CovenantCommittedAuthorityTransition
{

    public CovenantCommittedAuthorityTransition(
        string installationIdentity,
        long authorityEpoch,
        uint masterKeyVersion,
        long canonicalEnvelopeEpoch,
        long recoveryEnvelopeEpoch,
        long capabilityGeneration,
        Guid? datasetGeneration,
        bool covenantEnabled)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentity);

        CovenantValidation.RequirePositive(authorityEpoch, nameof(authorityEpoch));

        CovenantValidation.RequirePositive(masterKeyVersion, nameof(masterKeyVersion));

        CovenantValidation.RequirePositive(canonicalEnvelopeEpoch, nameof(canonicalEnvelopeEpoch));

        CovenantValidation.RequirePositive(recoveryEnvelopeEpoch, nameof(recoveryEnvelopeEpoch));

        CovenantValidation.RequirePositive(capabilityGeneration, nameof(capabilityGeneration));

        if (datasetGeneration == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty dataset generation is indistinguishable from an absent one.",
                nameof(datasetGeneration));
        }

        InstallationIdentity = installationIdentity;

        AuthorityEpoch = authorityEpoch;

        MasterKeyVersion = masterKeyVersion;

        CanonicalEnvelopeEpoch = canonicalEnvelopeEpoch;

        RecoveryEnvelopeEpoch = recoveryEnvelopeEpoch;

        CapabilityGeneration = capabilityGeneration;

        DatasetGeneration = datasetGeneration;

        CovenantEnabled = covenantEnabled;

    }

    public string InstallationIdentity { get; }

    public long AuthorityEpoch { get; }

    public uint MasterKeyVersion { get; }

    public long CanonicalEnvelopeEpoch { get; }

    public long RecoveryEnvelopeEpoch { get; }

    public long CapabilityGeneration { get; }

    public Guid? DatasetGeneration { get; }

    public bool CovenantEnabled { get; }

}

/// <summary>
/// Helper factories for the content-free errors every envelope surface returns.
/// </summary>
public static class CovenantEnvelopeErrors
{

    public static Error For(CovenantEnvelopeDecodeFailure failure) =>
        failure switch
        {
            CovenantEnvelopeDecodeFailure.Expired => new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This token has expired. Prepare the operation again."),
            CovenantEnvelopeDecodeFailure.PurposeMismatch => new Error(
                ErrorCodes.Covenant.InvalidCursor,
                "This token was not issued for this operation."),
            _ => new Error(
                ErrorCodes.Covenant.InvalidCursor,
                "This token is not valid."),
        };

}
