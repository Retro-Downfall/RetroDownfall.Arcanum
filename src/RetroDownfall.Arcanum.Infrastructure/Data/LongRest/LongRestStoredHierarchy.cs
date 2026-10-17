using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed record LongRestStoredWork(Guid GenerationId, Guid OperationId, string ItemId, LongRestWorkKind Kind,
    LongRestSweepScopeKind ScopeKind, Guid? CampaignId, CovenantDigest? PartitionDigest, LongRestStoredFence Fence,
    CovenantDigest SourceDigest, CovenantDigest RecipeDigest, int PairingFloorPermille = 0)
{
    internal static LongRestStoredWork From(LongRestWorkIdentity work) => new(work.GenerationId, work.OperationId, work.ItemId, work.Kind,
        work.Scope.Kind, work.Scope.CampaignId, work.Scope.ProtectedPartitionDigest, LongRestStoredFence.From(work.Fence), work.SourceDigest, work.RecipeDigest,
        work.PairingFloorPermille);

    internal LongRestWorkIdentity ToDomain()
    {
        if (Fence is null)
        {
            throw new InvalidDataException();
        }

        var parsed = LongRestSweepScope.Create(new(ScopeKind, CampaignId), PartitionDigest);

        if (parsed.IsFailure)
        {
            throw new InvalidDataException();
        }

        return new(GenerationId, OperationId, ItemId, Kind, parsed.Value, Fence.ToDomain(parsed.Value), SourceDigest, RecipeDigest, PairingFloorPermille);
    }
}

internal sealed record LongRestStoredPaid(Guid AttemptId, LongRestStoredWork Work, LongRestProviderPayloadKind PayloadKind,
    ulong PhysicalOrdinal, long Revision, LongRestDispatchAttemptState State, CovenantDigest PayloadDigest,
    CovenantDigest DestinationDigest, CovenantDigest? DisclosureReceiptDigest, long InputTokens, long OutputTokens, decimal? CostUsd)
{
    internal static LongRestStoredPaid From(LongRestPaidEvidence paid) => new(paid.Attempt.AttemptId, LongRestStoredWork.From(paid.Attempt.Work),
        paid.Attempt.PayloadKind, paid.Attempt.PhysicalOrdinal, paid.Attempt.Revision, paid.Attempt.State, paid.Attempt.PayloadDigest,
        paid.Attempt.DestinationDigest, paid.Attempt.DisclosureReceiptDigest, paid.InputTokens, paid.OutputTokens, paid.CostUsd);

    internal LongRestPaidEvidence ToDomain() => new(new(AttemptId, (Work ?? throw new InvalidDataException()).ToDomain(), PayloadKind, PhysicalOrdinal, Revision, State,
        PayloadDigest, DestinationDigest, DisclosureReceiptDigest), InputTokens, OutputTokens, CostUsd);
}

internal sealed record LongRestStoredSummary(Guid GenerationId, Guid NodeId, LongRestStoredWork Work, int Layer, long Group,
    CovenantDigest MembershipDigest, ImmutableArray<LongRestHierarchyChild> Children, CovenantDigest RecipeDigest,
    CovenantDigest PayloadDigest, string Summary, CovenantDigest SummaryDigest, ContentSensitivity Sensitivity,
    GenerationProvenance Provenance, ImmutableArray<CovenantDigest> LabelDigests, LongRestStoredPaid PaidEvidence)
{
    internal static LongRestStoredSummary From(LongRestAcceptedHierarchySummary summary) => new(summary.GenerationId, summary.NodeId,
        LongRestStoredWork.From(summary.Fold.Work), summary.Fold.Layer, summary.Fold.Group, summary.Fold.MembershipDigest, summary.Fold.Children,
        summary.RecipeDigest, summary.PayloadDigest, summary.Summary, summary.SummaryDigest, summary.Sensitivity,
        summary.Provenance, summary.LabelDigests, LongRestStoredPaid.From(summary.PaidEvidence));

    internal LongRestAcceptedHierarchySummary ToDomain() => new(GenerationId, NodeId, new((Work ?? throw new InvalidDataException()).ToDomain(), Layer, Group, MembershipDigest, Children),
        RecipeDigest, PayloadDigest, Summary, SummaryDigest, Sensitivity, Provenance, LabelDigests, (PaidEvidence ?? throw new InvalidDataException()).ToDomain());
}
