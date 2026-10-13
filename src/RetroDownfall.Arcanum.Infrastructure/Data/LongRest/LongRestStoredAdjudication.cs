using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed record LongRestStoredAdjudication(LongRestStoredWork Work, ImmutableArray<LongRestSourceLeaf> Inputs,
    LongRestAdjudicationDecision Decision, CovenantDigest PayloadDigest, LongRestStoredPaid? PaidEvidence, LongRestProtectedEvidence? ProtectedEvidence)
{
    internal static LongRestStoredAdjudication From(LongRestAdjudication value) => new(LongRestStoredWork.From(value.Work), value.Inputs,
        value.Decision, value.PayloadDigest, value.PaidEvidence is null ? null : LongRestStoredPaid.From(value.PaidEvidence), value.ProtectedEvidence);

    internal LongRestAdjudication ToDomain() => new(Work.ToDomain(), Inputs, Decision, PayloadDigest, PaidEvidence?.ToDomain(), ProtectedEvidence);
}
