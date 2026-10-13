using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Instance field declarations are constructor-executed storage, so each one
    // remains charged by the shared projection proof. Within budget they cannot
    // remove the witness; past the original 256-unit cap the proof fails closed.
    [Theory]
    [InlineData(8, true)]
    [InlineData(140, false)]
    public void OrdinaryConstructorProjectionStillChargesExecutedFieldDeclarations(int fields, bool admitted)
    {
        const string coordinatorName = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        SyntaxNode originalTree = CSharpSyntaxTree.ParseText(source).GetRoot();

        ClassDeclarationSyntax original = Assert.Single(originalTree.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        string declarations = string.Join(global::System.Environment.NewLine,
            Enumerable.Range(0, fields).Select(static index => $"private readonly int Reserved{index};"));

        string replacement = original.ToFullString();

        replacement = replacement.Insert(replacement.IndexOf('{') + 1,
            global::System.Environment.NewLine + declarations + global::System.Environment.NewLine);

        source = source.ReplaceSdkFixtureText(original.ToFullString(), replacement, StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax coordinator = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        INamedTypeSymbol coordinatorType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(coordinator));

        Assert.Equal(coordinatorName, coordinatorType.ToDisplayString());

        Assert.True(coordinatorType.IsSealed);

        Assert.Single(coordinator.ParameterList!.Parameters);

        Assert.Empty(coordinator.Members.OfType<ConstructorDeclarationSyntax>());

        VariableDeclaratorSyntax[] reserved = coordinator.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(static field => field.Declaration.Variables)
            .Where(static variable => variable.Identifier.ValueText.StartsWith("Reserved", StringComparison.Ordinal)).ToArray();

        Assert.Equal(fields, reserved.Length);

        IdentifierNameSyntax[] names = syntax.DescendantNodes().OfType<IdentifierNameSyntax>().ToArray();

        foreach (VariableDeclaratorSyntax variable in reserved)
        {
            IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(variable));

            Assert.False(field.IsStatic);

            Assert.True(field.IsReadOnly);

            Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);

            Assert.Null(variable.Initializer);

            Assert.DoesNotContain(names, name => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, field));
        }

        // Root scope/Bind/resolution/join and paid-send text remain identical, excluding outer trivia.
        foreach (string methodName in new[] { "RunOneAsync", "ResumeAsync", "SendFirstAsync", "SendSecondAsync" })
        {
            string owner = methodName == "RunOneAsync" ? "Worker" : "LongRestSweepCoordinator";

            MethodDeclarationSyntax before = Assert.Single(originalTree.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Parent is ClassDeclarationSyntax declaringType && declaringType.Identifier.ValueText == owner
                    && method.Identifier.ValueText == methodName);

            MethodDeclarationSyntax after = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Parent is ClassDeclarationSyntax declaringType && declaringType.Identifier.ValueText == owner
                    && method.Identifier.ValueText == methodName);

            Assert.Equal(before.ToString(), after.ToString());
        }

        output.WriteLine("CONSTRUCTOR_PROJECTION_BUDGET_PREMISES fields=" + fields + " executedUninitializedPrivateReadonly=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])], []);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquisition.AdmittedWorkKind);

        HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == coordinatorName && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, physical.Length);

        foreach (HostedProducerSite send in physical)
        {
            MethodDeclarationSyntax sendMethod = Assert.Single(coordinator.Members.OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == send.Member);

            InvocationExpressionSyntax sendSyntax = Assert.Single(sendMethod.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

            string coordinate = "/site@" + tree.FilePath + ":" + sendSyntax.SpanStart;

            Assert.EndsWith(coordinate, send.OperationId);

            if (admitted)
            {
                Assert.NotNull(acquisition.CapsuleId);

                Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);
            }
            else
            {
                Assert.Null(send.WorkFrontierCapsuleId);

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
            }
        }

        if (admitted) Assert.Empty(result.Diagnostics);
    }
}
