using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // RequiresExactValueProvenance used to make a formal carry its caller's actual argument whenever it
    // fed a record comparison, an exact framework scalar conversion, a pure float intrinsic or a
    // primitive-queue argument. Each such formal split every caller of a one-line helper into its own
    // evaluation environment (FileHandleIdentity.IdentitiesMatch alone reached 259 states under one root).
    // The Structural Operand Rule, the pure intrinsic table, the queue argument-boundary rule and
    // Canonical Equals Traversal classify from the operand's own syntax, so those formals ask for nothing.
    //
    // These fixtures count analyzed states, so a regression that brings the demand back fails here
    // instead of only in a twenty-minute whole-graph run. The kept-demand twins are
    // RepeatedRecordEqualityPreservesTheActualConvertedReceiver (a record whose authored Equals can reach
    // a producer site keeps the demand) and PrimitivePriorityQueueRequiresCompleteDefaultOrigins (the
    // queue receiver keeps its default-origin demand).
    private static (string Helper, Func<int, string> Site, string Owner, string Member, bool ComparesRecords) OperandDemandShape(string shape)
    {
        return shape switch
        {
            "record-equality" => (
                ClosedValueDigestHelper + " static class Helpers { public static bool Same(DigestShape a, DigestShape b) => a == b; }",
                static index => "_ = Helpers.Same(new DigestShape(new byte[" + index + "]), DigestSource.Right); ",
                "Helpers", "Same", true),
            "scalar-conversion" => (
                "static class Helpers { public static System.Index At(int i) => i; }",
                static index => "_ = Helpers.At(" + index + "); ",
                "Helpers", "At", false),
            "finite-intrinsic" => (
                "static class Helpers { public static bool Finite(float x) => float.IsFinite(x); }",
                static index => "_ = Helpers.Finite(" + index + ".5f); ",
                "Helpers", "Finite", false),
            "vector-dot-intrinsic" => (
                "static class VectorSource { public static System.Numerics.Vector<float> Zero => default; } "
                    + "static class Helpers { public static float Dot(System.Numerics.Vector<float> a, System.Numerics.Vector<float> b) => System.Numerics.Vector.Dot<float>(a, b); }",
                // One getter keeps the total state count independent of the number of call sites; the call
                // sites still differ by position, and positions are what bind an argument to its caller.
                static _ => "_ = Helpers.Dot(VectorSource.Zero, VectorSource.Zero); ",
                "Helpers", "Dot", false),
            "primitive-queue-argument" => (
                "static class Helpers { public static int Push(Guid item, double score) { var queue = new System.Collections.Generic.PriorityQueue<Guid, (double Score, ulong ReverseHigh, ulong ReverseLow)>(); queue.Enqueue(item, (score, 0UL, 0UL)); return queue.Count; } }",
                static index => "_ = Helpers.Push(Guid.Empty, " + index + "d); ",
                "Helpers", "Push", false),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static int OperandDemandStatesOf(
        CSharpCompilation compilation, HostedProducerAdmittedStateObservation[] states, string typeName, string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

        IMethodSymbol method = Assert.Single(type.GetMembers(methodName).OfType<IMethodSymbol>());

        string key = method.OriginalDefinition.GetDocumentationCommentId() ?? method.ToDisplayString();

        return states.Count(state => state.Method == key);
    }

    [Theory]
    [InlineData("record-equality")]
    [InlineData("scalar-conversion")]
    [InlineData("finite-intrinsic")]
    [InlineData("vector-dot-intrinsic")]
    [InlineData("primitive-queue-argument")]
    public void AFormalThatFeedsOnlyAFrameworkRuleStatesDoNotGrowWithTheNumberOfCallers(string shape)
    {
        var (helper, site, owner, member, comparesRecords) = OperandDemandShape(shape);

        // Every caller passes a distinct argument expression under one root, so a demand for the actual
        // argument would give each caller its own evaluation environment and its own state.
        var (fewCompilation, fewResult, few) = ClosedValueDiscover(string.Concat(Enumerable.Range(1, 2).Select(site)), helper);

        var (manyCompilation, manyResult, many) = ClosedValueDiscover(string.Concat(Enumerable.Range(1, 20).Select(site)), helper);

        // The formal is accepted, not merely uncounted: nothing about the fixture is left unclassified.
        Assert.Empty(fewResult.Diagnostics);

        Assert.Empty(manyResult.Diagnostics);

        int helperFew = OperandDemandStatesOf(fewCompilation, few, owner, member);

        int helperMany = OperandDemandStatesOf(manyCompilation, many, owner, member);

        Assert.True(helperFew > 0, "The helper is reached under the root.");

        Assert.Equal(helperFew, helperMany);

        Assert.Equal(few.Length, many.Length);

        if (comparesRecords)
        {
            Assert.Equal(
                ClosedValueStatesOf(fewCompilation, few, "DigestShape", "Equals").Length,
                ClosedValueStatesOf(manyCompilation, many, "DigestShape", "Equals").Length);
        }
    }
}
