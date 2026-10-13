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
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedGenericConstructorFactsCannotEraseASecondHolderDependency(bool includeHolderCarrier)
    {
        const string owner = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest";

        const string holderName = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string coordinatorName = owner + ".LongRestSweepCoordinator";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        SyntaxNode before = CSharpSyntaxTree.ParseText(source).GetRoot();

        string secondParameter = includeHolderCarrier ? ", Carrier<LongRestWorkAdmission> second" : "";

        source = source.ReplaceSdkFixtureText("LongRestSweepCoordinator(LongRestWorkAdmission workAdmission)",
            "LongRestSweepCoordinator(LongRestWorkAdmission workAdmission, Carrier<Harmless> first" + secondParameter + ")",
            StringComparison.Ordinal);

        string registrations = "services.AddScoped<" + owner + ".Harmless>(); services.AddScoped<" + owner + ".Carrier<" + owner + ".Harmless>>(); ";

        if (includeHolderCarrier)
            registrations += "services.AddScoped<" + owner + ".Carrier<" + holderName + ">>(); ";

        source = source.ReplaceSdkFixtureText("public static void Configure(IServiceCollection services) { ",
            "public static void Configure(IServiceCollection services) { " + registrations, StringComparison.Ordinal);

        source += $$"""

            namespace {{owner}}
            {
                internal sealed class Harmless { public Harmless() { } }

                internal sealed class Carrier<T>
                {
                    private readonly T value;

                    public Carrier(T value)
                    {
                        this.value = value;
                    }
                }
            }
            """;

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string type, string name) => Assert.Single(Type(type).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        // Preserve the complete acquired/bound/joined root and both physical bodies.
        foreach (string methodName in new[] { "RunOneAsync", "ResumeAsync", "SendFirstAsync", "SendSecondAsync" })
        {
            string type = methodName == "RunOneAsync" ? "Worker" : "LongRestSweepCoordinator";

            MethodDeclarationSyntax original = Assert.Single(before.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Parent is ClassDeclarationSyntax declaringType && declaringType.Identifier.ValueText == type
                    && method.Identifier.ValueText == methodName);

            Assert.Equal(original.ToString(), Method(type, methodName).ToString());
        }

        INamedTypeSymbol carrier = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("Carrier")));

        Assert.True(carrier.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, carrier.ContainingAssembly.Identity);

        IMethodSymbol authoredConstructor = Assert.Single(carrier.InstanceConstructors,
            static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

        SyntaxReference constructorSource = Assert.Single(authoredConstructor.DeclaringSyntaxReferences);

        Assert.Same(tree, constructorSource.SyntaxTree);

        IFieldSymbol stored = Assert.Single(carrier.GetMembers().OfType<IFieldSymbol>());

        Assert.True(stored.IsReadOnly && !stored.IsStatic);

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(carrier.TypeParameters), stored.Type));

        ConstructorDeclarationSyntax body = Assert.IsType<ConstructorDeclarationSyntax>(constructorSource.GetSyntax());

        AssignmentExpressionSyntax assignment = Assert.Single(body.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        Assert.True(SymbolEqualityComparer.Default.Equals(stored, model.GetSymbolInfo(assignment.Left).Symbol));

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(authoredConstructor.Parameters), model.GetSymbolInfo(assignment.Right).Symbol));

        INamedTypeSymbol coordinator = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("LongRestSweepCoordinator")));

        IMethodSymbol selected = Assert.Single(coordinator.InstanceConstructors,
            static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

        Assert.Equal(includeHolderCarrier ? 3 : 2, selected.Parameters.Length);

        Assert.Equal(holderName, selected.Parameters[0].Type.ToDisplayString());

        INamedTypeSymbol first = Assert.IsAssignableFrom<INamedTypeSymbol>(selected.Parameters[1].Type);

        Assert.Equal(owner + ".Harmless", Assert.Single(first.TypeArguments).ToDisplayString());

        var registrationsByType = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => (Call: call, Method: model.GetSymbolInfo(call).Symbol as IMethodSymbol))
            .Where(pair => pair.Method is { Name: "AddScoped", TypeArguments.Length: 1 }
                && pair.Method.TypeArguments[0] is INamedTypeSymbol type
                && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, carrier)).ToArray();

        Assert.Equal(includeHolderCarrier ? 2 : 1, registrationsByType.Length);

        for (int index = 0; index < registrationsByType.Length; index++)
        {
            var registration = registrationsByType[index];

            IMethodSymbol add = registration.Method!;

            Assert.Equal("Configure", Assert.Single(registration.Call.Ancestors().OfType<MethodDeclarationSyntax>()).Identifier.ValueText);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", add.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", add.ContainingAssembly.Identity.Name);

            Assert.Empty(add.DeclaringSyntaxReferences);

            INamedTypeSymbol closed = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(add.TypeArguments));

            Assert.True(SymbolEqualityComparer.Default.Equals(selected.Parameters[index + 1].Type, closed));

            Assert.True(SymbolEqualityComparer.Default.Equals(carrier, closed.OriginalDefinition));

            IMethodSymbol constructor = Assert.Single(closed.InstanceConstructors,
                static candidate => candidate.DeclaredAccessibility == Accessibility.Public && !candidate.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(closed.TypeArguments), Assert.Single(constructor.Parameters).Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(authoredConstructor, constructor.OriginalDefinition));

            SyntaxReference declaration = Assert.Single(constructor.DeclaringSyntaxReferences);

            Assert.Same(constructorSource.SyntaxTree, declaration.SyntaxTree);

            Assert.Equal(constructorSource.Span, declaration.Span);
        }

        if (includeHolderCarrier)
        {
            INamedTypeSymbol second = Assert.IsAssignableFrom<INamedTypeSymbol>(selected.Parameters[2].Type);

            Assert.Equal(holderName, Assert.Single(second.TypeArguments).ToDisplayString());

            Assert.False(SymbolEqualityComparer.Default.Equals(first, second));

            Assert.False(SymbolEqualityComparer.Default.Equals(Assert.Single(first.TypeArguments), Assert.Single(second.TypeArguments)));

            Assert.True(registrationsByType[0].Call.Span.End < registrationsByType[1].Call.SpanStart);
        }

        output.WriteLine("CLOSED_GENERIC_CONSTRUCTOR_PREMISES holderCarrier=" + includeHolderCarrier
            + " closedTypesConstructorParametersRegistrationOrderAndSharedSource=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        string Coordinate(InvocationExpressionSyntax call) => "/site@" + tree.FilePath + ":" + call.SpanStart;

        InvocationExpressionSyntax acquire = Assert.Single(Method("Worker", "RunOneAsync").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.EndsWith(Coordinate(acquire), acquired.OperationId);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        Assert.NotNull(acquired.CapsuleId);

        HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == coordinatorName && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, physical.Length);

        foreach (string member in new[] { "SendFirstAsync", "SendSecondAsync" })
        {
            MethodDeclarationSyntax method = Method("LongRestSweepCoordinator", member);

            InvocationExpressionSyntax sendSyntax = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            InvocationExpressionSyntax groupSyntax = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" });

            HostedProducerSite send = Assert.Single(physical, site => site.Member == member);

            HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                && site.EnclosingType == coordinatorName && site.Member == member
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.EndsWith(Coordinate(sendSyntax), send.OperationId);

            Assert.EndsWith(Coordinate(groupSyntax), group.OperationId);

            if (includeHolderCarrier)
            {
                Assert.Null(group.WorkFrontierCapsuleId);

                Assert.Null(send.WorkFrontierCapsuleId);

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.EndsWith(Coordinate(sendSyntax), StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(acquired.CapsuleId, group.WorkFrontierCapsuleId);

                Assert.NotNull(group.CapsuleId);

                Assert.Equal(acquired.CapsuleId, send.WorkFrontierCapsuleId);

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
        }

        if (!includeHolderCarrier)
        {
            Assert.NotEqual(physical[0].EffectFrontierCapsuleId, physical[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
