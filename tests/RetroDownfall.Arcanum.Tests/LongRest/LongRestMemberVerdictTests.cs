using System.Collections.Immutable;
using System.Text;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The shared way tests answer an adjudication request: the member verdicts of version 2 of the request. Every test that fakes a model uses
/// these three answers, so none of them knows the wire shape of the answer.
/// </summary>
internal static class LongRestScriptedVerdicts
{
    /// <summary>Every memory states the same fact as every earlier one: the unit is one fact.</summary>
    internal static string AllPairs(int count) => Answer(count, static (_, _) => true);

    /// <summary>No memory states the same fact as another.</summary>
    internal static string NoMatches(int count) => Answer(count, static (_, _) => false);

    /// <summary>True when the provider request body (the JSON the fake handler recorded) is an adjudication request and not a summary.</summary>
    internal static bool IsAdjudication(string requestBody) => requestBody.Contains("long_rest_adjudication", StringComparison.Ordinal);

    /// <summary>The memories the request's user message lists: the worked example in the system prompt is not counted.</summary>
    internal static int MemoriesIn(string requestBody)
    {
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(requestBody);

        string user = "";

        // An embedding request has no messages, and a fake that answers every request asks about them all.
        if (body.RootElement.TryGetProperty("messages", out System.Text.Json.JsonElement messages))
        {
            foreach (System.Text.Json.JsonElement message in messages.EnumerateArray())
            {
                if (message.GetProperty("role").GetString() == "user") user = message.GetProperty("content").GetString() ?? "";
            }
        }

        return user.Split('\n').Count(static line => line.StartsWith("Memory ", StringComparison.Ordinal));
    }

    /// <summary>The answer in which none of the request's memories states the same fact as another.</summary>
    internal static string NoMatches(string requestBody) => NoMatches(MemoriesIn(requestBody));

    /// <summary>The answer in which all of the request's memories state one fact.</summary>
    internal static string AllPairs(string requestBody) => AllPairs(MemoriesIn(requestBody));

    /// <summary>An answer that is not an instance of the closed shape.</summary>
    internal const string Malformed = """{"kind":"EquivalentObservations","survivorVersionId":null}""";

    /// <summary>The members <paramref name="together"/> state one fact (each earlier one), the others none; a number is one-based.</summary>
    internal static string Groups(int count, params int[][] together)
    {
        HashSet<(int, int)> pairs = [];

        foreach (int[] group in together)
        {
            foreach (int left in group)
            {
                foreach (int right in group)
                {
                    if (left < right) pairs.Add((left, right));
                }
            }
        }

        return Answer(count, (earlier, later) => pairs.Contains((earlier, later)));
    }

    private static string Answer(int count, Func<int, int, bool> same)
    {
        StringBuilder text = new("{\"memories\":[");

        for (int memory = 1; memory <= count; memory++)
        {
            if (memory > 1) text.Append(',');

            text.Append("{\"memory\":").Append(memory).Append(",\"fact\":\"fact ").Append(memory).Append("\",\"sameAs\":[")
                .Append(string.Join(',', Enumerable.Range(1, memory - 1).Where(earlier => same(earlier, memory)))).Append("]}");
        }

        return text.Append("]}").ToString();
    }
}

public sealed class LongRestMemberVerdictTests
{
    private static ImmutableArray<LongRestSourceLeaf> Unit(params string[] contents) => [.. contents.Select((content, index) => LongRestPackingData.Leaf(index, content))];

    private static ImmutableArray<LongRestSourceLeaf> Plain(int count) => Unit([.. Enumerable.Range(1, count).Select(index => "a fact worded like " + new string('x', index))]);

