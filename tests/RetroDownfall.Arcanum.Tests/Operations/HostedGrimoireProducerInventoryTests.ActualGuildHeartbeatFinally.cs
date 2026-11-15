using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ActualGuildHeartbeatFinallyRetainsExactCompletionAcrossNormalAndFaultExit(bool fault, bool wrongTask)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildSpendRefreshService.cs");

        MethodDeclarationSyntax actual = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "RunClaimedAsync");

        LocalDeclarationStatementSyntax publication = actual.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Single(static statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "heartbeat"));

        TryStatementSyntax lifetime = Assert.IsType<TryStatementSyntax>(actual.Body.Statements[actual.Body.Statements.IndexOf(publication) + 1]);

        InvocationExpressionSyntax actualPublication = Assert.IsType<InvocationExpressionSyntax>(
            Assert.Single(publication.Declaration.Variables).Initializer!.Value);

        Assert.Equal("MonitorAsync", actualPublication.Expression.ToString());

        Assert.Equal("stopHeartbeat.Token", actualPublication.ArgumentList.Arguments.Last().Expression.ToString());

        BlockSyntax operationalBody = Assert.IsType<BlockSyntax>(SyntaxFactory.ParseStatement(
            "{ if (fault) throw new InvalidOperationException(\"fixture dispatch fault\"); await Task.Yield(); }"));

        if (lifetime.Finally!.Block.Statements is [TryStatementSyntax originalStop])
        {
            Assert.Equal("await stopHeartbeat.CancelAsync().ConfigureAwait(false);", Assert.Single(originalStop.Block.Statements).ToString());

            Assert.Equal("await heartbeat.ConfigureAwait(false);", Assert.Single(originalStop.Finally!.Block.Statements).ToString());

            CatchClauseSyntax mapping = Assert.Single(lifetime.Catches);

            Assert.Equal("Exception", mapping.Declaration!.Type.ToString());

            Assert.Equal("result = Unavailable;", Assert.Single(mapping.Block.Statements).ToString());

            // Replace operational dependencies only; retain the actual extracted cancellation/join structure.
            lifetime = lifetime.WithBlock(operationalBody).WithCatches(default);
        }
        else
        {
            Assert.Equal("await heartbeat.ConfigureAwait(false);", Assert.Single(lifetime.Finally.Block.Statements).ToString());

            TryStatementSyntax protectedBody = Assert.IsType<TryStatementSyntax>(Assert.Single(lifetime.Block.Statements));

            Assert.Equal("await stopHeartbeat.CancelAsync().ConfigureAwait(false);", Assert.Single(protectedBody.Finally!.Block.Statements).ToString());

            CatchClauseSyntax mapping = Assert.Single(protectedBody.Catches);

            Assert.Equal("Exception", mapping.Declaration!.Type.ToString());

            Assert.Equal("result = Unavailable;", Assert.Single(mapping.Block.Statements).ToString());

            lifetime = lifetime.WithBlock(SyntaxFactory.Block(
                protectedBody.WithBlock(operationalBody).WithCatches(default)));
        }

        string protectedLifetime = lifetime.ToFullString();

        if (wrongTask)
        {
            Assert.Equal(1, protectedLifetime.Split("await heartbeat.ConfigureAwait(false);", StringSplitOptions.None).Length - 1);

            protectedLifetime = protectedLifetime.Replace("await heartbeat.ConfigureAwait(false);",
                "await Task.CompletedTask.ConfigureAwait(false);", StringComparison.Ordinal);
        }

        string helper = $$"""
            static class GuildHeartbeatFixture
            {
                internal static async Task RunAsync(CancellationToken token, bool fault)
                {
                    using CancellationTokenSource stopHeartbeat = new();

                    Task heartbeat = KeepClaimAliveAsync(stopHeartbeat.Token);

                    {{protectedLifetime}}
                }

                private static async Task KeepClaimAliveAsync(CancellationToken stoppingToken)
                {
                    await Task.Yield();

                    System.IO.File.Delete("heartbeat-physical-effect");
                }
            }
            """;

        string source = R2Source(R2Admission + "await GuildHeartbeatFixture.RunAsync(token, " + (fault ? "true" : "false") + ");", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax heartbeatCall = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static call => call.Expression.ToString() == "KeepClaimAliveAsync");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(heartbeatCall));

        Assert.True(selected.TargetMethod.IsAsync);

        Assert.Equal("GuildHeartbeatFixture", selected.TargetMethod.ContainingType.Name);

        Assert.Equal(typeof(Task).Assembly.GetName().Name, selected.TargetMethod.ReturnType.ContainingAssembly.Name);

        Assert.Equal("Task", selected.TargetMethod.ReturnType.MetadataName);

        VariableDeclaratorSyntax selectedPublication = Assert.IsType<VariableDeclaratorSyntax>(heartbeatCall.Parent!.Parent);

        Assert.Equal("heartbeat", selectedPublication.Identifier.ValueText);

        Assert.Single(compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "gate.TryAcquireWorkLease");

        Assert.Single(compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "lease.TryBeginExternalEffectGroup");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string coordinate = "/call@" + heartbeatCall.SyntaxTree.FilePath + ":" + heartbeatCall.SpanStart;

        Assert.Equal(wrongTask, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith("GuildHeartbeatFixture.KeepClaimAliveAsync;", StringComparison.Ordinal)));

        HostedProducerSite physical = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(!wrongTask, physical.WorkFrontierCapsuleId is not null && physical.EffectFrontierCapsuleId is not null);
    }
}
