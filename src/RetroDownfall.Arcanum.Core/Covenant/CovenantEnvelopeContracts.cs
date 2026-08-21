using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

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
public sealed record CovenantCommittedCapabilityTransition
{

    public CovenantCommittedCapabilityTransition(
        long ExpectedGeneration,
        long Generation,
        bool FeatureEnabled,
        CovenantCapabilityState Canonical,
        int? CanonicalSchemaVersion,
        string? CanonicalInstalledFingerprint,
        CovenantCapabilityState Accelerator,
        int? AcceleratorSchemaVersion,
        string? AcceleratorInstalledFingerprint,
        Guid DatasetGeneration,
        long CanonicalSequence,
        long CoreCampaignDeletionSequence,
        long CanonicalAppliedCampaignDeletionSequence,
        long CanonicalAppliedSessionDeletionSequence,
        Guid? AppliedDatasetGeneration,
        long? AppliedSequence,
        long? AppliedCampaignDeletionSequence,
        ulong AcceleratorEpoch,
        CovenantFtsSynchronizationState FtsSynchronization,
        bool RebuildRequired,
        long CleanupAppliedCampaignSequence,
        long CleanupAppliedSessionSequence,
        bool CleanupFullSweepRequired,
        string? CanonicalDiagnosticCode,
        string? AcceleratorDiagnosticCode)
    {

        this.ExpectedGeneration =
            CovenantValidation.RequirePositive(ExpectedGeneration, nameof(ExpectedGeneration));

        this.Generation = RequireNextGeneration(this.ExpectedGeneration, Generation);

        this.FeatureEnabled = FeatureEnabled;

        this.Canonical = ValidateTier(
            Canonical,
            CanonicalSchemaVersion,
            CanonicalInstalledFingerprint,
            CanonicalDiagnosticCode,
            nameof(Canonical));

        this.CanonicalSchemaVersion = CanonicalSchemaVersion;

        this.CanonicalInstalledFingerprint = CanonicalInstalledFingerprint;

        this.Accelerator = ValidateTier(
            Accelerator,
            AcceleratorSchemaVersion,
            AcceleratorInstalledFingerprint,
            AcceleratorDiagnosticCode,
            nameof(Accelerator));

        this.AcceleratorSchemaVersion = AcceleratorSchemaVersion;

        this.AcceleratorInstalledFingerprint = AcceleratorInstalledFingerprint;

        this.DatasetGeneration =
            CovenantValidation.RequireNonEmpty(DatasetGeneration, nameof(DatasetGeneration));

        this.CanonicalSequence = RequireNonNegative(CanonicalSequence, nameof(CanonicalSequence));

        this.CoreCampaignDeletionSequence = RequireNonNegative(
            CoreCampaignDeletionSequence,
            nameof(CoreCampaignDeletionSequence));

        this.CanonicalAppliedCampaignDeletionSequence = RequireNonNegative(
            CanonicalAppliedCampaignDeletionSequence,
            nameof(CanonicalAppliedCampaignDeletionSequence));

        this.CanonicalAppliedSessionDeletionSequence = RequireNonNegative(
            CanonicalAppliedSessionDeletionSequence,
            nameof(CanonicalAppliedSessionDeletionSequence));

        this.AppliedDatasetGeneration = ValidateAppliedTuple(
            AppliedDatasetGeneration,
            AppliedSequence,
            AppliedCampaignDeletionSequence);

        this.AppliedSequence = AppliedSequence;

        this.AppliedCampaignDeletionSequence = AppliedCampaignDeletionSequence;

        this.AcceleratorEpoch = AcceleratorEpoch;

        this.FtsSynchronization = Enum.IsDefined(FtsSynchronization)
            ? FtsSynchronization
            : throw new ArgumentOutOfRangeException(nameof(FtsSynchronization));

        this.RebuildRequired = RebuildRequired;

        this.CleanupAppliedCampaignSequence = RequireNonNegative(
            CleanupAppliedCampaignSequence,
            nameof(CleanupAppliedCampaignSequence));

        this.CleanupAppliedSessionSequence = RequireNonNegative(
            CleanupAppliedSessionSequence,
            nameof(CleanupAppliedSessionSequence));

        this.CleanupFullSweepRequired = CleanupFullSweepRequired;

        this.CanonicalDiagnosticCode = CanonicalDiagnosticCode;

        this.AcceleratorDiagnosticCode = AcceleratorDiagnosticCode;

    }

    public long ExpectedGeneration { get; }

    public long Generation { get; }

    public bool FeatureEnabled { get; }

    public CovenantCapabilityState Canonical { get; }

    public int? CanonicalSchemaVersion { get; }

    public string? CanonicalInstalledFingerprint { get; }

    public CovenantCapabilityState Accelerator { get; }

    public int? AcceleratorSchemaVersion { get; }

    public string? AcceleratorInstalledFingerprint { get; }

    public Guid DatasetGeneration { get; }

    public long CanonicalSequence { get; }

    public long CoreCampaignDeletionSequence { get; }

