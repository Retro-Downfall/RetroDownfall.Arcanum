using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// A chat request is a pure function of the set of memories it compares and of the recipe, never of which memory happened to be the anchor
// or of the node identities one generation drew at random. That is what makes an identical set recognisable as one decision.
public sealed class LongRestCanonicalRenderingTests
{
    private static readonly CovenantDigest Recipe = new(new byte[32]);

    private static readonly ProviderSettings Provider = new() { Name = "provider", Endpoint = "https://provider.test/v1", Models = ["model"] };

    private static LongRestSourceLeaf Named(int index, string memory, string content) =>
        LongRestPackingData.Leaf(index, content) with { Snapshot = LongRestPackingData.Leaf(index, content).Snapshot with { MemoryId = memory } };

    private static CovenantDigest Digest(LongRestProviderPayload payload) => LongRestProviderCallFreezer.PayloadDigest(payload, Provider, "model").Value;

    [Fact]
    public void An_adjudication_request_is_the_same_whichever_member_is_the_anchor()
    {
        LongRestSourceLeaf[] members = [.. Enumerable.Range(0, 5).Select(index => LongRestPackingData.Leaf(index, $"observation number {index}"))];

        int[][] orders = [[0, 1, 2, 3, 4], [3, 0, 4, 1, 2], [4, 3, 2, 1, 0], [2, 4, 0, 3, 1]];

        string? prompt = null;

        CovenantDigest? digest = null;

        foreach (int[] order in orders)
        {
            ImmutableArray<LongRestSourceLeaf> pack = [.. order.Select(index => members[index])];

            LongRestProviderPayload payload = LongRestPackingData.AdjudicationPayload(pack, 8192).Value;

            LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(payload).Value;

            prompt ??= request.UserPrompt;

            digest ??= Digest(payload);

            Assert.Equal(prompt, request.UserPrompt);

            Assert.Equal(digest, Digest(payload));

            // The anchor is first in the pack and last in no order in particular; the request lists the memories in ordinal identity order and
            // labels each by its place. Nothing of a memory but its text is in it.
            Assert.Equal(Enumerable.Range(1, 5).Select(static place => "Memory " + place.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)),
                request.UserPrompt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static block => block.Split('\n')[0]));

