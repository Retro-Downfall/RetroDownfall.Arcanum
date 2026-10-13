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
    public void Per_unit_transfer_retains_exact_owner_when_hosted_catalog_uses_a_short_service_name(bool namespaced)
    {
        string source = PerUnitRecoveryMatrixFixture("same-lease");

        if (namespaced) source = NamespaceRecoveryWorker(source, moveAdmissionToWrongOwner: false);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        string owner = namespaced ? "Fixture.NamedRecovery.Worker" : "Worker";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverExactRecoveryWorker([compilation], owner);

        Assert.Empty(result.Diagnostics);

        HostedProducerSite frontier = Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == owner && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite[] sends = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler"
            && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, sends.Length);

        Assert.All(sends, send =>
        {
            Assert.Equal(frontier.CapsuleId, send.WorkFrontierCapsuleId);

            Assert.NotNull(send.EffectFrontierCapsuleId);
        });
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-compilation")]
    [InlineData("wrong-assembly")]
    [InlineData("wrong-path")]
    [InlineData("early-binding-disposal")]
    public void Short_service_name_cannot_supply_another_owners_compilation_or_path_transfer(string mutation)
    {
        string shape = mutation == "early-binding-disposal" ? mutation : "same-lease";

        string source = NamespaceRecoveryWorker(PerUnitRecoveryMatrixFixture(shape), mutation == "wrong-owner");

        CSharpCompilation compilation = Compile(source);

        if (mutation == "wrong-path")
        {
            compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, path: "src/Other.cs"));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = mutation is "wrong-compilation" or "wrong-assembly"
            ? [compilation, mutation == "wrong-assembly" ? Compile(source).WithAssemblyName("ForeignInventoryFixture") : Compile(source)]
            : [compilation];

        if (compilations.Length == 2)
        {
            Assert.NotSame(compilations[0], compilations[1]);

            Assert.Equal(mutation == "wrong-compilation", compilations[0].Assembly.Identity.Equals(compilations[1].Assembly.Identity));
        }

        Assert.All(compilations, candidate => Assert.Empty(candidate.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverExactRecoveryWorker(compilations, "Fixture.NamedRecovery.Worker");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED" or "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN");

        Assert.DoesNotContain(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler"
            && site.Callee == "System.IO.File.Delete" && (site.WorkFrontierCapsuleId is not null || site.EffectFrontierCapsuleId is not null));
    }

    private static string NamespaceRecoveryWorker(string source, bool moveAdmissionToWrongOwner)
    {
        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax worker = Assert.Single(unit.Members.OfType<ClassDeclarationSyntax>(), static type => type.Identifier.ValueText == "Worker");

        if (moveAdmissionToWrongOwner)
        {
            MethodDeclarationSyntax start = Assert.Single(worker.Members.OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "StartAsync");

            MethodDeclarationSyntax foreign = start.WithIdentifier(SyntaxFactory.Identifier("ExecuteAsync"));

            MethodDeclarationSyntax forwarding = start.WithBody(SyntaxFactory.Block(SyntaxFactory.ParseStatement(
                "await new Fixture.ForeignRecovery.Worker().ExecuteAsync(token);")));

            worker = worker.ReplaceNode(start, forwarding);

            unit = unit.AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName("Fixture.ForeignRecovery"))
                .AddMembers(SyntaxFactory.ClassDeclaration("Worker").AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(foreign)));
        }

        ClassDeclarationSyntax original = Assert.Single(unit.Members.OfType<ClassDeclarationSyntax>(), static type => type.Identifier.ValueText == "Worker");

        unit = unit.ReplaceNode(original, SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName("Fixture.NamedRecovery")).AddMembers(worker));

        return unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Fixture.NamedRecovery"))).NormalizeWhitespace().ToFullString();
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverExactRecoveryWorker(IReadOnlyList<CSharpCompilation> compilations, string owner)
    {
        HostedProducerOperationEntry root = OrdinaryRoot(owner + ".StartAsync") with { EnclosingType = owner };

        NonHostedProducerChainEntry[] owners =
        [
            new("RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot.Run", "src/Fixture.cs",
                "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot", "Run", HostedProducerAuthorityKind.OwnerBoundRecovery,
                "exact owner fixture", []),
        ];

        return HostedGrimoireProducerInventory.DiscoverProducerSites(compilations, new(["Worker"], []), [new("Worker", [root])], owners);
    }
}
