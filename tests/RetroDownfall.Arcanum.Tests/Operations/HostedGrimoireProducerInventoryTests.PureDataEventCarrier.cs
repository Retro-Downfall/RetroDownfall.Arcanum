using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // A field-like event keeps its delegate in a backing field that the symbol model does not list among a type's members, so a
    // field scan alone reads a type whose only state is such an event as closed pure data. Its delegate can be a closure or a
    // method group over the held group, and every instance can carry one. The type is therefore not closed, and the value
    // derived from the group keeps its origin: boxing it and handing it to a framework callee that retains it is still a
    // retained handle with no proven lifetime continuation, and the send after it still loses its Effect. The delegate
    // field twin is the control: the same carrier with the storage spelled as a field.
    private const string PureDataEventCarrierDeclarations = """
        sealed class Paid
        {
            public string Key { get; init; } = "";

            public event Action? Later;

            public void Fire() => Later?.Invoke();
        }

        sealed class PaidField
        {
            public string Key { get; init; } = "";

            public Action? Later;

            public void Fire() => Later?.Invoke();
        }

        static class Door
        {
            internal static Paid Wrap(IDisposable held)
            {
                Paid wrapped = new() { Key = "k" };

                wrapped.Later += held.Dispose;

                return wrapped;
            }

            internal static Paid WrapLambda(IDisposable held)
            {
                Paid wrapped = new() { Key = "k" };

                wrapped.Later += () => held.Dispose();

                return wrapped;
            }

            internal static PaidField WrapField(IDisposable held)
            {
                PaidField wrapped = new() { Key = "k" };

                wrapped.Later = held.Dispose;

                return wrapped;
            }
        }

        static class Sink
        {
            internal static readonly System.Threading.Channels.Channel<object> Box = System.Threading.Channels.Channel.CreateBounded<object>(1);
        }
        """;

    [Theory]
    [InlineData("event-method-group-then-channel", "Paid paid = Door.Wrap(held); object boxed = paid; Sink.Box.Writer.TryWrite(boxed);", "Sink.Box.Writer.TryWrite")]
    [InlineData("event-lambda-then-keepalive", "Paid paid = Door.WrapLambda(held); object boxed = paid; GC.KeepAlive(boxed);", "GC.KeepAlive")]
    [InlineData("event-method-group-then-keepalive", "Paid paid = Door.Wrap(held); object boxed = paid; GC.KeepAlive(boxed);", "GC.KeepAlive")]
    [InlineData("delegate-field-then-channel", "PaidField paid = Door.WrapField(held); object boxed = paid; Sink.Box.Writer.TryWrite(boxed);", "Sink.Box.Writer.TryWrite")]
    public void AValueThatCarriesTheHeldGroupInAnEventStillEscapesAndStillCostsTheSendAfterItItsEffect(string shape, string statements, string consumerText)
    {
        string body = R2Admission + statements + " System.IO.File.Delete(\"after\");";

        CSharpCompilation compilation = Compile(R2Source(body, PureDataEventCarrierDeclarations));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = Assert.Single(compilation.SyntaxTrees).GetRoot();

        InvocationExpressionSyntax consumer = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString() == consumerText);

        InvocationExpressionSyntax send = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"PURE_DATA_EVENT_CARRIER_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith("/call@" + consumer.SyntaxTree.FilePath + ":" + consumer.SpanStart, StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Identity.EndsWith("/site@" + send.SyntaxTree.FilePath + ":" + send.SpanStart, StringComparison.Ordinal));
    }
}
