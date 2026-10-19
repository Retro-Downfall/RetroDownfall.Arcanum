using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public static class MemoryEvaluationTestData
{
    public static MemoryEvaluationRequest ValidRequest(int pairCount = 5) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new(MemoryEvaluationScopeKind.Global),
        "provider",
        "model",
        new(),
        new("system", "user"),
        new([new(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"), new CovenantDigest(new byte[32]))], [], []),
        [new MemoryEvaluationRegexCheck("yes")],
        pairCount);

    public static MemoryEvaluationAggregate Aggregate(params MemoryEvaluationPairRelation[] relations) => new(
        relations.Length,
        relations.Length,
        0,
        relations.Count(value => value == MemoryEvaluationPairRelation.Improved),
        relations.Count(value => value == MemoryEvaluationPairRelation.Regressed),
        relations.Count(value => value == MemoryEvaluationPairRelation.Equivalent),
        relations.Count(value => value == MemoryEvaluationPairRelation.Mixed),
        [],
        new CovenantDigest(new byte[32]));
}
