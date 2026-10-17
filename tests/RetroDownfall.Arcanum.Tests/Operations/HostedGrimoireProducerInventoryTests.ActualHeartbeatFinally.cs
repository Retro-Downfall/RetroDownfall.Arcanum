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
    public void ActualPaidHeartbeatFinallyRetainsExactCompletionAcrossNormalAndFaultExit(bool fault, bool wrongTask)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Api/Intelligence/LongRestMaintenance.Dispatch.cs");

        MethodDeclarationSyntax actual = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "ExecutePaidAsync");

        LocalDeclarationStatementSyntax publication = actual.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Single(static statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "heartbeat"));

        TryStatementSyntax lifetime = Assert.IsType<TryStatementSyntax>(actual.Body.Statements[actual.Body.Statements.IndexOf(publication) + 1]);

        BlockSyntax operationalBody = Assert.IsType<BlockSyntax>(SyntaxFactory.ParseStatement(
            "{ if (fault) throw new InvalidOperationException(\"fixture dispatch fault\"); await Task.Yield(); }"));

        if (lifetime.Finally!.Block.Statements is [TryStatementSyntax originalStop])
        {
            Assert.Equal("await stop.CancelAsync().ConfigureAwait(false);", Assert.Single(originalStop.Block.Statements).ToString());

            Assert.Equal("await heartbeat.ConfigureAwait(false);", Assert.Single(originalStop.Finally!.Block.Statements).ToString());

            lifetime = lifetime.WithBlock(operationalBody);
        }
        else
        {
            Assert.Equal("await heartbeat.ConfigureAwait(false);", Assert.Single(lifetime.Finally.Block.Statements).ToString());

            TryStatementSyntax protectedBody = Assert.IsType<TryStatementSyntax>(Assert.Single(lifetime.Block.Statements));

            Assert.Equal("await stop.CancelAsync().ConfigureAwait(false);", Assert.Single(protectedBody.Finally!.Block.Statements).ToString());

            lifetime = lifetime.WithBlock(SyntaxFactory.Block(protectedBody.WithBlock(operationalBody)));
        }

        string protectedLifetime = lifetime.ToFullString();

        if (wrongTask)
        {
            Assert.Equal(1, protectedLifetime.Split("await heartbeat.ConfigureAwait(false);", StringSplitOptions.None).Length - 1);

            protectedLifetime = protectedLifetime.Replace("await heartbeat.ConfigureAwait(false);",
                "await Task.CompletedTask.ConfigureAwait(false);", StringComparison.Ordinal);
        }

        string helper = $$"""
            static class HeartbeatFixture
            {
                internal static async Task RunAsync(CancellationToken token, bool fault)
                {
                    using CancellationTokenSource stop = new();

                    Task heartbeat = KeepClaimAliveAsync(stop.Token);

                    {{protectedLifetime}}
                }

                private static async Task KeepClaimAliveAsync(CancellationToken stoppingToken)
                {
                    await Task.Yield();

                    System.IO.File.Delete("heartbeat-physical-effect");
                }
            }
            """;

        string source = R2Source(R2Admission + "await HeartbeatFixture.RunAsync(token, " + (fault ? "true" : "false") + ");", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax heartbeatCall = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static call => call.Expression.ToString() == "KeepClaimAliveAsync");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(heartbeatCall));

        Assert.True(selected.TargetMethod.IsAsync);

        Assert.Equal("HeartbeatFixture", selected.TargetMethod.ContainingType.Name);

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
            && diagnostic.Detail.StartsWith("HeartbeatFixture.KeepClaimAliveAsync;", StringComparison.Ordinal)));

        HostedProducerSite physical = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(!wrongTask, physical.WorkFrontierCapsuleId is not null && physical.EffectFrontierCapsuleId is not null);
    }
}
