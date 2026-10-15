using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The CallbackReachResolution fixtures: which authored callbacks a framework member handed a value can run, resolved through the
// members a generated record member compares, hashes and prints. The resolution keeps one step per type and slot and one answer per
// implementation, so these twins pin that each field shape still reaches (or still does not reach) what it reached before: an effect
// in a callback the value can hold is refused wherever the value holds it, and the same value with no effect there is not.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string CallbackReachEffect = "System.IO.File.Delete(\"callback-reach-effect\"); ";

    // The leaf holds the effect in the callback named; the callback is Equals, ToString, or neither ("Touch" is no callback slot).
    private static string CallbackReachLeaf(string callback, bool effect, string contracts = "") =>
        "sealed class Leaf" + contracts + " { "
        + "public override bool Equals(object? other) { " + (effect && callback == "Equals" ? CallbackReachEffect : string.Empty) + "return ReferenceEquals(this, other); } "
        + "public override int GetHashCode() { " + (effect && callback == "GetHashCode" ? CallbackReachEffect : string.Empty) + "return 1; } "
        + "public override string ToString() { " + (effect && callback == "ToString" ? CallbackReachEffect : string.Empty) + "return \"leaf\"; } "
        + "public void Touch() { " + (effect && callback == "Touch" ? CallbackReachEffect : string.Empty) + "} }";

    [Theory]
    [InlineData("object-field", "Equals", "sealed record Outer(object Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", "", true)]
    [InlineData("object-field", "GetHashCode", "sealed record Outer(object Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", "", true)]
    [InlineData("object-field", "ToString", "sealed record Outer(object Inner);", "string text = $\"{new Outer(new Leaf())}\"; _ = text;", "", true)]
    [InlineData("object-field", "Touch", "sealed record Outer(object Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", "", false)]
    [InlineData("interface-field", "Equals", "interface IPart { } sealed record Outer(IPart Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", " : IPart", true)]
    [InlineData("interface-field", "ToString", "interface IPart { } sealed record Outer(IPart Inner);", "string text = $\"{new Outer(new Leaf())}\"; _ = text;", " : IPart", true)]
    [InlineData("unrelated-interface-field", "Equals", "interface IPart { } interface IOther { } sealed record Outer(IOther Inner);", "System.GC.KeepAlive(new Outer(null!));", " : IPart", false)]
    [InlineData("nested-record", "Equals", "sealed record Outer(Middle Inner); sealed record Middle(object Hidden);", "System.GC.KeepAlive(new Outer(new Middle(new Leaf())));", "", true)]
    [InlineData("nested-record", "ToString", "sealed record Outer(Middle Inner); sealed record Middle(object Hidden);", "string text = $\"{new Outer(new Middle(new Leaf()))}\"; _ = text;", "", true)]
    [InlineData("record-cycle", "Equals", "sealed record First(Second Next); sealed record Second(First Back, object Hidden);", "System.GC.KeepAlive(new First(new Second(null!, new Leaf())));", "", true)]
    [InlineData("record-cycle", "ToString", "sealed record First(Second Next); sealed record Second(First Back, object Hidden);", "string text = $\"{new First(new Second(null!, new Leaf()))}\"; _ = text;", "", true)]
    [InlineData("record-cycle", "Touch", "sealed record First(Second Next); sealed record Second(First Back, object Hidden);", "System.GC.KeepAlive(new First(new Second(null!, new Leaf())));", "", false)]
    [InlineData("generic-field", "Equals", "sealed record Box<T>(T Value); sealed record Outer(Box<object> Inner);", "System.GC.KeepAlive(new Outer(new Box<object>(new Leaf())));", "", true)]
    [InlineData("tuple-field", "Equals", "sealed record Outer((int, object) Pair);", "System.GC.KeepAlive(new Outer((1, new Leaf())));", "", true)]
    [InlineData("array-field", "Equals", "sealed record Outer(object[] Items);", "System.GC.KeepAlive(new Outer([new Leaf()]));", "", false)]
    [InlineData("nullable-field", "Equals", "sealed record Outer(object? Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", "", true)]
    [InlineData("sealed-field", "Equals", "sealed record Outer(Leaf Inner);", "System.GC.KeepAlive(new Outer(new Leaf()));", "", true)]
    [InlineData("two-records-share-a-leaf", "Equals", "sealed record Left(object Inner); sealed record Right(object Inner);", "System.GC.KeepAlive(new Left(new Leaf())); System.GC.KeepAlive(new Right(new Leaf()));", "", true)]
    public void FrameworkMemberHandedARecordIsRefusedWhereAFieldHoldsAnAuthoredCallbackThatReachesASite(string shape, string callback, string records, string statement, string contracts, bool reached)
    {
        // A callback the value can hold is either followed to the site it reaches (formatting an exact type) or refused where a framework member
        // is handed the value (equality and hashing of a value nothing proves).
        static bool Reached(HostedProducerDiscovery<HostedProducerSite> result) =>
            EnumerationDeleteSites(result).Length != 0 || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, records + " " + CallbackReachLeaf(callback, effect: true, contracts)));

        Assert.True(reached == Reached(result), shape + "/" + callback + ": " + string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, records + " " + CallbackReachLeaf(callback, effect: false, contracts)));

        Assert.False(Reached(clean), shape + "/" + callback + " (no effect): " + string.Join(" | ", clean.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));
    }

    // The candidates of a callback slot are read from an index of the production formatters. For a sample of the receiver types the
    // traversal hands it (object, a value, a type parameter, and the formatters, interfaces and base types of the production source) and for
    // every slot the formatters implement, the index selects exactly the formatters assignable to the receiver that run the slot.
    [Fact]
    public void TheFormatterCandidateIndexSelectsExactlyWhatItsDefinitionSelects()
    {
        (int pairs, int selective, IReadOnlyList<string> mismatches) = HostedGrimoireProducerInventory.ProbeFormatterCandidateIndex(HostedGrimoireProducerInventory.ProductionCompilations, 24);

        Assert.True(mismatches.Count == 0, "Receiver and slot pairs the formatter index and its definition disagree on: " + string.Join(" | ", mismatches.Take(20)));

        // The sample is wide and selective: most pairs select some formatters and not all of them.
        Assert.True(pairs >= 200, "The sample holds " + pairs + " receiver and slot pairs.");

        Assert.True(selective * 4 >= pairs, "Only " + selective + " of " + pairs + " pairs select some of the formatters.");
    }

    // The judgement of a callback slot resolves each authored implementation once and takes each formatting step once per resolution. The
    // judgement for object resolves each production implementation at most once (the sum of the work is bounded by the implementations, not
    // by the implementations and the types that ask), and the judgement for a value, which asks about the same implementations, resolves none
    // of them again. The production source has no callback that reaches a site, so both answers are no.
    [Fact]
    public void TheCallbackJudgementResolvesEachProductionImplementationOnce()
    {
        (int implementations, int firstResolutions, int firstSteps, bool firstReaches, int secondResolutions, int secondSteps, bool secondReaches) =
            HostedGrimoireProducerInventory.ProbeCallbackReachWork(HostedGrimoireProducerInventory.ProductionCompilations);

        output.WriteLine($"CALLBACK_REACH_WORK implementations={implementations} firstResolutions={firstResolutions} firstSteps={firstSteps} secondResolutions={secondResolutions} secondSteps={secondSteps}");

        Assert.True(implementations >= 1000, "The production source holds " + implementations + " callback implementations.");

        Assert.False(firstReaches);

        Assert.False(secondReaches);

        Assert.True(firstResolutions <= implementations, firstResolutions + " resolutions for " + implementations + " implementations.");

        Assert.Equal(0, secondResolutions);

        Assert.Equal(0, secondSteps);

        // A step is a slot of a type, and the steps of one resolution are the types its fields can hold: the work is bounded by the product of
        // the implementations and a small factor, never by the product of the implementations and the formatters.
        Assert.True(firstSteps <= implementations * 64, firstSteps + " formatting steps for " + implementations + " implementations.");
    }

    // The program-wide queries a graph answers from an index or a memo (the call sites of a method, the constructors of a type, the implementers
    // of an interface method, whether a parameter is written, whether a callable mutates a local) return what the pass or computation they replace returns, in the same order, over a
    // sample of the production source.
    [Fact]
    public void TheProgramIndexesAnswerWhatThePassesTheyReplaceAnswer()
    {
        (int calls, int constructors, int implementers, int parameters, int mutations, IReadOnlyList<string> mismatches) =
            HostedGrimoireProducerInventory.ProbeProgramIndexes(HostedGrimoireProducerInventory.ProductionCompilations, 24);

        Assert.True(mismatches.Count == 0, "Queries the index or memo and the pass or computation disagree on: " + string.Join(" | ", mismatches.Take(20)));

        Assert.True(calls >= 40 && constructors >= 20 && implementers >= 10 && parameters >= 20 && mutations >= 100, $"The sample holds {calls} call-site queries, {constructors} constructor queries, {implementers} implementer queries, {parameters} parameter queries and {mutations} mutation queries.");
    }
}