            Assert.Equal(Enumerable.Range(0, 5).Select(static index => $"observation number {index}"),
                request.UserPrompt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static block => block.Split('\n')[1]));

            Assert.DoesNotContain("m-0000", request.UserPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain("v-0000", request.UserPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain("ContentHash", request.UserPrompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Memories_are_listed_by_ordinal_identity_and_not_by_culture()
    {
        LongRestSourceLeaf upper = Named(0, "B-1", "first");

        LongRestSourceLeaf lower = Named(1, "a-1", "second");

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(LongRestPackingData.AdjudicationPayload([lower, upper], 8192).Value).Value;

        // Ordinally 'B' (0x42) sorts before 'a' (0x61); a culture comparison would put 'a' first. The first place is B-1's text.
        Assert.Equal("Memory 01\nfirst\n\nMemory 02\nsecond\n\n", request.UserPrompt);
    }

    [Fact]
    public void A_summary_request_labels_its_children_by_place_and_is_the_same_whatever_node_identities_a_generation_drew()
    {
        ImmutableArray<LongRestHierarchyChild> first = [.. Enumerable.Range(1, 4).Select(index => LongRestPackingData.Child(index, $"child summary {index}"))];

        ImmutableArray<LongRestHierarchyChild> redrawn = [.. first.Select(static child => child with { NodeId = Guid.NewGuid() })];

        LongRestProviderPayload one = LongRestPackingData.SummaryPayload(first, Recipe, 8192).Value;

        LongRestProviderPayload other = LongRestPackingData.SummaryPayload(redrawn, Recipe, 8192).Value;

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(one).Value;

        Assert.Equal(LongRestProviderCallFreezer.RenderChat(other).Value.UserPrompt, request.UserPrompt);

        Assert.Equal(Digest(one), Digest(other));

        Assert.Equal("Observation 01\nchild summary 1\n\nObservation 02\nchild summary 2\n\nObservation 03\nchild summary 3\n\nObservation 04\nchild summary 4\n\n", request.UserPrompt);

        Assert.DoesNotContain(first[0].NodeId.ToString("D"), request.UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_summary_with_the_most_children_a_fold_may_have_renders_exactly_the_bytes_the_planner_measured()
    {
        ImmutableArray<LongRestHierarchyChild> children = [.. Enumerable.Range(1, LongRestDiscoveryLimits.HierarchyChildren).Select(index => LongRestPackingData.Child(index, $"é child {index}"))];

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(LongRestPackingData.SummaryPayload(children, Recipe, 8192).Value).Value;

        long rendered = LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription);

        Assert.Equal(LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary) + children.Sum(static child => LongRestPromptLayout.SummaryObservationBytes(child.Content)), rendered);

        Assert.EndsWith("Observation 16\né child 16\n\n", request.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_whose_lifecycle_state_differs_is_a_different_set()
    {
        LongRestSourceLeaf[] members = [LongRestPackingData.Leaf(0, "one"), LongRestPackingData.Leaf(1, "two")];

        CovenantDigest plain = LongRestDiscoveryHash.SourcePage(LongRestPackingData.Scope, [.. members]).Value;

        LongRestSourceLeaf pinned = members[1] with { Snapshot = members[1].Snapshot with { PinnedAtUtc = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero) } };

        LongRestSourceLeaf consolidated = members[1] with { Snapshot = members[1].Snapshot with { IsConsolidated = true } };

        Assert.NotEqual(plain, LongRestDiscoveryHash.SourcePage(LongRestPackingData.Scope, [members[0], pinned]).Value);

        Assert.NotEqual(plain, LongRestDiscoveryHash.SourcePage(LongRestPackingData.Scope, [members[0], consolidated]).Value);

        // The digest is of the set: neither the anchor nor the order the members are listed in enters it.
        Assert.Equal(plain, LongRestDiscoveryHash.SourcePage(LongRestPackingData.Scope, [members[1], members[0]]).Value);
    }

    [Fact]
    public void The_adjudication_recipe_version_is_bound_into_the_recipe_and_pins_the_adjudication_prompt()
    {
        LongRestRecipeIdentity recipe = new(1, 1, "provider", "model", LongRestPackingData.Digest, "provider", "embedding", LongRestPackingData.Digest, 64);

        Assert.Equal(LongRestPromptLayout.AdjudicationRecipeVersion, recipe.AdjudicationRecipeVersion);

        CovenantDigest current = LongRestDiscoveryHash.Recipe(recipe).Value;

        Assert.NotEqual(current, LongRestDiscoveryHash.Recipe(recipe with { AdjudicationRecipeVersion = recipe.AdjudicationRecipeVersion + 1 }).Value);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { AdjudicationRecipeVersion = 0 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { AdjudicationRecipeVersion = -1 }).IsFailure);

        // The version names the adjudication request's text: the prompt, the decision schema and the shape of a memory block. Changing any of
        // them without moving the version would let an old decision answer a new question, so this digest and the version move together.
        string text = string.Join('\0', LongRestPromptLayout.AdjudicationSystemPrompt, LongRestPromptLayout.DecisionSchema, LongRestPromptLayout.AdjudicationSchemaName,
            LongRestPromptLayout.AdjudicationSchemaDescription, LongRestPromptLayout.AdjudicationMemory(1, "<content>"));

        Assert.Equal(2, LongRestPromptLayout.AdjudicationRecipeVersion);

        // Taken from the source text by an independent script.
        Assert.Equal("CDB92F65583A43938B23A890E6489277D29A4346591F6163A3CE04E781107EA1", Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text))));
    }
}