    private static LongRestAdjudicationDecision? Decide(string answer, ImmutableArray<LongRestSourceLeaf> inputs) => LongRestMemberVerdict.Decide(answer, inputs);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(16)]
    public void Every_pair_asserted_is_an_equivalence_with_no_survivor_named(int count)
    {
        LongRestAdjudicationDecision decision = Decide(LongRestScriptedVerdicts.AllPairs(count), Plain(count))!;

        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, decision.Kind);

        Assert.Null(decision.SurvivorVersionId);
    }

    [Fact]
    public void One_missing_pair_in_a_unit_is_no_change_however_many_others_are_asserted()
    {
        // Four memories; 1, 2 and 3 are one fact, 4 is asserted the same as 3 only: the pair (1, 4) and (2, 4) are missing.
        string answer = LongRestScriptedVerdicts.Groups(4, [1, 2, 3], [3, 4]);

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(answer, Plain(4))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(LongRestScriptedVerdicts.Groups(3, [1, 2]), Plain(3))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(LongRestScriptedVerdicts.NoMatches(5), Plain(5))!.Kind);
    }

    [Fact]
    public void An_empty_list_of_memories_is_no_change_and_not_invalid()
    {
        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide("""{"memories":[]}""", Plain(3))!.Kind);
    }

    [Fact]
    public void A_pair_is_asserted_by_either_memory_in_either_direction()
    {
        // 1 says nothing; 2 and 3 each point back, 3 at 1 and 2 at 1; and 2 is also pointed at by 1 (a later memory named by an earlier one).
        string answer = """{"memories":[{"memory":1,"fact":"f","sameAs":[2]},{"memory":2,"fact":"f","sameAs":[]},{"memory":3,"fact":"f","sameAs":[1,2]}]}""";

        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(answer, Plain(3))!.Kind);
    }

    [Fact]
    public void Out_of_range_self_referencing_and_repeated_entries_contribute_nothing()
    {
        ImmutableArray<LongRestSourceLeaf> three = Plain(3);

        // Memories 4 and 0 do not exist, 2 refers to itself: only (1, 2) is asserted, so (1, 3) and (2, 3) are missing.
        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(
            """{"memories":[{"memory":1,"fact":"f","sameAs":[]},{"memory":2,"fact":"f","sameAs":[1,2,9,0,-1]},{"memory":3,"fact":"f","sameAs":[]},{"memory":4,"fact":"f","sameAs":[1,2,3]},{"memory":0,"fact":"f","sameAs":[1,2,3]}]}""", three)!.Kind);

        // A memory listed twice contributes nothing at all, even where each entry would assert a pair on its own.
        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(
            """{"memories":[{"memory":1,"fact":"f","sameAs":[]},{"memory":2,"fact":"f","sameAs":[1]},{"memory":3,"fact":"f","sameAs":[1,2]},{"memory":3,"fact":"g","sameAs":[]}]}""", three)!.Kind);

        // The same entries without the repeat are the whole unit.
        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(
            """{"memories":[{"memory":1,"fact":"f","sameAs":[]},{"memory":2,"fact":"f","sameAs":[1]},{"memory":3,"fact":"f","sameAs":[1,2]}]}""", three)!.Kind);

        // Repeated references in one list count once and break nothing.
        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(
            """{"memories":[{"memory":1,"fact":"f","sameAs":[]},{"memory":2,"fact":"f","sameAs":[1,1,1]},{"memory":3,"fact":"f","sameAs":[2,1,2]}]}""", three)!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"memories\"")]
    [InlineData("{}")]
    [InlineData("""{"memories":null}""")]
    [InlineData("""{"memories":{}}""")]
    [InlineData("""{"memories":[],"extra":1}""")]
    [InlineData("""{"memories":[],"memories":[]}""")]
    [InlineData("""{"kind":"EquivalentObservations","survivorVersionId":null}""")]
    [InlineData("""{"memories":[1]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f"}]}""")]
    [InlineData("""{"memories":[{"memory":1,"sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"fact":"f","sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":[],"note":"x"}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","fact":"g","sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":"1","fact":"f","sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":1.5,"fact":"f","sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":7,"sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":null,"sameAs":[]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":null}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":"1"}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":["1"]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":[1.5]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":[2147483648]}]}""")]
    [InlineData("""{"memories":[{"memory":1,"fact":"f","sameAs":[]}""")]
    public void An_answer_that_is_not_an_instance_of_the_closed_shape_is_invalid(string answer) => Assert.Null(Decide(answer, Plain(3)));

    [Fact]
    public void A_missing_unit_is_invalid_and_an_answer_to_a_single_memory_never_folds()
    {
        Assert.Null(Decide(LongRestScriptedVerdicts.AllPairs(2), default));

        Assert.Null(Decide(null!, Plain(2)));

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(LongRestScriptedVerdicts.AllPairs(1), Plain(1))!.Kind);
    }

    [Fact]
    public void The_model_path_can_never_produce_a_supersession_or_a_survivor()
    {
        Random random = new(15102026);

        for (int round = 0; round < 500; round++)
        {
            int count = random.Next(2, 9);

            StringBuilder answer = new("{\"memories\":[");

            for (int memory = 1; memory <= count; memory++)
            {
                if (memory > 1) answer.Append(',');

                // References anywhere, in or out of range, including a survivor-looking string field.
                answer.Append("{\"memory\":").Append(random.Next(0, count + 2)).Append(",\"fact\":\"Supersession survivorVersionId v-").Append(memory).Append("\",\"sameAs\":[")
                    .Append(string.Join(',', Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(-1, count + 3)))).Append("]}");
            }

            LongRestAdjudicationDecision? decision = Decide(answer.Append("]}").ToString(), Plain(count));

            Assert.NotNull(decision);

            Assert.NotEqual(LongRestAdjudicationKind.Supersession, decision.Kind);

            Assert.Null(decision.SurvivorVersionId);
        }
    }

    [Fact]
    public void Number_Agreement_turns_an_all_pairs_answer_into_no_change_when_the_numbers_differ()
    {
        string all = LongRestScriptedVerdicts.AllPairs(2);

        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(all, Unit("The standup is at 9:30.", "A standup happens at 9:30 daily."))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(all, Unit("The standup is at 9:30.", "The standup is at 10:00."))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(all, Unit("The user wears size 43 shoes.", "The user wears size 44 shoes."))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(all, Unit("A trip in March 2027.", "In March 2027 a trip."))!.Kind);

        Assert.Equal(LongRestAdjudicationKind.EquivalentObservations, Decide(all, Unit("The user writes .NET 10.", "The stack is .NET 10."))!.Kind);

        // One memory of three that disagrees refuses the whole unit.
        Assert.Equal(LongRestAdjudicationKind.NoChange, Decide(LongRestScriptedVerdicts.AllPairs(3), Unit("port 80", "the port is 80", "the port is 81"))!.Kind);
    }

    [Fact]
    public void The_decision_is_the_same_whatever_order_the_inputs_arrive_in()
    {
        ImmutableArray<LongRestSourceLeaf> inputs = Unit("port 80 open", "the open port is 80", "an open port, 80", "port 80 is open");

        string answer = LongRestScriptedVerdicts.AllPairs(4);

        LongRestAdjudicationDecision expected = Decide(answer, inputs)!;

        Random random = new(3);

        for (int round = 0; round < 24; round++)
        {
            ImmutableArray<LongRestSourceLeaf> permuted = [.. inputs.OrderBy(_ => random.Next())];

            Assert.Equal(expected, Decide(answer, permuted));

            // The request lists the memories in ordinal MemoryId order whatever the order they were found in, so ordinal n is the same memory.
            Assert.Equal(inputs.Select(leaf => leaf.Target.MemoryId).Order(StringComparer.Ordinal), LongRestPromptLayout.CanonicalAdjudicationOrder(permuted).Select(leaf => leaf.Target.MemoryId));
        }
    }

    [Fact]
    public void Restated_facts_are_read_and_dropped_and_appear_in_nothing_the_reader_returns()
    {
        const string secret = "SECRET-RESTATED-FACT-3f9a";

        string answer = LongRestScriptedVerdicts.AllPairs(3).Replace("fact 1", secret, StringComparison.Ordinal).Replace("fact 2", secret + "2", StringComparison.Ordinal);

        LongRestAdjudicationDecision decision = Decide(answer, Plain(3))!;

        Assert.DoesNotContain("SECRET", decision.ToString(), StringComparison.Ordinal);

        Assert.Null(decision.SurvivorVersionId);
    }

    [Fact]
    public void The_scripted_verdict_helper_writes_the_shape_the_schema_describes()
    {
        Assert.Equal("""{"memories":[{"memory":1,"fact":"fact 1","sameAs":[]},{"memory":2,"fact":"fact 2","sameAs":[1]},{"memory":3,"fact":"fact 3","sameAs":[1,2]}]}""",
            LongRestScriptedVerdicts.AllPairs(3));

        Assert.Equal("""{"memories":[{"memory":1,"fact":"fact 1","sameAs":[]},{"memory":2,"fact":"fact 2","sameAs":[]}]}""", LongRestScriptedVerdicts.NoMatches(2));

        Assert.Null(Decide(LongRestScriptedVerdicts.Malformed, Plain(2)));

        using System.Text.Json.JsonDocument schema = System.Text.Json.JsonDocument.Parse(LongRestPromptLayout.DecisionSchema);

        Assert.Equal(["memories"], schema.RootElement.GetProperty("required").EnumerateArray().Select(static item => item.GetString()!));

        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }
}
