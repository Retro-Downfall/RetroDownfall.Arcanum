using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The carrier model's treatment of a handle written to storage. The origin walk follows a handle through locals, formals and
    // the values built from them. A handle written to storage the walk cannot follow (a static, an element of an array, a field
    // of another object) leaves the tracked environment at the store, and that ends the admission there: every send after the
    // store loses its Work and its Effect. The store itself is not refused. This is the model the Lease Spine pins for a lease
    // stored in a field, and the ordinary retained holder shapes rely on it, so it is not changed by Handle-Incapable Values:
    // a closed key, index or conversion that reads a stash back is as invisible as a direct read of the static always was, and
    // it no longer inherits the stashing call's origin by accident of the may-alias walk.
    private const string HeapStoreDeclarations = """
        static class Holder
        {
            internal static IDisposable? Kept;
        }

        sealed class Box
        {
            internal IDisposable? Kept;
        }

        static class Stash
        {
            internal static IDisposable[] Items = new IDisposable[4];

            internal static int Put(IDisposable g)
            {
                Items[0] = g;

                return 0;
            }

            internal static void Forward(int slot) => Sink.Box.Writer.TryWrite(Items[slot]);
        }

        static class Sink
        {
            internal static readonly System.Threading.Channels.Channel<object> Box = System.Threading.Channels.Channel.CreateBounded<object>(1);
        }
        """;

    private static string HeapStoreStatements(string shape) => shape switch
    {
        "static-field" => "Holder.Kept = held;",
        "static-array-element" => "Stash.Items[0] = held;",
        "field-of-another-object" => "Box box = new(); box.Kept = held;",
        "through-an-authored-callee" => "Stash.Put(held);",
        "key-then-channel" => "int slot = Stash.Put(held); Stash.Forward(slot);",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [InlineData("static-field")]
    [InlineData("static-array-element")]
    [InlineData("field-of-another-object")]
    [InlineData("through-an-authored-callee")]
    [InlineData("key-then-channel")]
    public void AHandleWrittenToStorageEndsTheAdmissionAtTheStoreSoTheSendAfterItLosesItsEffect(string shape)
    {
        CSharpCompilation compilation = Compile(R2Source(R2Admission + HeapStoreStatements(shape) + " System.IO.File.Delete(\"after\");", HeapStoreDeclarations));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        InvocationExpressionSyntax send = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"HEAP_STORED_HANDLE_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Identity.EndsWith("/site@" + send.SyntaxTree.FilePath + ":" + send.SpanStart, StringComparison.Ordinal));
    }

    // KNOWN LIMIT. With no send after the store, nothing needs the admission that the store ended, and the store is not refused
    // by itself, whether the stash is read back directly, through an index, or through a closed key. This pins today's model so
    // that it cannot drift silently. Closing the limit means refusing the store itself, which also changes how a lease stored
    // in a field is modeled (LeaseSpineGrantsTheWorkersWorkAndTheGroupsEffectAtTheSendOrEndsThemAtTheExactDeparture, the
    // stored-in-field row) and the ordinary retained holder shapes; when it is closed, these rows must flip to assert the
    // escape at the store.
    [Theory]
    [InlineData("static-field")]
    [InlineData("static-array-element")]
    [InlineData("field-of-another-object")]
    [InlineData("through-an-authored-callee")]
    [InlineData("key-then-channel")]
    public void AHandleWrittenToStorageIsNotItselfRefusedWhenNoSendNeedsTheAdmissionAfterIt(string shape)
    {
        CSharpCompilation compilation = Compile(R2Source(R2Admission + HeapStoreStatements(shape), HeapStoreDeclarations));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Diagnostics);
    }
}
