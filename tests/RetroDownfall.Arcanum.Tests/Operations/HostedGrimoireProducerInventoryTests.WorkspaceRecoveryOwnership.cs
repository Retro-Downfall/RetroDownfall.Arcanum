using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_workspace_recovery_adapter_joins_its_broker_before_returning(bool detachBroker)
    {
        const string relative = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildWorkspaceMutationFence.cs";

        CompilationUnitSyntax original = CSharpSyntaxTree.ParseText(File.ReadAllText(
            Path.Combine(TestRepositoryPaths.RepositoryRoot(), relative))).GetCompilationUnitRoot();

        ClassDeclarationSyntax owner = original.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "GuildWorkspaceMutationFence");

        ClassDeclarationSyntax lease = owner.Members.OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "Lease");

        MethodDeclarationSyntax recovery = lease.Members.OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "RetainRecoveryAsync");

        InvocationExpressionSyntax brokerCall = Assert.Single(recovery.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "owner.Broker.RetainRecoveryAsync");

        if (detachBroker)
        {
            recovery = recovery.WithModifiers(SyntaxFactory.TokenList(recovery.Modifiers
                    .Where(static modifier => !modifier.IsKind(SyntaxKind.AsyncKeyword))))
                .WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ParseStatement("_ = " + brokerCall + ";"),
                    SyntaxFactory.ParseStatement("return default;")));
        }

        // Keep the production adapter body, its nested identity, constructor bindings,
        // and owner getter. Only the broker below is a synthetic effect sentinel.
        ClassDeclarationSyntax selectedLease = lease.WithBaseList(null)
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(recovery));

        PropertyDeclarationSyntax getter = owner.Members.OfType<PropertyDeclarationSyntax>()
            .Single(static property => property.Identifier.ValueText == "Broker");

        MemberDeclarationSyntax invocation = SyntaxFactory.ParseMemberDeclaration("private sealed class Invocation { }")!;

        MemberDeclarationSyntax exercise = SyntaxFactory.ParseMemberDeclaration("""
            internal async Task ExerciseRecoveryAsync(CancellationToken token)
            {
                Lease retained = new(this, null!, null!);
                _ = await retained.RetainRecoveryAsync("fixture", token).ConfigureAwait(false);
            }
            """)!;

        ClassDeclarationSyntax selected = owner.WithBaseList(null)
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([getter, selectedLease, invocation, exercise]));

        CompilationUnitSyntax isolated = original.ReplaceNode(owner, selected)
            .WithUsings(SyntaxFactory.List(original.Usings.Where(static directive => directive.Name?.ToString()
                is "RetroDownfall.Arcanum.Core.Guild" or "RetroDownfall.Arcanum.Core.Primitives")));

        SyntaxTree actual = CSharpSyntaxTree.Create(isolated, path: relative);

        const string sentinel = """
            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                internal sealed class GuildWorkspaceLeaseBroker
                {
                    internal async Task<RetroDownfall.Arcanum.Core.Primitives.Result> RetainRecoveryAsync(
                        RetroDownfall.Arcanum.Core.Guild.GuildMutationPermit permit, string reasonCode, CancellationToken cancellationToken)
                    {
                        await Task.CompletedTask;
                        System.IO.File.Delete("recovery-retention-sentinel");
                        return null!;
                    }
                }
            }
            """;

        string fixture = R2Source(R2Admission + """
            var owner = new RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceMutationFence(
                new RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceLeaseBroker());
            await owner.ExerciseRecoveryAsync(token);
            """, sentinel);

        SyntaxTree globals = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Threading; global using System.Threading.Tasks;",
            path: "src/Recovery.GlobalUsings.g.cs");

        var compilation = CompileWithProductionReferencePack(fixture, "RetroDownfall.Arcanum.Infrastructure")
            .AddSyntaxTrees(actual, globals);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        const string brokerTarget = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceLeaseBroker.RetainRecoveryAsync;";

        bool refused = result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith(brokerTarget, StringComparison.Ordinal));

        Assert.Equal(detachBroker, refused);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        if (detachBroker)
        {
            Assert.Null(effect.EffectFrontierCapsuleId);
        }
        else
        {
            Assert.NotNull(effect.WorkFrontierCapsuleId);
            Assert.NotNull(effect.EffectFrontierCapsuleId);
            Assert.Empty(result.Diagnostics);
        }
    }
}
