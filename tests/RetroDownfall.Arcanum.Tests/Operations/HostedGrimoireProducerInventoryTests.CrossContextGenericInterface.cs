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
    public void CrossContextGenericInterfaceFactoryCannotDisappearFromTheHolderCensus(bool crossContext)
    {
        const string holderName = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string owner = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest";

        const string contractName = "ProbeContracts.ICarrier<" + holderName + ">";

        CSharpCompilation protocol = Compile("namespace ProbeContracts { public interface ICarrier<T> { } }")
            .WithAssemblyName("ClosedCarrier.Protocol");

        Assert.Empty(protocol.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        using MemoryStream protocolImage = new();

        Assert.True(protocol.Emit(protocolImage).Success);

        PortableExecutableReference protocolMetadata = MetadataReference.CreateFromImage(protocolImage.ToArray());

        CompilationReference protocolSource = protocol.ToMetadataReference();

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        source = source.ReplaceSdkFixtureText("LongRestSweepCoordinator(LongRestWorkAdmission workAdmission)",
            "LongRestSweepCoordinator(LongRestWorkAdmission workAdmission, global::" + contractName + " extra)", StringComparison.Ordinal);

        // This same-context factory receives no current holder: its actual payload
        // is null. It remains present beside the uncertain cross-context factory.
        source = source.ReplaceSdkFixtureText("public static void Configure(IServiceCollection services) { ",
            "public static void Configure(IServiceCollection services) { services.AddScoped<global::" + contractName
                + ">(sp => new global::" + owner + ".CrossContextCarrier<global::" + holderName + ">(null)); ", StringComparison.Ordinal);

        source += $$"""

            namespace {{owner}}
            {
                public sealed class CrossContextCarrier<T> : global::ProbeContracts.ICarrier<T>
                {
                    private readonly T value;

                    public CrossContextCarrier(T value)
                    {
                        this.value = value;
                    }
                }
            }
            """;

        ClassDeclarationSyntax api = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "ApiBootstrapper");

        string registrationSource = "";

        if (crossContext)
        {
            source = source.ReplaceSdkFixtureText(api.ToFullString(), "", StringComparison.Ordinal);

            source = source.ReplaceSdkFixtureText("public class Worker(",
                "[assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(\"ClosedCarrier.Registration\")] public class Worker(", StringComparison.Ordinal);

            string apiSource = api.ToFullString().ReplaceSdkFixtureText("global::Composition.Configure(services);",
                "global::Composition.Configure(services); global::CrossGenericRegistration.Add(services);", StringComparison.Ordinal);

            registrationSource = "using Microsoft.Extensions.DependencyInjection; namespace RetroDownfall.Arcanum.Api { " + apiSource + " } " + $$"""
                public static class CrossGenericRegistration
                {
                    public static void Add(IServiceCollection services)
                    {
                        services.AddScoped<global::{{contractName}}>(sp =>
                            new global::{{owner}}.CrossContextCarrier<global::{{holderName}}>(sp.GetRequiredService<global::{{holderName}}>()));
                    }
                }
                """;
        }

        CSharpCompilation worker = Compile(source).WithAssemblyName("ClosedCarrier.Worker")
            .AddReferences(protocolSource);

        CSharpCompilation? registration = crossContext
            ? Compile(registrationSource).WithAssemblyName("ClosedCarrier.Registration")
                .AddReferences(protocolMetadata, worker.ToMetadataReference())
            : null;

        CSharpCompilation[] compilations = registration is null ? [protocol, worker] : [protocol, worker, registration];

        Assert.All(compilations, static compilation => Assert.Empty(compilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        SyntaxTree workerTree = Assert.Single(worker.SyntaxTrees);

        SemanticModel workerModel = worker.GetSemanticModel(workerTree);

        SyntaxNode workerSyntax = workerTree.GetRoot();

        ClassDeclarationSyntax Type(string name) => Assert.Single(workerSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string type, string name) => Assert.Single(Type(type).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        INamedTypeSymbol coordinator = Assert.IsAssignableFrom<INamedTypeSymbol>(workerModel.GetDeclaredSymbol(Type("LongRestSweepCoordinator")));

        IMethodSymbol coordinatorConstructor = Assert.Single(coordinator.InstanceConstructors,
            static method => method.DeclaredAccessibility == Accessibility.Public);

        INamedTypeSymbol requested = Assert.IsAssignableFrom<INamedTypeSymbol>(coordinatorConstructor.Parameters[1].Type);

        Assert.Equal(contractName, requested.ToDisplayString());

        Assert.Equal(TypeKind.Interface, requested.TypeKind);

        Assert.True(requested.IsGenericType);

        Assert.Same(protocolSource, Assert.Single(worker.References.OfType<CompilationReference>(),
            reference => ReferenceEquals(reference.Compilation, protocol)));

        Assert.True(SymbolEqualityComparer.Default.Equals(protocol.GetTypeByMetadataName("ProbeContracts.ICarrier`1"), requested.OriginalDefinition));

        Assert.Equal(protocol.Assembly.Identity, requested.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(coordinatorConstructor.Parameters[0].Type, Assert.Single(requested.TypeArguments)));

        INamedTypeSymbol carrier = Assert.IsAssignableFrom<INamedTypeSymbol>(workerModel.GetDeclaredSymbol(Type("CrossContextCarrier")));

        IMethodSymbol sourceConstructor = Assert.Single(carrier.InstanceConstructors,
            static method => method.DeclaredAccessibility == Accessibility.Public);

        Assert.True(carrier.IsSealed);

        IFieldSymbol storage = Assert.Single(carrier.GetMembers().OfType<IFieldSymbol>());

        Assert.True(storage.IsReadOnly && !storage.IsStatic);

        ConstructorDeclarationSyntax constructorSyntax = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(sourceConstructor.DeclaringSyntaxReferences).GetSyntax());

        ISimpleAssignmentOperation stored = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(workerModel.GetOperation(
            Assert.Single(constructorSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())));

        Assert.True(SymbolEqualityComparer.Default.Equals(storage, Assert.IsAssignableFrom<IFieldReferenceOperation>(stored.Target).Field));

        Assert.True(SymbolEqualityComparer.Default.Equals(sourceConstructor.Parameters[0], Assert.IsAssignableFrom<IParameterReferenceOperation>(stored.Value).Parameter));

        InvocationExpressionSyntax benignSyntax = Assert.Single(Method("Composition", "Configure").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => workerModel.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddScoped", TypeArguments: [INamedTypeSymbol type] }
                && SymbolEqualityComparer.Default.Equals(type, requested));

        IInvocationOperation benign = Assert.IsAssignableFrom<IInvocationOperation>(workerModel.GetOperation(benignSyntax));

        AssertMetadataReferenceOwns(worker, benign.TargetMethod.ContainingAssembly);

        AnonymousFunctionExpressionSyntax benignLambda = Assert.Single(benignSyntax.ArgumentList.Arguments).Expression as AnonymousFunctionExpressionSyntax
            ?? throw new InvalidOperationException("Expected same-context source factory.");

        IObjectCreationOperation benignCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(workerModel.GetOperation(benignLambda.Body));

        Assert.True(Assert.Single(benignCreation.Arguments).Value.ConstantValue.HasValue);

        Assert.Null(Assert.Single(benignCreation.Arguments).Value.ConstantValue.Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(sourceConstructor, benignCreation.Constructor!.OriginalDefinition));

        Assert.True(SymbolEqualityComparer.Default.Equals(requested, Assert.Single(Assert.IsAssignableFrom<INamedTypeSymbol>(benignCreation.Type).AllInterfaces)));

        if (registration is not null)
        {
            SyntaxTree registrationTree = Assert.Single(registration.SyntaxTrees);

            SemanticModel registrationModel = registration.GetSemanticModel(registrationTree);

            Assert.Same(protocolMetadata, Assert.Single(registration.References.OfType<PortableExecutableReference>(),
                reference => ReferenceEquals(reference, protocolMetadata)));

            IAssemblySymbol metadataOwner = Assert.IsAssignableFrom<IAssemblySymbol>(registration.GetAssemblyOrModuleSymbol(protocolMetadata));

            Assert.Equal(protocol.Assembly.Identity, metadataOwner.Identity);

            Assert.Empty(metadataOwner.GetTypeByMetadataName("ProbeContracts.ICarrier`1")!.DeclaringSyntaxReferences);

            MethodDeclarationSyntax add = Assert.Single(registrationTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
                static method => method.Identifier.ValueText == "Add");

            InvocationExpressionSyntax unsafeSyntax = Assert.Single(add.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => registrationModel.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddScoped" });

            IInvocationOperation unsafeRegistration = Assert.IsAssignableFrom<IInvocationOperation>(registrationModel.GetOperation(unsafeSyntax));

            INamedTypeSymbol uncertain = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(unsafeRegistration.TargetMethod.TypeArguments));

            Assert.Equal(requested.OriginalDefinition.MetadataName, uncertain.OriginalDefinition.MetadataName);

            Assert.Equal(requested.OriginalDefinition.ContainingNamespace.ToDisplayString(), uncertain.OriginalDefinition.ContainingNamespace.ToDisplayString());

            Assert.Equal(requested.ContainingAssembly.Identity, uncertain.ContainingAssembly.Identity);

            Assert.False(SymbolEqualityComparer.Default.Equals(requested, uncertain));

            Assert.False(SymbolEqualityComparer.Default.Equals(requested.OriginalDefinition, uncertain.OriginalDefinition));

            Assert.True(SymbolEqualityComparer.Default.Equals(metadataOwner.GetTypeByMetadataName("ProbeContracts.ICarrier`1"), uncertain.OriginalDefinition));

            AssertMetadataReferenceOwns(registration, unsafeRegistration.TargetMethod.ContainingAssembly);

            AnonymousFunctionExpressionSyntax factory = Assert.IsAssignableFrom<AnonymousFunctionExpressionSyntax>(Assert.Single(unsafeSyntax.ArgumentList.Arguments).Expression);

            IObjectCreationOperation actual = Assert.IsAssignableFrom<IObjectCreationOperation>(registrationModel.GetOperation(factory.Body));

            Assert.Equal(carrier.ContainingAssembly.Identity, actual.Constructor!.ContainingAssembly.Identity);

            Assert.Equal(sourceConstructor.MetadataName, actual.Constructor.MetadataName);

            Assert.Equal(sourceConstructor.Parameters.Length, actual.Constructor.Parameters.Length);

            IInvocationOperation resolution = Assert.IsAssignableFrom<IInvocationOperation>(Assert.Single(actual.Arguments).Value);

            Assert.Equal("GetRequiredService", resolution.TargetMethod.Name);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolution.TargetMethod.ContainingType.ToDisplayString());

            AssertMetadataReferenceOwns(registration, resolution.TargetMethod.ContainingAssembly);

            Assert.Equal(holderName, Assert.Single(resolution.TargetMethod.TypeArguments).ToDisplayString());

            Assert.Equal(coordinatorConstructor.Parameters[0].Type.ContainingAssembly.Identity, Assert.Single(resolution.TargetMethod.TypeArguments).ContainingAssembly.Identity);

            IAnonymousFunctionOperation closure = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(registrationModel.GetOperation(factory));

            IParameterSymbol provider = Assert.Single(closure.Symbol.Parameters);

            Assert.Equal("System.IServiceProvider", provider.Type.ToDisplayString());

            Assert.True(SymbolEqualityComparer.Default.Equals(provider, Assert.IsAssignableFrom<IParameterReferenceOperation>(Assert.Single(resolution.Arguments).Value).Parameter));

            Assert.True(SymbolEqualityComparer.Default.Equals(actual.Constructor.Parameters[0], Assert.Single(actual.Arguments).Parameter));

            Assert.True(Assert.Single(actual.Arguments).InConversion.IsIdentity && Assert.Single(actual.Arguments).OutConversion.IsIdentity);

            Assert.True(SymbolEqualityComparer.Default.Equals(uncertain, Assert.Single(Assert.IsAssignableFrom<INamedTypeSymbol>(actual.Type).AllInterfaces)));

            MethodDeclarationSyntax apiBody = Assert.Single(registrationTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
                static method => method.Identifier.ValueText == "AddArcanumApiServices");

            InvocationExpressionSyntax invocation = Assert.Single(apiBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "global::CrossGenericRegistration.Add");

            Assert.True(SymbolEqualityComparer.Default.Equals(registrationModel.GetDeclaredSymbol(add),
                Assert.IsAssignableFrom<IInvocationOperation>(registrationModel.GetOperation(invocation)).TargetMethod));
        }

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(compilations);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            compilations, hosted, [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.NotNull(acquired.CapsuleId);

        foreach (string member in new[] { "SendFirstAsync", "SendSecondAsync" })
        {
            InvocationExpressionSyntax sendSyntax = Assert.Single(Method("LongRestSweepCoordinator", member).DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            IInvocationOperation sendOperation = Assert.IsAssignableFrom<IInvocationOperation>(workerModel.GetOperation(sendSyntax));

            Assert.Equal("System.IO.File", sendOperation.TargetMethod.ContainingType.ToDisplayString());

            AssertMetadataReferenceOwns(worker, sendOperation.TargetMethod.ContainingAssembly);

            string coordinate = "/site@" + workerTree.FilePath + ":" + sendSyntax.SpanStart;

            HostedProducerSite send = Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete"
                && site.EnclosingType == owner + ".LongRestSweepCoordinator" && site.Member == member
                && site.OperationId.EndsWith(coordinate, StringComparison.Ordinal));

            HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                && site.EnclosingType == owner + ".LongRestSweepCoordinator" && site.Member == member
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            if (crossContext)
            {
                Assert.Null(group.WorkFrontierCapsuleId);

                Assert.Null(send.WorkFrontierCapsuleId);

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.StartsWith(send.AuthorityOperationId + "/", StringComparison.Ordinal)
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(acquired.CapsuleId, group.WorkFrontierCapsuleId);

                Assert.NotNull(group.CapsuleId);

                Assert.Equal(acquired.CapsuleId, send.WorkFrontierCapsuleId);

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
        }

        if (!crossContext) Assert.Empty(result.Diagnostics);
    }
}
