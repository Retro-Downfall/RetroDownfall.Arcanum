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
    public void Finally_owned_effect_cannot_survive_a_later_using_alias_at_physical_cleanup(bool laterAlias, bool earlierAlias)
    {
        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            + "System.IDisposable? shellGroup = null; try { "
            + "if (!lease.TryBeginExternalEffectGroup(out shellGroup)) return; "
            + (earlierAlias ? "using System.IDisposable alias = shellGroup; " : string.Empty)
            + "using ManualEffectPhysicalCleanup physical = new(); "
            + (laterAlias ? "using System.IDisposable alias = shellGroup; " : string.Empty)
            + "System.IO.File.Delete(\"manual-effect-body\"); await Task.Yield(); "
            + "} finally { " + FinallyOwnedGroupDisposal + " }";

        const string helper = """
            internal sealed class ManualEffectPhysicalCleanup : System.IDisposable
            {
                public void Dispose() { System.IO.File.Delete("manual-effect-cleanup"); }
            }
            """;

        CSharpCompilation compilation = Compile("#nullable enable\n" + R2Source(body, helper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode syntax = compilation.SyntaxTrees.Single().GetRoot();

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        TryStatementSyntax protector = Assert.Single(syntax.DescendantNodes().OfType<TryStatementSyntax>());

        Assert.True(SyntaxFactory.AreEquivalent(SyntaxFactory.ParseStatement("{ " + FinallyOwnedGroupDisposal + " }"), protector.Finally!.Block));

        InvocationExpressionSyntax acquisition = Assert.Single(protector.Block.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "lease.TryBeginExternalEffectGroup");

        IInvocationOperation acquired = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquisition));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", acquired.TargetMethod.ContainingType.ToDisplayString());

        IArgumentOperation groupArgument = Assert.Single(acquired.Arguments);

        Assert.Equal(RefKind.Out, groupArgument.Parameter!.RefKind);

        ILocalReferenceOperation groupValue = Assert.IsAssignableFrom<ILocalReferenceOperation>(groupArgument.Value);

        Assert.Equal("shellGroup", groupValue.Local.Name);

        InvocationExpressionSyntax release = Assert.Single(protector.Finally.Block.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation released = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(release));

        Assert.Equal("System.IDisposable", released.TargetMethod.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(groupValue.Local,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(released.Instance).Local));

        LocalDeclarationStatementSyntax[] owners = [.. protector.Block.Statements.OfType<LocalDeclarationStatementSyntax>()];

        Assert.Equal(laterAlias || earlierAlias ? 2 : 1, owners.Length);

        Assert.All(owners, static owner => Assert.NotEqual(0, owner.UsingKeyword.RawKind));

        int physicalIndex = earlierAlias ? 1 : 0;

        VariableDeclaratorSyntax physical = Assert.Single(owners[physicalIndex].Declaration.Variables);

        Assert.Equal("physical", physical.Identifier.ValueText);

        IObjectCreationOperation created = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(physical.Initializer!.Value));

        Assert.Equal("ManualEffectPhysicalCleanup", created.Type!.ToDisplayString());

        if (laterAlias || earlierAlias)
        {
            int aliasIndex = earlierAlias ? 0 : 1;

            VariableDeclaratorSyntax alias = Assert.Single(owners[aliasIndex].Declaration.Variables);

            Assert.Equal("alias", alias.Identifier.ValueText);

            Assert.True(SymbolEqualityComparer.Default.Equals(groupValue.Local, model.GetSymbolInfo(alias.Initializer!.Value).Symbol));

            Assert.Same(owners[0].Parent, owners[1].Parent);

            Assert.True(owners[0].Span.End < owners[1].SpanStart);
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite work = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite bodyEffect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "Worker");

        Assert.Equal(work.CapsuleId, bodyEffect.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, bodyEffect.EffectFrontierCapsuleId);

        HostedProducerSite cleanup = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ManualEffectPhysicalCleanup" && site.Member == "Dispose");

        Assert.Equal(HostedProducerSiteKind.FileSystemEffect, cleanup.Kind);

        Assert.Equal(work.CapsuleId, cleanup.WorkFrontierCapsuleId);

        Assert.Equal(work.CapsuleId, cleanup.RegisteredRootWorkFrontierCapsuleId);

        if (laterAlias)
        {
            Assert.Null(cleanup.EffectFrontierCapsuleId);

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete");
        }
        else
        {
            Assert.Equal(group.CapsuleId, cleanup.EffectFrontierCapsuleId);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete");
        }
    }
}
