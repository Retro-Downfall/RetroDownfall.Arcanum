using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("adapter", false, false, false)]
    [InlineData("unknown", true, false, false)]
    [InlineData("owned-http", false, true, false)]
    [InlineData("branching", false, false, false)]
    [InlineData("adapter", false, false, true)]
    [InlineData("unknown", true, false, true)]
    [InlineData("owned-http", false, true, true)]
    [InlineData("branching", false, false, true)]
    [InlineData("di-adapter", false, false, false)]
    [InlineData("di-unknown", true, false, false)]
    [InlineData("di-owned-http", false, true, false)]
    [InlineData("di-branching", false, false, false)]
    [InlineData("di-adapter", false, false, true)]
    [InlineData("di-unknown", true, false, true)]
    [InlineData("di-owned-http", false, true, true)]
    [InlineData("di-branching", false, false, true)]
    [InlineData("callback-null", false, false, false)]
    [InlineData("callback-null", false, false, true)]
    [InlineData("di-callback-null", false, false, false)]
    [InlineData("di-callback-null", false, false, true)]
    [InlineData("callback-outside", true, false, false)]
    [InlineData("di-callback-fallthrough", true, false, false)]
    [InlineData("compact-reassigned", true, false, false)]
    [InlineData("compact-reassigned", true, false, true)]
    [InlineData("compact-local-mutation", true, false, false)]
    [InlineData("compact-local-mutation", true, false, true)]
    public void HostedContinuationActualLeaseAndResultSourceRetainAdapterAndTransportOrigins(string shape, bool unresolved, bool transport, bool crossCore)
    {
        string leasePath = "src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs";

        CompilationUnitSyntax original = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), leasePath))).GetCompilationUnitRoot();

        ClassDeclarationSyntax lease = original.Members.OfType<FileScopedNamespaceDeclarationSyntax>().Single().Members
            .OfType<ClassDeclarationSyntax>().Single(static type => type.Identifier.ValueText == "ChatClientLease");

        FileScopedNamespaceDeclarationSyntax owner = original.Members.OfType<FileScopedNamespaceDeclarationSyntax>().Single();

        CompilationUnitSyntax selected = original.ReplaceNode(owner, owner.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(lease)))
            .WithUsings(SyntaxFactory.List(original.Usings.Where(static directive => directive.Name?.ToString() is "Microsoft.Extensions.AI" or "RetroDownfall.Arcanum.Core.Configuration")));

        SyntaxTree actualLease = CSharpSyntaxTree.Create(selected, path: leasePath);

        string resultPath = "src/RetroDownfall.Arcanum.Core/Primitives/Result.cs";

        SyntaxTree actualResult = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), resultPath)), path: resultPath);

        SyntaxTree globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs");

        bool injected = shape.StartsWith("di-", StringComparison.Ordinal);

        string behavior = injected ? shape[3..] : shape;

        string client = behavior == "unknown" ? "ActualLeaseInput.Client()"
            : "Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(new OpenAI.Chat.ChatClient(\"fixture-model\", new System.ClientModel.ApiKeyCredential(\"fixture-key\"), new OpenAI.OpenAIClientOptions()))";

        bool callbackGuard = behavior.StartsWith("callback-", StringComparison.Ordinal);

        string optionalBranches = behavior == "branching" || callbackGuard
            ? "if (ActualLeaseInput.Fails()) return new RetroDownfall.Arcanum.Core.Primitives.Error(\"fixture.failure\", \"fixture\"); if (ActualLeaseInput.Unsupported()) return RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>.Success(null); "
            : "";

        string helper = "internal interface IActualLeaseFactory { Task<RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>> ResolveAsync(CancellationToken token); } "
            + "internal sealed class ActualLeaseFactory : IActualLeaseFactory { public async Task<RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>> ResolveAsync(CancellationToken token) { "
            + optionalBranches + "await Task.CompletedTask; Microsoft.Extensions.AI.IChatClient client = " + client + "; "
            + "return RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>.Success(new(client, new RetroDownfall.Arcanum.Core.Configuration.ProviderSettings(), \"fixture-model\", ownedHttpClient: "
            + (transport ? "ActualLeaseInput.Transport()" : "null") + ")); } } "
            + "internal static class ActualLeaseInput { internal static extern Microsoft.Extensions.AI.IChatClient Client(); internal static extern System.Net.Http.HttpClient Transport(); internal static extern bool Fails(); internal static extern bool Unsupported(); internal static async Task Join(Func<Task> action) { await action(); } }"
            + "internal sealed class EffectfulLeaseChatClient : Microsoft.Extensions.AI.IChatClient { "
            + "public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken token = default) => throw new NotSupportedException(); "
            + "public System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken token = default) => throw new NotSupportedException(); "
            + "public object? GetService(Type serviceType, object? serviceKey = null) => null; public void Dispose() { System.IO.File.Delete(\"reassigned-client-cleanup\"); } }";

        string nullBody = callbackGuard && behavior != "callback-outside" ? "await ActualLeaseInput.Join(onNull); " : "";

        string use = "var resolved = await factory.ResolveAsync(token).ConfigureAwait(false); if (resolved.IsFailure) return; "
            + "if (resolved.Value is null) { " + nullBody + (behavior == "callback-fallthrough" ? "" : "return;") + " } "
            + (behavior == "callback-outside" ? "await ActualLeaseInput.Join(onNull); " : "")
            + "using RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease acquired = resolved.Value; System.IO.File.Exists(\"actual-lease-control\");";

        if (behavior == "compact-reassigned")
        {
            use = use.ReplaceSdkFixtureText("if (resolved.IsFailure) return; ",
                "if (resolved.IsFailure)return;resolved = RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>.Success(new(new EffectfulLeaseChatClient(), new RetroDownfall.Arcanum.Core.Configuration.ProviderSettings(), \"fixture-model\", ownedHttpClient: null));", StringComparison.Ordinal);
        }

        if (behavior == "compact-local-mutation")
        {
            use = use.ReplaceSdkFixtureText("if (resolved.IsFailure) return; ",
                "void ReplaceLease() { resolved = RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease?>.Success(new(new EffectfulLeaseChatClient(), new RetroDownfall.Arcanum.Core.Configuration.ProviderSettings(), \"fixture-model\", ownedHttpClient: null)); }if(resolved.IsFailure)return;ReplaceLease();", StringComparison.Ordinal);
        }

        string callback = "static () => { System.IO.File.Delete(\"null-guard-callback\"); return Task.CompletedTask; }";

        string source;

        if (injected)
        {
            helper += " internal sealed class ActualLeaseConsumer(IActualLeaseFactory factory) { internal async Task Run(CancellationToken token" + (callbackGuard ? ", Func<Task> onNull" : "") + ") { " + use + " } }";

            source = R2Source(R2Admission + "using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ActualLeaseConsumer>().Run(token" + (callbackGuard ? ", " + callback : "") + ");", helper)
                .ReplaceSdkFixtureText("public class Worker : IHostedService", "public sealed class Worker(IServiceScopeFactory scopes) : IHostedService", StringComparison.Ordinal)
                .ReplaceSdkFixtureText("new Worker();", "new Worker(provider.GetRequiredService<IServiceScopeFactory>());", StringComparison.Ordinal)
                .ReplaceSdkFixtureText("services.AddHostedService<Worker>();", "services.AddScoped<IActualLeaseFactory, ActualLeaseFactory>(); services.AddScoped<ActualLeaseConsumer>(); services.AddHostedService<Worker>();", StringComparison.Ordinal);
        }
        else if (callbackGuard)
        {
            helper += " internal static class ActualLeaseUse { internal static async Task Run(IActualLeaseFactory factory, Func<Task> onNull, CancellationToken token) { " + use + " } }";

            source = R2Source(R2Admission + "await ActualLeaseUse.Run(new ActualLeaseFactory(), " + callback + ", token);", helper);
        }
        else
        {
            source = R2Source(R2Admission + "IActualLeaseFactory factory = new ActualLeaseFactory(); " + use, helper);
        }

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromFile(typeof(global::RetroDownfall.Arcanum.Api.Intelligence.ChatClientLease).Assembly.Location))
            .AddSyntaxTrees(actualLease, globals);

        if (!crossCore) compilation = compilation.AddSyntaxTrees(actualResult);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        if (behavior.StartsWith("compact-", StringComparison.Ordinal))
        {
            SyntaxNode fixture = compilation.SyntaxTrees.Single(static tree => tree.FilePath == "src/Fixture.cs").GetRoot();

            IfStatementSyntax guard = Assert.Single(fixture.DescendantNodes().OfType<IfStatementSyntax>(), static statement => statement.Condition.ToString() == "resolved.IsFailure");

            AssignmentExpressionSyntax replacement = Assert.Single(fixture.DescendantNodes().OfType<AssignmentExpressionSyntax>(), static assignment => assignment.Left.ToString() == "resolved");

            if (behavior == "compact-reassigned") Assert.Equal(guard.Span.End, replacement.SpanStart);
            else Assert.Equal(guard.Span.End, Assert.Single(fixture.DescendantNodes().OfType<InvocationExpressionSyntax>(), static call => call.Expression.ToString() == "ReplaceLease").SpanStart);
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            crossCore ? [HostedGrimoireProducerInventory.ProductionCompilations.Single(static source => source.AssemblyName == "RetroDownfall.Arcanum.Core"), compilation] : [compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists");

        if (behavior.StartsWith("compact-", StringComparison.Ordinal))
        {
            Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED")
                || result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null));

            return;
        }

        Assert.Equal(unresolved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal)));

        if (callbackGuard) Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.Equal(transport || behavior is "callback-outside" or "callback-fallthrough", result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.HttpMessageInvoker.Dispose"));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualLeaseProductionSubsetKeepsAdapterAndBorrowedTransportProvenance()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["Loremaster"]);

        Assert.NotEmpty(result.Items);

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/ChatClientFactory.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Net.Http.HttpMessageInvoker.Dispose")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.Callee == "System.ClientModel.Primitives.PipelineMessage..ctor" || site.Kind == HostedProducerSiteKind.ProviderCall);
    }
}
