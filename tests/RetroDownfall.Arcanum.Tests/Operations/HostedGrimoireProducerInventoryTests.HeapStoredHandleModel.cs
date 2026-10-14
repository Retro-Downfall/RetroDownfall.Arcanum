using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The carrier model's treatment of a handle written to storage. The origin walk follows a handle through locals, formals and
    // the values built from them. A handle written to storage the walk cannot follow leaves the tracked environment at the
    // store, and that ends the admission there: every send after the store loses its Work and its Effect. Two kinds of storage
    // differ in what else the checker says about the store itself:
    //   - static storage (a static field or property, an element or member reached from one) is process-wide ambient state, never
    //     a supported home for a lease or an external-effect group, so the store is refused where it happens. Without that, a
    //     closed key, index or conversion that reads the stash back would carry a handle that Handle-Incapable Values correctly
    //     treat as unreachable from closed data.
    //   - instance storage (a field of another object, or of this) is where the supported holder shapes keep a lease, and the
    //     Lease Spine pins a lease stored in a field as ending the Work at the send, so the store is not refused by itself.
    private const string HeapStoreDeclarations = """
        static class Holder
        {
            internal static IDisposable? Kept;

            internal static IDisposable? KeptProperty { get; set; }

            internal static Box Shared = new();

            internal static System.Collections.Generic.Dictionary<string, IDisposable> Map = new();

            internal static string? Name;

            internal static CancellationTokenSource? Source;
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

    // The statements of the row, the exact text of the assignment that stores, and the static member the store lands in.
    private static (string Statements, string Store, string Root) HeapStoreRow(string shape) => shape switch
    {
        "static-field" => ("Holder.Kept = held;", "Holder.Kept = held", "Holder.Kept"),
        "static-property" => ("Holder.KeptProperty = held;", "Holder.KeptProperty = held", "Holder.KeptProperty"),
        "static-array-element" => ("Stash.Items[0] = held;", "Stash.Items[0] = held", "Stash.Items"),
        "static-coalesce" => ("Holder.Kept ??= held;", "Holder.Kept ??= held", "Holder.Kept"),
        "static-deconstruction" => ("int count; (Holder.Kept, count) = (held, 1);", "(Holder.Kept, count) = (held, 1)", "Holder.Kept"),
        "field-of-a-static-object" => ("Holder.Shared.Kept = held;", "Holder.Shared.Kept = held", "Holder.Shared"),
        "static-indexer" => ("Holder.Map[\"k\"] = held;", "Holder.Map[\"k\"] = held", "Holder.Map"),
        "through-an-authored-callee" => ("Stash.Put(held);", "Items[0] = g", "Stash.Items"),
        "key-then-channel" => ("int slot = Stash.Put(held); Stash.Forward(slot);", "Items[0] = g", "Stash.Items"),
        "field-of-another-object" => ("Box box = new(); box.Kept = held;", "box.Kept = held", ""),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private (CSharpCompilation Compilation, HostedProducerDiscovery<HostedProducerSite> Result) RunHeapStore(string shape, string statements, string after = "")
    {
        CSharpCompilation compilation = Compile(R2Source(R2Admission + statements + after, HeapStoreDeclarations));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"HEAP_STORED_HANDLE_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return (compilation, result);
    }

    [Theory]
    [InlineData("static-field")]
    [InlineData("static-array-element")]
    [InlineData("field-of-another-object")]
    [InlineData("through-an-authored-callee")]
    [InlineData("key-then-channel")]
    public void AHandleWrittenToStorageEndsTheAdmissionAtTheStoreSoTheSendAfterItLosesItsEffect(string shape)
    {
        (CSharpCompilation compilation, HostedProducerDiscovery<HostedProducerSite> result) = RunHeapStore(shape, HeapStoreRow(shape).Statements, " System.IO.File.Delete(\"after\");");

        InvocationExpressionSyntax send = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Identity.EndsWith("/site@" + send.SyntaxTree.FilePath + ":" + send.SpanStart, StringComparison.Ordinal));
    }

    // The store itself is refused where it happens, whatever reads the stash back afterwards and whether or not any send needs
    // the admission after it.
    [Theory]
    [InlineData("static-field")]
    [InlineData("static-property")]
    [InlineData("static-array-element")]
    [InlineData("static-coalesce")]
    [InlineData("static-deconstruction")]
    [InlineData("field-of-a-static-object")]
    [InlineData("static-indexer")]
    [InlineData("through-an-authored-callee")]
    [InlineData("key-then-channel")]
    public void AHandleWrittenToStaticStorageIsRefusedAtTheStore(string shape)
    {
        (string statements, string storeText, string root) = HeapStoreRow(shape);

        (CSharpCompilation compilation, HostedProducerDiscovery<HostedProducerSite> result) = RunHeapStore(shape, statements);

        AssignmentExpressionSyntax store = Assert.Single(compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.ToString() == storeText);

        HostedProducerInventoryDiagnostic[] refused = [.. result.Diagnostics.Where(diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith("/store@" + store.SyntaxTree.FilePath + ":" + store.SpanStart, StringComparison.Ordinal))];

        Assert.NotEmpty(refused);

        Assert.All(refused, diagnostic => Assert.StartsWith(root + ";", diagnostic.Detail, StringComparison.Ordinal));
    }

    // KNOWN LIMIT. Instance storage is where the supported holder shapes keep a lease, so a handle written to a field of another
    // object is not refused by itself: only the sends after it lose the admission. A stash in instance storage that a closed key
    // reads back is therefore not refused. Closing this means a holder-aware store proof, which also changes how a lease stored
    // in a field is modeled (LeaseSpineGrantsTheWorkersWorkAndTheGroupsEffectAtTheSendOrEndsThemAtTheExactDeparture, the
    // stored-in-field row); when it is closed, this row must flip to assert the escape at the store.
    [Fact]
    public void AHandleWrittenToInstanceStorageIsNotItselfRefusedWhenNoSendNeedsTheAdmissionAfterIt()
    {
        (_, HostedProducerDiscovery<HostedProducerSite> result) = RunHeapStore("field-of-another-object", HeapStoreRow("field-of-another-object").Statements);

        Assert.Empty(result.Diagnostics);
    }

    // The refusal is for a handle in static storage only. Local copies, closed data derived from the lease, and a disposable
    // that has nothing to do with the lease are stored and copied freely.
    [Theory]
    [InlineData("local-alias", "IDisposable alias = held; IDisposable other; other = alias;")]
    [InlineData("closed-data-into-a-static", "string kind = lease is not null ? \"known\" : \"unknown\"; Holder.Name = kind;")]
    [InlineData("unrelated-disposable-into-a-static", "Holder.Source = new CancellationTokenSource();")]
    public void ADataValueALocalCopyOrAnUnrelatedDisposableIsNotARefusedStore(string shape, string statements)
    {
        (_, HostedProducerDiscovery<HostedProducerSite> result) = RunHeapStore(shape, statements);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Identity.Contains("/store@", StringComparison.Ordinal));
    }
}
