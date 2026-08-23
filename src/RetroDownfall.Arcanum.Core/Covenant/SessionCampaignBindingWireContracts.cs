using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// One Session's immutable Campaign binding, or the fact that it predates having one.
/// </summary>
/// <remarks>
/// <paramref name="Immutable"/> is the whole point of the row. Once a Session resolves to Global-only
/// or to one Campaign, that choice never changes again, because a Session whose scope could move
/// would carry a history assembled under one Covenant into a turn evaluated under another (§10.12).
/// </remarks>
public sealed record SessionCampaignBindingStatusItemDto(
    Guid SessionId,
    SessionCampaignBindingKind Kind,
    Guid? CampaignId,
    bool Immutable,
    DateTimeOffset? ResolvedAtUtc);

/// <summary>
/// A keyset-paginated page of Session binding statuses.
/// </summary>
public sealed record SessionCampaignBindingStatusPageDto(
    SessionCampaignBindingStatusItemDto[] Items,
    string? NextCursor,
    bool Truncated);

/// <summary>
/// The prepared, immutable resolution of one legacy-unresolved Session.
/// </summary>
public sealed record SessionCampaignBindingPlanDto(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind Kind,
    Guid? CampaignId,
    long EntryCount,
    string PriorBindingRowDigest,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Token);

/// <summary>
/// The durable outcome of one committed binding resolution.
/// </summary>
public sealed record SessionCampaignBindingResultDto(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind Kind,
    Guid? CampaignId,
    bool Replayed);

/// <summary>
/// A typed all-Session or explicit-identity binding-status selector.
/// </summary>
public sealed record SessionCampaignBindingStatusRequest(
    bool AllSessions,
    Guid? SessionId,
    int Limit,
    string? Cursor)
{

    [JsonIgnore]
    public int EffectiveLimit =>
        Limit <= 0 ? CovenantLimits.DefaultPageSize : CovenantManagementReadLimits.ClampPageSize(Limit);

    public Result Validate() =>
        AllSessions
            ? CovenantWireValidation.First(
                SessionId is null
                    ? Result.Success()
                    : CovenantWireValidation.InvalidScope("An all-Session status request cannot also name one Session."),
                CovenantWireValidation.ValidateCursor(Cursor))
            : CovenantWireValidation.First(
                CovenantWireValidation.RequireIdentity(SessionId ?? Guid.Empty, "Session identity"),
                CovenantWireValidation.ValidateCursor(Cursor));

}

/// <summary>
/// A prepared resolution of one legacy-unresolved Session's binding.
/// </summary>
/// <remarks>
/// <see cref="SessionCampaignBindingKind.LegacyUnresolved"/> is refused as a target. It is the state
/// being left, not a state anything can be moved into, and accepting it would let a caller "resolve"
/// a Session to exactly where it already was while consuming its one immutable choice.
/// </remarks>
public sealed record SessionCampaignBindingPrepareRequest(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind Kind,
    Guid? CampaignId)
{

    public Result Validate()
    {

        Result identities = CovenantWireValidation.First(
            CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity"),
            CovenantWireValidation.RequireIdentity(SessionId, "Session identity"));

        if (identities.IsFailure)
        {

            return identities;

        }

        return Kind switch
        {
            SessionCampaignBindingKind.GlobalOnly =>
                CampaignId is null
                    ? Result.Success()
                    : CovenantWireValidation.InvalidScope("A Global-only binding cannot name a Campaign."),

            SessionCampaignBindingKind.Campaign =>
                CampaignId is { } id && id != Guid.Empty
                    ? Result.Success()
                    : CovenantWireValidation.InvalidScope("A Campaign binding requires a nonempty Campaign identity."),

            _ => CovenantWireValidation.InvalidScope(
                "A binding resolution names the scope a Session is moving to, never the unresolved state it is leaving."),
        };

    }

}

/// <summary>
/// The apply half of a binding resolution.
/// </summary>
public sealed record SessionCampaignBindingApplyRequest(
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
