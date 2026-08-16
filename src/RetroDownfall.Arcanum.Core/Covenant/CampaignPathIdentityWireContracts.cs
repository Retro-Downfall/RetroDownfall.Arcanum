using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What Arcanum currently knows about one Campaign's physical root.
/// </summary>
/// <remarks>
/// Six states rather than a boolean, because the remedies differ. A Campaign that predates physical
/// identity needs registering; one whose directory moved needs repairing; one whose marker belongs to
/// a Campaign that no longer exists needs taking over. Collapsing them would leave the operator with
/// one message and five possible next steps (§10.12).
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathIdentityState>))]
public enum CampaignPathIdentityState : byte
{

    Active = 1,

    LegacyUnresolved = 2,

    Missing = 3,

    Invalid = 4,

    OrphanCleanupPending = 5,

    OperationPending = 6,

}

/// <summary>
/// The exact next operation for one unresolved Campaign root.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathRemediation>))]
public enum CampaignPathRemediation : byte
{

    None = 1,

    Register = 2,

    Update = 3,

    RepairMoved = 4,

    Deregister = 5,

    TakeoverOrphan = 6,

    WaitForOperation = 7,

}

/// <summary>
/// One Campaign's path-identity status, with the verb that resolves it.
/// </summary>
/// <remarks>
/// The display path is the server's normalized form of a root it opened itself. The CLI never probes
/// the filesystem for this: a bulk legacy-upgrade report generated from the client's own view of the
/// disk would describe a different machine's directories whenever the host is not the caller.
/// </remarks>
public sealed record CampaignPathIdentityStatusItemDto(
    Guid CampaignId,
    CampaignPathIdentityState State,
    long PathRevision,
    CampaignPathRemediation Remediation,
    string? NormalizedDisplayPath,
    string? RemediationCode);

/// <summary>
/// A keyset-paginated page of path-identity statuses.
/// </summary>
public sealed record CampaignPathIdentityStatusPageDto(
    CampaignPathIdentityStatusItemDto[] Items,
    string? NextCursor,
    bool Truncated);

/// <summary>
/// The server-opened facts about one prospective path operation, and the token that binds them.
/// </summary>
/// <remarks>
/// Every identity here was computed by opening the target under the same no-follow policy the apply
/// half will use, and apply reopens and revalidates all of it before committing the durable marker
/// intent. A plan computed from a path string would describe whatever the string meant at the moment
/// it was typed.
/// </remarks>
public sealed record CampaignPathIdentityPlanDto(
    Guid OperationId,
    Guid CampaignId,
    CampaignPathIdentityOperation Operation,
    string? NormalizedDisplayPath,
    string? CurrentIdentityDigest,
    string? ProspectiveIdentityDigest,
    string? ExistingMarkerIdentity,
    bool MarkerConflict,
    long ActiveTurnBlockerCount,
    bool OldMarkerCleanupRequired,
    long CurrentPathRevision,
    long CampaignRegistryEpoch,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Token);

/// <summary>
/// The durable outcome of one committed path operation.
/// </summary>
public sealed record CampaignPathIdentityResultDto(
    Guid OperationId,
    Guid CampaignId,
    CampaignPathIdentityOperation Operation,
    CampaignPathIdentityState State,
    long PathRevision,
    long AvailabilityGeneration,
    long DrainedTurnCount,
    bool Replayed);

/// <summary>
/// A typed all-Campaign or explicit-identity status selector.
/// </summary>
public sealed record CampaignPathIdentityStatusRequest(
    bool AllCampaigns,
    Guid? CampaignId,
    int Limit,
    string? Cursor)
{

    [JsonIgnore]
    public int EffectiveLimit =>
        Limit <= 0 ? CovenantLimits.DefaultPageSize : CovenantManagementReadLimits.ClampPageSize(Limit);

    public Result Validate() =>
        AllCampaigns
            ? CovenantWireValidation.First(
                CampaignId is null
                    ? Result.Success()
                    : CovenantWireValidation.InvalidScope("An all-Campaign status request cannot also name one Campaign."),
                CovenantWireValidation.ValidateCursor(Cursor))
            : CovenantWireValidation.First(
                CovenantWireValidation.RequireIdentity(CampaignId ?? Guid.Empty, "Campaign identity"),
                CovenantWireValidation.ValidateCursor(Cursor));

}

/// <summary>
/// A prepared path operation.
/// </summary>
/// <remarks>
/// The path is required for every operation that opens one and refused for the one that does not.
/// A deregistration that carried a path would look like it was deregistering that path, when what it
/// actually does is release whatever root the Campaign currently holds.
/// </remarks>
public sealed record CampaignPathPrepareRequest(
    Guid OperationId,
    CampaignPathIdentityOperation Operation,
    string? Path)
{

    public Result Validate()
    {

        Result identity = CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity");

        if (identity.IsFailure)
        {

            return identity;

        }

        return Operation switch
        {
            CampaignPathIdentityOperation.Register
                or CampaignPathIdentityOperation.Update
                or CampaignPathIdentityOperation.RepairMoved
                or CampaignPathIdentityOperation.TakeoverOrphan =>
                string.IsNullOrWhiteSpace(Path)
                    ? new Error(ErrorCodes.Campaign.InvalidPath, "This Campaign path operation requires a target path.")
                    : Result.Success(),

            CampaignPathIdentityOperation.Deregister =>
                Path is null
                    ? Result.Success()
                    : new Error(
                        ErrorCodes.Campaign.InvalidPath,
                        "Deregistration releases the Campaign's current root and takes no path."),

            _ => new Error(ErrorCodes.Campaign.InvalidPath, "The requested Campaign path operation is not a member of the contract."),
        };

    }

}

/// <summary>
/// The apply half of a path operation.
/// </summary>
public sealed record CampaignPathApplyRequest(
    Guid OperationId,
    string ApplyRequestDigest,
    string Token)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity"),
            CovenantWireValidation.ValidateApplyRequestDigest(ApplyRequestDigest),
            CovenantWireValidation.ValidateToken(Token));

}
