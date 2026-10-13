using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("baseline", true)]
    [InlineData("unused-members", true)]
    [InlineData("disposal-initializer", false)]
    [InlineData("opaque-initializer", false)]
    public void OrdinaryConstructorProjectionSeparatesUnusedMembersFromExecutedInitializers(string shape, bool admitted)
    {
        const string coordinatorName = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        SyntaxNode originalTree = CSharpSyntaxTree.ParseText(source).GetRoot();

        ClassDeclarationSyntax original = Assert.Single(originalTree.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        string unused = shape == "baseline" ? "" : string.Join(global::System.Environment.NewLine,
            Enumerable.Range(0, 130).Select(index => $"private void Dormant{index}() {{ }} private sealed class DormantType{index} {{ }}"));

        string initializer = shape switch
        {
            "disposal-initializer" => "private readonly bool observed = ObserveConstruction(workAdmission); "
                + "private static bool ObserveConstruction(LongRestWorkAdmission admission) { admission.Lease.Dispose(); return true; }",
            "opaque-initializer" => "private readonly bool observed = ObserveConstruction(workAdmission); "
                + "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)] "
                + "private static extern bool ObserveConstruction(LongRestWorkAdmission admission);",
            _ => "",
        };

        // Add only declarations inside this existing selected primary-constructor type.
        string replacement = original.ToFullString();

        replacement = replacement.Insert(replacement.IndexOf('{') + 1,
            global::System.Environment.NewLine + initializer + global::System.Environment.NewLine + unused + global::System.Environment.NewLine);

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

        IParameterSymbol captured = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
            Assert.Single(coordinator.ParameterList!.Parameters)));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", captured.Type.ToDisplayString());

        IMethodSymbol selectedConstructor = Assert.IsAssignableFrom<IMethodSymbol>(captured.ContainingSymbol);

        Assert.Equal(MethodKind.Constructor, selectedConstructor.MethodKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(selectedConstructor,
            Assert.Single(coordinatorType.InstanceConstructors, static constructor => constructor.DeclaredAccessibility == Accessibility.Public)));

        Assert.Empty(coordinator.Members.OfType<ConstructorDeclarationSyntax>());

        MethodDeclarationSyntax[] dormantMethods = coordinator.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText.StartsWith("Dormant", StringComparison.Ordinal)).ToArray();

        ClassDeclarationSyntax[] dormantTypes = coordinator.Members.OfType<ClassDeclarationSyntax>()
            .Where(static type => type.Identifier.ValueText.StartsWith("DormantType", StringComparison.Ordinal)).ToArray();

        Assert.Equal(shape == "baseline" ? 0 : 130, dormantMethods.Length);

        Assert.Equal(dormantMethods.Length, dormantTypes.Length);

        IMethodSymbol[] invokedMethods = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol).OfType<IMethodSymbol>().ToArray();

        ITypeSymbol[] constructedTypes = syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Select(creation => model.GetTypeInfo(creation).Type).OfType<ITypeSymbol>().ToArray();

        foreach (MethodDeclarationSyntax dormant in dormantMethods)
        {
            IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(dormant));

            Assert.Equal(MethodKind.Ordinary, method.MethodKind);

            Assert.Equal(Accessibility.Private, method.DeclaredAccessibility);

            Assert.Empty(dormant.Body!.Statements);

            Assert.DoesNotContain(invokedMethods, invoked => SymbolEqualityComparer.Default.Equals(invoked, method));
        }

        foreach (ClassDeclarationSyntax dormant in dormantTypes)
        {
            INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(dormant));

            Assert.Empty(dormant.Members);

            Assert.DoesNotContain(constructedTypes, constructed => SymbolEqualityComparer.Default.Equals(constructed, type));
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

        if (!admitted)
        {
            VariableDeclaratorSyntax observed = Assert.Single(coordinator.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(static field => field.Declaration.Variables));

            IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(observed));

            Assert.False(field.IsStatic);

            Assert.True(field.IsReadOnly);

            InvocationExpressionSyntax actualInitializer = Assert.IsType<InvocationExpressionSyntax>(observed.Initializer!.Value);

            var call = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(actualInitializer));

            IArgumentOperation argument = Assert.Single(call.Arguments);

            Assert.Equal(RefKind.None, argument.Parameter!.RefKind);

            Assert.True(argument.InConversion.IsIdentity && argument.OutConversion.IsIdentity);

            Assert.True(SymbolEqualityComparer.Default.Equals(captured,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(argument.Value).Parameter));

            MethodDeclarationSyntax observer = Assert.Single(coordinator.Members.OfType<MethodDeclarationSyntax>(),
                static method => method.Identifier.ValueText == "ObserveConstruction");

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(observer), call.TargetMethod));

            Assert.True(call.TargetMethod.IsStatic);

            Assert.Equal(compilation.Assembly.Identity, call.TargetMethod.ContainingAssembly.Identity);

            if (shape == "opaque-initializer")
            {
                Assert.True(call.TargetMethod.IsExtern);

                Assert.Null(observer.Body);

                Assert.Null(observer.ExpressionBody);
            }
            else
            {
                var dispose = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(
                    Assert.Single(observer.DescendantNodes().OfType<InvocationExpressionSyntax>())));

                Assert.Equal("System.IDisposable", dispose.TargetMethod.ContainingType.ToDisplayString());

                Assert.Equal("Dispose", dispose.TargetMethod.Name);

                var lease = Assert.IsAssignableFrom<IPropertyReferenceOperation>(dispose.Instance);

                Assert.Equal("Lease", lease.Property.Name);

                Assert.True(SymbolEqualityComparer.Default.Equals(captured.Type, lease.Property.ContainingType));

                Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(call.TargetMethod.Parameters),
                    Assert.IsAssignableFrom<IParameterReferenceOperation>(lease.Instance).Parameter));
            }
        }

        output.WriteLine("CONSTRUCTOR_MEMBER_PROJECTION_PREMISES shape=" + shape + " unusedDeclarationsAndActualInitializer=passed");

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

                Assert.NotNull(send.WorkFrontierCapsuleId);

                Assert.NotNull(send.EffectFrontierCapsuleId);

                Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

                HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                    && site.EnclosingType == coordinatorName && site.Member == send.Member
                    && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

                Assert.NotNull(group.CapsuleId);

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
            else
            {
                Assert.Null(send.WorkFrontierCapsuleId);

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
            }
        }

        if (admitted)
        {
            Assert.NotEqual(physical[0].EffectFrontierCapsuleId, physical[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
