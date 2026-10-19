using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationPromptTests
{
    [Fact]
    public void Empty_source_manifest_has_a_domain_separated_literal_identity()
    {
        Assert.Equal("C1B7C4E24759255774424E3098C7275AF41EF89692F7D1800E868FEA6411A1E4",
            Convert.ToHexString(MemoryEvaluationDigests.SourceManifest([], []).Bytes));
    }

    [Fact]
    public void Removing_declared_memory_ranges_reproduces_control_exactly()
    {
        var (request, snapshot) = Setup();

        var layout = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptComposer.Compose(request, snapshot));

        var pair = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout));

        Assert.Equal("system é", pair.ControlSystemPrompt);

        Assert.Equal("user 🧙", pair.ControlUserPrompt);

        Assert.Equal("system é\n\nselected λ", pair.TreatmentSystemPrompt);

        var occurrence = Assert.Single(pair.Occurrences);

        Assert.Equal(9, occurrence.StartUtf8);

        Assert.Equal(13, occurrence.LengthUtf8);

        Assert.Equal(snapshot.Coordinates[0].ContentDigest, occurrence.ContentDigest);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("missing")]
    [InlineData("range")]
    [InlineData("identity")]
    [InlineData("extra")]
    public void Proof_rejects_changed_base_bytes_unattributed_memory_and_wrong_ranges(string mutation)
    {
        var (request, snapshot) = Setup();

        var layout = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptComposer.Compose(request, snapshot));

        layout = mutation switch
        {
            "base" => layout with { UserPrompt = "changed" },
            "missing" => layout with { Occurrences = [] },
            "range" => layout with { Occurrences = [layout.Occurrences[0] with { StartUtf8 = 8 }] },
            "identity" => layout with { Occurrences = [layout.Occurrences[0] with { ContentDigest = new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()) }] },
            _ => layout with { SystemPrompt = layout.SystemPrompt + "UNSELECTED_SENTINEL" }
        };

        Assert.True(MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout).IsFailure);
    }

    [Fact]
    public void Prompt_part_is_independent_of_covenant_source_lane()
    {
        var (request, snapshot) = Setup();

        snapshot = snapshot with { Fragments = [snapshot.Fragments[0] with { Placement = MemoryEvaluationPromptPart.User }] };

        var layout = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptComposer.Compose(request, snapshot));

        var pair = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout));

        Assert.Equal("system é", pair.TreatmentSystemPrompt);

        Assert.Equal("user 🧙\n\nselected λ", pair.TreatmentUserPrompt);
    }

    [Fact]
    public void Rendering_preserves_confirmed_context_and_adaptively_fences_proposed_and_saga_data()
    {
        var compiler = new CovenantCompiler();

        var compiled = compiler.Compile("selected", "adversarial ``` fence");

        string saga = RetroDownfall.Arcanum.Infrastructure.Intelligence.SystemPromptBuilder.RenderEvaluationSaga("adversarial ``` fence");

        Assert.StartsWith("## DATA", saga);

        Assert.Contains("````\nadversarial ``` fence\n````", saga);

        string confirmed = RetroDownfall.Arcanum.Infrastructure.Intelligence.SystemPromptBuilder.RenderEvaluationCovenant(CovenantPlacement.GlobalConfirmed, compiled.Fragment);

        Assert.StartsWith("## CONTEXT", confirmed);
    }

    [Fact]
    public void Composer_refuses_oversized_rendered_memory_without_truncation()
    {
        var (request, snapshot) = Setup();

        string oversized = new('x', MemoryEvaluationLimits.RenderedMemoryBytes);

        snapshot = snapshot with { RenderedMemory = oversized, Fragments = [new(0, oversized, MemoryEvaluationPromptPart.System)] };

        Assert.True(MemoryEvaluationPromptComposer.Compose(request, snapshot).IsFailure);
    }

    [Fact]
    public void Caller_array_changes_cannot_rewrite_prepared_prompt_bytes()
    {
        var (request, snapshot) = Setup();

        MemoryEvaluationSourceFragment[] borrowed = [new(0, "selected λ", MemoryEvaluationPromptPart.System)];

        snapshot = snapshot with { Fragments = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(borrowed) };

        var layout = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptComposer.Compose(request, snapshot));

        borrowed[0] = borrowed[0] with { Text = "replaced" };

        var proof = MemoryEvaluationSourceHarness.Require(MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout));

        Assert.Equal("system é\n\nselected λ", proof.TreatmentSystemPrompt);
    }

    private static (MemoryEvaluationValidatedRequest Request, MemoryEvaluationSourceSnapshot Snapshot) Setup()
    {
        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Workload = new("system é", "user 🧙") }));

        var target = request.Request.Memory.Covenant[0];

        MemoryEvaluationSourceCoordinate coordinate = new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Covenant,
            target.EntryId.ToString("D"), null, target.VersionId.ToString("D"), 1, target.FragmentDigest, null, null,
            MemoryEvaluationScopeKind.Global, null, null, null, null, null, MemoryEvaluationSourceLifecycle.Active,
            ContentSensitivity.CovenantDerived, null, null, Lane: CovenantLane.Confirmed, Placement: CovenantPlacement.GlobalConfirmed);

        return (request, new([coordinate], "selected λ", new CovenantDigest(new byte[32]), Fragments: [new(0, "selected λ", MemoryEvaluationPromptPart.System)]));
    }
}
