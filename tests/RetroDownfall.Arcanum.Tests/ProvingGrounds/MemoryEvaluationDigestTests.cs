using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Covenant;
using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationDigestTests
{
    [Fact]
    public void Request_has_literal_independently_framed_golden_vector()
    {
        Assert.Equal("E1FB3B3081F0681DAC1E9818F53B634A5C9DD5CB3136390623E89538BDACDDAF", MemoryEvaluationDigests.Request(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest()).Value).ToString());
    }

    [Fact]
    public void Target_order_is_a_set_but_check_order_is_meaningful()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest();

        var first = request.Memory.Covenant[0];

        var second = first with { EntryId = Guid.Parse("44444444-4444-4444-4444-444444444444") };

        request = request with { Memory = request.Memory with { Covenant = [first, second] }, Checks = [new MemoryEvaluationRegexCheck("a"), new MemoryEvaluationRegexCheck("b")] };

        var baseline = Digest(request);

        Assert.Equal(baseline, Digest(request with { Memory = request.Memory with { Covenant = [second, first] } }));

        Assert.NotEqual(baseline, Digest(request with { Checks = [request.Checks[1], request.Checks[0]] }));
    }

    [Fact]
    public void Every_decision_relevant_request_change_is_bound()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest();

        var baseline = Digest(request);

        MemoryEvaluationRequest[] changed = [request with { Model = "other" }, request with { ProviderName = "other" }, request with { Workload = new("other", "user") }, request with { Options = new(Seed: 1) }, request with { PairCount = 3 }, request with { Scope = new(MemoryEvaluationScopeKind.Campaign, Guid.NewGuid()) }, request with { Memory = request.Memory with { Covenant = [request.Memory.Covenant[0] with { VersionId = Guid.NewGuid() }] } }];

        Assert.All(changed, candidate => Assert.NotEqual(baseline, Digest(candidate)));
    }

    [Fact]
    public void Source_snapshot_copies_borrowed_manifest_array()
    {
        MemoryEvaluationSourceCoordinate[] borrowed = [Coordinate()];

        MemoryEvaluationSourceSnapshot snapshot = new(ImmutableCollectionsMarshal.AsImmutableArray(borrowed), "memory", Zero());

        borrowed[0] = borrowed[0] with { SubjectId = "changed" };

        Assert.Equal("subject", snapshot.Coordinates[0].SubjectId);
    }

    [Fact]
    public void Execution_plan_binds_fence_generation_even_after_identical_source_bytes_return()
    {
        MemoryEvaluationExecutionPlan plan = Plan();

        Assert.NotEqual(MemoryEvaluationDigests.ExecutionPlan(plan), MemoryEvaluationDigests.ExecutionPlan(plan with { Sources = plan.Sources with { Fence = plan.Sources.Fence! with { RuntimeAuthorityGeneration = 2 } } }));
    }

    [Fact]
    public void Plan_and_report_have_separate_literal_golden_vectors()
    {
        Assert.Equal("66B3A9338B7B5E3581FF5A07696262EC1E8762FD87C286B8439B874B60B612EB", MemoryEvaluationDigests.ExecutionPlan(Plan()).ToString());

        Assert.Equal("6139F1769935A30151AC3BE186B998045F8ED98EDABD3C949075BB0FFEDD8002", MemoryEvaluationDigests.Report(Report()).ToString());

        Assert.Equal("0AE38541C0F00B90AA7A382D27AEC42FE38565DA0D71563B0FF1DEF7A6BE74EB", MemoryEvaluationDigests.Report(Report() with { Registration = Report().Registration with { SourceAsOfUtc = DateTimeOffset.UnixEpoch } }).ToString());
    }

    [Fact]
    public void Source_cut_is_bound_to_terminal_report_identity()
    {
        var report = Report();
        Assert.NotEqual(MemoryEvaluationDigests.Report(report), MemoryEvaluationDigests.Report(report with { Registration = report.Registration with { SourceAsOfUtc = DateTimeOffset.UnixEpoch } }));
    }

    [Fact]
    public void Durable_aggregates_own_their_metadata_arrays()
    {
        MemoryEvaluationCheckAggregate[] borrowed = [new(0, MemoryEvaluationCheckKind.Regex, Zero(), 0, 3, 0, 0)];

        var aggregate = MemoryEvaluationTestData.Aggregate(MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved) with { Checks = ImmutableCollectionsMarshal.AsImmutableArray(borrowed) };

        borrowed[0] = borrowed[0] with { PassFail = 3 };

        Assert.Equal(0, aggregate.Checks[0].PassFail);
    }

    [Fact]
    public void Source_owner_authorship_and_each_store_origin_remain_distinct_bindings()
    {
        var coordinate = Coordinate() with { Store = MemoryEvaluationSource.Covenant };

        var plan = Plan() with { Sources = Plan().Sources with { Coordinates = [coordinate] } };

        var baseline = MemoryEvaluationDigests.ExecutionPlan(plan);

        Assert.NotEqual(baseline, MemoryEvaluationDigests.ExecutionPlan(plan with { Sources = plan.Sources with { Coordinates = [coordinate with { CanonicalOwnerSessionId = "owner" }] } }));

        Assert.NotEqual(baseline, MemoryEvaluationDigests.ExecutionPlan(plan with { Sources = plan.Sources with { Coordinates = [coordinate with { SourceSessionId = "author" }] } }));

        Assert.NotEqual(baseline, MemoryEvaluationDigests.ExecutionPlan(plan with { Sources = plan.Sources with { Coordinates = [coordinate with { CovenantOrigin = CovenantOrigin.Operator }] } }));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Coordinate_reordering_without_remapping_changes_source_attribution(bool fragment, bool occurrence)
    {
        var plan = AttributedPlan(fragment, occurrence);

        var reordered = plan with { Sources = plan.Sources with { Coordinates = [plan.Sources.Coordinates[1], plan.Sources.Coordinates[0]] } };

        Assert.NotEqual(MemoryEvaluationDigests.ExecutionPlan(plan), MemoryEvaluationDigests.ExecutionPlan(reordered));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Coordinate_reordering_with_remapped_references_preserves_source_attribution(bool fragment, bool occurrence)
    {
        var plan = AttributedPlan(fragment, occurrence);

        var reordered = plan with
        {
            Sources = plan.Sources with
            {
                Coordinates = [plan.Sources.Coordinates[1], plan.Sources.Coordinates[0]],

                Fragments = fragment ? [plan.Sources.Fragments[0] with { SourceIndex = 1 }] : []
            },

            Prompts = plan.Prompts with { Occurrences = occurrence ? [plan.Prompts.Occurrences[0] with { SourceIndex = 1 }] : [] }
        };

        Assert.Equal(MemoryEvaluationDigests.ExecutionPlan(plan), MemoryEvaluationDigests.ExecutionPlan(reordered));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public void Out_of_range_source_references_cannot_receive_a_plan_identity(int sourceIndex, bool fragment)
    {
        var plan = AttributedPlan(fragment, !fragment);

        var invalid = plan with
        {
            Sources = plan.Sources with { Fragments = fragment ? [plan.Sources.Fragments[0] with { SourceIndex = sourceIndex }] : [] },

            Prompts = plan.Prompts with { Occurrences = fragment ? [] : [plan.Prompts.Occurrences[0] with { SourceIndex = sourceIndex }] }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryEvaluationDigests.ExecutionPlan(invalid));
    }

    private static MemoryEvaluationExecutionPlan AttributedPlan(bool fragment, bool occurrence)
    {
        var plan = Plan();

        return plan with
        {
            Sources = plan.Sources with
            {
                Coordinates = [Coordinate() with { SubjectId = "A" }, Coordinate() with { SubjectId = "B" }],

                Fragments = fragment ? [new(0, "memory", MemoryEvaluationPromptPart.System)] : []
            },

            Prompts = plan.Prompts with { Occurrences = occurrence ? [new(0, MemoryEvaluationPromptPart.System, 7, 6, Zero())] : [] }
        };
    }

    private static CovenantDigest Zero() => new(new byte[32]);

    private static MemoryEvaluationSourceCoordinate Coordinate() => new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Saga, "subject", null, "version", 1, Zero(), null, null, MemoryEvaluationScopeKind.Global, null, null, null, null, null, MemoryEvaluationSourceLifecycle.Active, ContentSensitivity.CovenantDerived, null, null);

    private static MemoryEvaluationExecutionPlan Plan() => new(
        MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest()).Value,
        new([], "memory", Zero(), [], new(Guid.Parse("55555555-5555-5555-5555-555555555555"), 1, 1, 1, Zero())),
        new("system", "user", "system memory", "user", [], Zero()),
        Zero(), Zero(), Zero(), Zero(), Zero(), 1,
        [MemoryEvaluationArm.Control, MemoryEvaluationArm.Treatment]);

    private static MemoryEvaluationTerminalReport Report() => new(
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("66666666-6666-6666-6666-666666666666"), DateTimeOffset.UnixEpoch,
            new(Guid.Parse("55555555-5555-5555-5555-555555555555"), MemoryEvaluationScopeKind.Global, null),
            Zero(), Zero(), Zero(), Zero(), Zero(), Zero(), Zero(), Zero(), Zero(), 3, 1),
        DateTimeOffset.UnixEpoch.AddSeconds(1), MemoryEvaluationState.Completed,
        new(MemoryEvaluationOutcome.Benefit, MemoryEvaluationReason.None),
        MemoryEvaluationTestData.Aggregate(MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved),
        [], null, null, null, 1000, []);

    private static string Digest(MemoryEvaluationRequest request) => MemoryEvaluationDigests.Request(MemoryEvaluationValidator.Validate(request).Value).ToString();
}
