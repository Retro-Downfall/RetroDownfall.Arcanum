using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildBudgetOwnerKind
{
    Quest = 0,

    Cohort = 1,
}

internal enum GuildBudgetEffectKind
{
    LocalProvider = 0,

    External = 1,
}

internal enum GuildBudgetEffectState
{
    Prepared = 0,

    PossiblyDispatched = 1,

    Complete = 2,

    ProvenNoDispatch = 3,
}

/// <summary>Host-bound physical effect carrying qualified price/provider bounds; runtime adapters own their qualification and reservation identity.</summary>
internal sealed record GuildBudgetEffectRequest(Guid EffectId, Guid QuestId, Guid DispatchId,
    Guid AccountingAssociationId, GuildBudgetEffectKind Kind, Guid SourceId, Guid? ReservationId,
    decimal? UsdBound, long? TokenBound);

/// <summary>Cumulative evidence for exactly one physical effect, copied from its authoritative accounting source.</summary>
internal sealed record GuildBudgetEvidence(Guid ReceiptId, string Digest, decimal KnownUsd, long KnownTokens,
    bool UnknownUsd, bool UnknownTokens, bool Complete, bool NoDispatchProven = false);

internal sealed record GuildBudgetEffect(GuildBudgetEffectRequest Request, decimal KnownUsd, long KnownTokens,
    bool UnknownUsd, bool UnknownTokens, GuildBudgetEffectState State, long Revision);

internal sealed record GuildBudgetEffectAdmission(GuildBudgetEffect Effect, bool MayDispatch);

internal sealed record GuildBudgetSnapshot(Guid AccountId, GuildBudgetGrant Grant, GuildBudgetGrant Remaining,
    GuildBudgetGrant OutstandingAllocation, GuildBudgetGrant UnresolvedHolds, decimal KnownUsd, long KnownTokens,
    bool UnknownUsd, bool UnknownTokens, long PricedCount, long UnpricedCount, long UncertainCount,
    long Revision, bool Closed, GuildBudgetGrant? Committed = null, bool CapabilityFault = false);