    public long CanonicalAppliedCampaignDeletionSequence { get; }

    public long CanonicalAppliedSessionDeletionSequence { get; }

    public Guid? AppliedDatasetGeneration { get; }

    public long? AppliedSequence { get; }

    public long? AppliedCampaignDeletionSequence { get; }

    public ulong AcceleratorEpoch { get; }

    public CovenantFtsSynchronizationState FtsSynchronization { get; }

    public bool RebuildRequired { get; }

    public long CleanupAppliedCampaignSequence { get; }

    public long CleanupAppliedSessionSequence { get; }

    public bool CleanupFullSweepRequired { get; }

    public string? CanonicalDiagnosticCode { get; }

    public string? AcceleratorDiagnosticCode { get; }

    private static long RequireNextGeneration(long expectedGeneration, long generation)
    {

        CovenantValidation.RequirePositive(generation, nameof(Generation));

        if (expectedGeneration == long.MaxValue || generation != expectedGeneration + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Generation),
                "A committed capability transition advances exactly one generation.");
        }

        return generation;

    }

    private static long RequireNonNegative(long value, string parameterName) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static Guid? ValidateAppliedTuple(
        Guid? datasetGeneration,
        long? sequence,
        long? campaignDeletionSequence)
    {

        bool complete = datasetGeneration is not null
            && sequence is not null
            && campaignDeletionSequence is not null;

        if (!complete
            && (datasetGeneration is not null
                || sequence is not null
                || campaignDeletionSequence is not null))
        {
            throw new ArgumentException(
                "The applied accelerator position is present as one complete tuple.",
                nameof(AppliedDatasetGeneration));
        }

        if (datasetGeneration == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty applied dataset generation is invalid.",
                nameof(AppliedDatasetGeneration));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AppliedSequence));
        }

        if (campaignDeletionSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AppliedCampaignDeletionSequence));
        }

        return datasetGeneration;

    }

    private static CovenantCapabilityState ValidateTier(
        CovenantCapabilityState state,
        int? schemaVersion,
        string? installedFingerprint,
        string? diagnosticCode,
        string parameterName)
    {

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        if (state == CovenantCapabilityState.Healthy)
        {
            if (schemaVersion is null or <= 0
                || string.IsNullOrWhiteSpace(installedFingerprint)
                || diagnosticCode is not null)
            {
                throw new ArgumentException(
                    "A healthy capability tier requires installed schema metadata and no diagnostic code.",
                    parameterName);
            }
        }
        else if (schemaVersion is not null || string.IsNullOrWhiteSpace(diagnosticCode))
        {
            throw new ArgumentException(
                "An unhealthy capability tier carries no schema version and requires a diagnostic code.",
                parameterName);
        }

        return state;

    }

}

/// <summary>
/// The exact nonsecret facts one committed authority transition establishes.
/// </summary>
public sealed record CovenantCommittedAuthorityTransition
{

    public CovenantCommittedAuthorityTransition(
        string installationIdentity,
        long authorityEpoch,
        uint masterKeyVersion,
        long canonicalEnvelopeEpoch,
        long recoveryEnvelopeEpoch,
        CovenantHostToolsState hostToolsState,
        string? transitionId,
        CovenantCommittedCapabilityTransition capability)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentity);

        CovenantValidation.RequirePositive(authorityEpoch, nameof(authorityEpoch));

        CovenantValidation.RequirePositive(masterKeyVersion, nameof(masterKeyVersion));

        CovenantValidation.RequirePositive(canonicalEnvelopeEpoch, nameof(canonicalEnvelopeEpoch));

        CovenantValidation.RequirePositive(recoveryEnvelopeEpoch, nameof(recoveryEnvelopeEpoch));

        if (!Enum.IsDefined(hostToolsState))
        {
            throw new ArgumentOutOfRangeException(nameof(hostToolsState));
        }

        if ((hostToolsState == CovenantHostToolsState.Clean && transitionId is not null)
            || (hostToolsState != CovenantHostToolsState.Clean
                && string.IsNullOrWhiteSpace(transitionId)))
        {
            throw new ArgumentException(
                "The transition identity must agree with the host-tools state.",
                nameof(transitionId));
        }

        InstallationIdentity = installationIdentity;

        AuthorityEpoch = authorityEpoch;

        MasterKeyVersion = masterKeyVersion;

        CanonicalEnvelopeEpoch = canonicalEnvelopeEpoch;

        RecoveryEnvelopeEpoch = recoveryEnvelopeEpoch;

        HostToolsState = hostToolsState;

        TransitionId = transitionId;

        Capability = capability ?? throw new ArgumentNullException(nameof(capability));

    }

    public string InstallationIdentity { get; }

    public long AuthorityEpoch { get; }

    public uint MasterKeyVersion { get; }

    public long CanonicalEnvelopeEpoch { get; }

    public long RecoveryEnvelopeEpoch { get; }

    public CovenantHostToolsState HostToolsState { get; }

    public string? TransitionId { get; }

    public CovenantCommittedCapabilityTransition Capability { get; }

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
