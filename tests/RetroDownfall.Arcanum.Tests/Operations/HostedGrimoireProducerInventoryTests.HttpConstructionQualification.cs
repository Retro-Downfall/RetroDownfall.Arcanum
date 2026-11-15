using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using System.Net.Http;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Registered_spend_reader_default_transport_closes_its_actual_cleanup_context()
    {
        var result = HostedGrimoireProducerInventory.ProbeProductionSpendHttpConstruction();

        Assert.True(result.RegisteredAbsent);

        Assert.True(result.Handler);

        Assert.True(result.Client);

        Assert.True(result.Request);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Connect_certificate_refuses_an_inert_builder_that_publishes_before_the_actual_connect_body_finishes()
    {
        CSharpCompilation[] compilations = HostedGrimoireProducerInventory.ProductionCompilations.ToArray();

        int index = Array.FindIndex(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        Assert.True(index >= 0);

        CSharpCompilation original = compilations[index];

        SyntaxTree tree = original.SyntaxTrees.Single(static tree => tree.FilePath.Replace('\\', '/').EndsWith("/Security/OutboundUrlGuard.cs", StringComparison.Ordinal));

        SyntaxNode root = tree.GetRoot();

        MethodDeclarationSyntax callee = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "EgressConnectCallbackAsync");

        AttributeListSyntax attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.ParseName("System.Runtime.CompilerServices.AsyncMethodBuilder"),
                SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName("HttpEarlyValueTaskBuilder<>"))))))));

        MethodDeclarationSyntax changed = callee.AddAttributeLists(attribute);

        Assert.True(SyntaxFactory.AreEquivalent(callee.Body, changed.Body));

        SyntaxTree mutatedTree = CSharpSyntaxTree.ParseText(root.ReplaceNode(callee, changed).ToFullString()
            + "\n" + InertHttpValueTaskBuilder, (CSharpParseOptions)tree.Options, tree.FilePath);

        compilations[index] = original.ReplaceSyntaxTree(tree, mutatedTree);

        Assert.Empty(compilations[index].GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Location.SourceTree == mutatedTree));

        var result = HostedGrimoireProducerInventory.ProbeSpendHttpConstruction(compilations);

        // No default/registration mutation occurred: this discriminates the callback
        // publication proof from a generic premise-ledger or service-registration refusal.
        Assert.True(result.RegisteredAbsent);

        Assert.False(result.Handler);

        Assert.False(result.Client);

        Assert.False(result.Request);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_HTTP_CONNECT_CERTIFICATE_UNPROVEN");
    }

    [Fact]
    public async Task Authored_handler_can_install_disposable_content_on_an_originally_empty_GET_request()
    {
        int disposed = 0;

        using var handler = new InstallingRequestContentHandler(() => disposed++);

        using var client = new HttpClient(handler, disposeHandler: false);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid"))
        {
            Assert.Null(request.Content);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Assert.IsType<InstalledRequestContent>(request.Content);

            Assert.Equal(0, disposed);
        }

        Assert.Equal(1, disposed);
    }

    private sealed class InstallingRequestContentHandler(Action onContentDispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Content = new InstalledRequestContent(onContentDispose);

            return Task.FromResult(new HttpResponseMessage());
        }
    }

    private sealed class InstalledRequestContent(Action onDispose) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose();

            base.Dispose(disposing);
        }
    }

    private const string InertHttpValueTaskBuilder = """
        internal sealed class HttpEarlyValueTaskBuilder<TResult>
        {
            public static HttpEarlyValueTaskBuilder<TResult> Create() => new HttpEarlyValueTaskBuilder<TResult>();
            public void Start<TStateMachine>(ref TStateMachine machine) where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine => machine.MoveNext();
            public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine machine) { }
            public void SetResult(TResult result) { }
            public void SetException(System.Exception exception) { }
            public System.Threading.Tasks.ValueTask<TResult> Task => default;
            public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { }
            public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                where TAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { }
        }
        """;
}
