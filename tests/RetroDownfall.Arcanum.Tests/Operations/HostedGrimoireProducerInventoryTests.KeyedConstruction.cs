using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("unkeyed-safe")]
    [InlineData("selected-holder")]
    [InlineData("unknown-key")]
    [InlineData("competing-match")]
    [InlineData("opaque-selected-factory")]
    [InlineData("raw-selected-descriptor")]
    public void KeyedConstructionSelectsOnlyTheExactKeyWithoutErasingSelectedStorage(string shape)
    {
        const string owner = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest";

        const string holderName = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string probeName = owner + ".IKeyedConstructionProbe";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        SyntaxNode before = CSharpSyntaxTree.ParseText(source).GetRoot();

        bool secondSelection = shape == "selected-holder";

        bool unkeyedSafe = shape == "unkeyed-safe";

        string secondParameter = secondSelection ? ", IEnumerable<IKeyedConstructionProbe> capturedProbes" : "";

        source = source.ReplaceSdkFixtureText("LongRestSweepCoordinator(LongRestWorkAdmission workAdmission)",
            "LongRestSweepCoordinator(LongRestWorkAdmission workAdmission, IEnumerable<IKeyedConstructionProbe> selectedProbes"
                + secondParameter + ")", StringComparison.Ordinal);

        const string dispatchRegistration = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestSweepCoordinator, RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator>();";

        string key = shape == "unknown-key" ? owner + ".KeyedConstructionSelection.CurrentKey" : "\"selected\"";

        string additionalArgument = secondSelection ? ", sp.GetKeyedServices<" + probeName + ">(\"other\")" : "";

        string selectedLookup = unkeyedSafe ? "sp.GetServices<" + probeName + ">()"
            : "sp.GetKeyedServices<" + probeName + ">(" + key + ")";

        source = source.ReplaceSdkFixtureText(dispatchRegistration,
            "services.AddScoped<" + owner + ".LongRestSweepCoordinator>(static sp => new " + owner
                + ".LongRestSweepCoordinator(sp.GetRequiredService<" + holderName + ">(), " + selectedLookup
                + additionalArgument + ")); services.AddScoped<" + owner
                + ".ILongRestSweepCoordinator>(static sp => sp.GetRequiredService<" + owner
                + ".LongRestSweepCoordinator>());", StringComparison.Ordinal);

        string harmless = owner + ".KeyedConstructionCarrier<" + owner + ".KeyedConstructionHarmless>";

        string captured = owner + ".KeyedConstructionCarrier<" + holderName + ">";

        string selectedFactory = shape == "opaque-selected-factory"
            ? owner + ".KeyedConstructionSelection.Foreign"
            : "sp.GetRequiredService<" + harmless + ">()";

        string registrations = "services.AddScoped<" + owner + ".KeyedConstructionHarmless>(); services.AddScoped<"
            + harmless + ">(); services.AddScoped<" + captured + ">(); services.AddKeyedScoped<" + probeName
            + ">(\"selected\", static (sp, _) => " + selectedFactory + "); "
            + "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, "
            + "ServiceDescriptor.KeyedScoped<" + probeName + ", " + captured
            + ">(\"other\", static (sp, _) => sp.GetRequiredService<" + captured + ">())); ";

        if (unkeyedSafe)
        {
            registrations = "services.AddScoped<" + owner + ".KeyedConstructionHarmless>(); services.AddScoped<"
                + harmless + ">(); services.AddScoped<" + probeName
                + ">(static sp => sp.GetRequiredService<" + harmless + ">()); ";
        }

        if (shape == "competing-match")
        {
            registrations += "services.AddKeyedScoped<" + probeName
                + ">(\"selected\", static (sp, _) => sp.GetRequiredService<" + captured + ">()); ";
        }

        if (shape == "raw-selected-descriptor")
        {
            registrations += "services.Add(new ServiceDescriptor(typeof(" + probeName
                + "), \"selected\", static (sp, _) => sp.GetRequiredService<" + captured
                + ">(), ServiceLifetime.Scoped)); ";
        }

        source = source.ReplaceSdkFixtureText("public static void Configure(IServiceCollection services) { ",
            "public static void Configure(IServiceCollection services) { " + registrations, StringComparison.Ordinal);

        SyntaxNode constructed = CSharpSyntaxTree.ParseText(source).GetRoot();

        ClassDeclarationSyntax coordinatorSyntax = Assert.Single(constructed.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        source = source.Insert(coordinatorSyntax.OpenBraceToken.Span.End,
            "\n private readonly IEnumerable<IKeyedConstructionProbe> selectedStorage = selectedProbes;\n"
                + (secondSelection ? " private readonly IEnumerable<IKeyedConstructionProbe> capturedStorage = capturedProbes;\n" : ""));

        source += $$"""

            namespace {{owner}}
            {
                internal interface IKeyedConstructionProbe { }

                internal sealed class KeyedConstructionHarmless { public KeyedConstructionHarmless() { } }

                internal sealed class KeyedConstructionCarrier<T> : IKeyedConstructionProbe
                {
                    private readonly T value;

                    public KeyedConstructionCarrier(T value) { this.value = value; }
                }

                internal static class KeyedConstructionSelection
                {
                    public static object CurrentKey { get; set; } = "selected";

                    public static IKeyedConstructionProbe Foreign { get; set; } = null!;
                }
            }
            """;

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string type, string name) => Assert.Single(Type(type).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        foreach (string name in new[] { "RunOneAsync", "ResumeAsync", "SendFirstAsync", "SendSecondAsync" })
        {
            string declaring = name == "RunOneAsync" ? "Worker" : "LongRestSweepCoordinator";

            MethodDeclarationSyntax original = Assert.Single(before.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == name && method.Parent is ClassDeclarationSyntax type
                    && type.Identifier.ValueText == declaring);

            Assert.Equal(original.ToString(), Method(declaring, name).ToString());
        }

        void Metadata(IMethodSymbol method, string typeName)
        {
            IMethodSymbol declaration = method.ReducedFrom ?? method;

            Assert.Equal(typeName, declaration.ContainingType.ToDisplayString());

            Assert.Empty(declaration.DeclaringSyntaxReferences);

            var expected = typeof(IServiceCollection).Assembly.GetName();

            AssemblyIdentity identity = declaration.ContainingAssembly.Identity;

            Assert.Equal(expected.Name, identity.Name);

            Assert.Equal(expected.Version, identity.Version);

            Assert.Equal(expected.CultureName ?? "", identity.CultureName);

            Assert.Equal(expected.GetPublicKeyToken(), identity.PublicKeyToken.ToArray());

            Assert.Contains(compilation.References.OfType<PortableExecutableReference>(), reference =>
                SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(reference), declaration.ContainingAssembly));
        }

        INamedTypeSymbol probe = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(probeName));

        Assert.Equal(TypeKind.Interface, probe.TypeKind);

        Assert.Equal(compilation.Assembly.Identity, probe.ContainingAssembly.Identity);

        INamedTypeSymbol carrier = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("KeyedConstructionCarrier")));

        Assert.True(carrier.IsSealed && carrier.IsGenericType);

        Assert.Equal(compilation.Assembly.Identity, carrier.ContainingAssembly.Identity);

        IMethodSymbol carrierConstructor = Assert.Single(carrier.InstanceConstructors,
            static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

        IFieldSymbol value = Assert.Single(carrier.GetMembers().OfType<IFieldSymbol>());

        Assert.True(value.IsReadOnly && !value.IsStatic);

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(carrier.TypeParameters), value.Type));

        ConstructorDeclarationSyntax carrierBody = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(carrierConstructor.DeclaringSyntaxReferences).GetSyntax());

        AssignmentExpressionSyntax store = Assert.Single(carrierBody.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        Assert.True(SymbolEqualityComparer.Default.Equals(value, model.GetSymbolInfo(store.Left).Symbol));

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(carrierConstructor.Parameters), model.GetSymbolInfo(store.Right).Symbol));

        var calls = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => (Call: call, Operation: model.GetOperation(call) as IInvocationOperation)).ToArray();

        var lookups = calls.Where(pair => pair.Operation is { } selectedLookupOperation
            && selectedLookupOperation.TargetMethod.Name == (unkeyedSafe ? "GetServices" : "GetKeyedServices")
            && pair.Operation.TargetMethod.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], probe)).ToArray();

        Assert.Equal(secondSelection ? 2 : 1, lookups.Length);

        foreach (var lookup in lookups)
        {
            IInvocationOperation operation = lookup.Operation!;

            Metadata(operation.TargetMethod, unkeyedSafe
                ? "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"
                : "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions");

            IMethodSymbol declaration = operation.TargetMethod.ReducedFrom ?? operation.TargetMethod;

            Assert.Equal(unkeyedSafe ? 1 : 2, declaration.Parameters.Length);

            Assert.Equal("System.IServiceProvider", declaration.Parameters[0].Type.ToDisplayString());

            if (!unkeyedSafe) Assert.Equal(SpecialType.System_Object, declaration.Parameters[1].Type.SpecialType);

            Assert.All(declaration.Parameters, static parameter => Assert.Equal(RefKind.None, parameter.RefKind));

            INamedTypeSymbol returned = Assert.IsAssignableFrom<INamedTypeSymbol>(operation.Type);

            Assert.Equal(SpecialType.System_Collections_Generic_IEnumerable_T, returned.OriginalDefinition.SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(probe, Assert.Single(returned.TypeArguments)));

            MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(lookup.Call.Expression);

            IParameterSymbol provider = Assert.IsAssignableFrom<IParameterSymbol>(model.GetSymbolInfo(access.Expression).Symbol);

            Assert.Equal("System.IServiceProvider", provider.Type.ToDisplayString());

            Assert.Equal(MethodKind.AnonymousFunction, Assert.IsAssignableFrom<IMethodSymbol>(provider.ContainingSymbol).MethodKind);

            BaseObjectCreationExpressionSyntax made = Assert.Single(lookup.Call.Ancestors().OfType<BaseObjectCreationExpressionSyntax>());

            IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(made));

            Assert.Equal(owner + ".LongRestSweepCoordinator", allocation.Type!.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, allocation.Constructor!.ContainingAssembly.Identity);

            Assert.All(allocation.Constructor.DeclaringSyntaxReferences, declaration => Assert.Same(tree, declaration.SyntaxTree));

            Assert.Contains(allocation.Arguments, argument => argument.Value.Syntax.Span == lookup.Call.Span
                && SymbolEqualityComparer.Default.Equals(argument.Parameter!.Type, returned));
        }

        ExpressionSyntax? selectedKey = unkeyedSafe ? null : Assert.Single(lookups[0].Call.ArgumentList.Arguments).Expression;

        if (shape == "unknown-key")
        {
            Assert.False(model.GetConstantValue(selectedKey!).HasValue);

            IPropertyReferenceOperation keyRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(selectedKey!));

            Assert.True(keyRead.Property.IsStatic && keyRead.Property.SetMethod is { IsInitOnly: false });

            Assert.Equal(SpecialType.System_Object, keyRead.Property.Type.SpecialType);

            Assert.Equal(compilation.Assembly.Identity, keyRead.Property.ContainingAssembly.Identity);
        }
        else if (!unkeyedSafe)
        {
            Assert.Equal("selected", model.GetConstantValue(selectedKey!).Value);
        }

        if (secondSelection)
        {
            Assert.Equal("other", model.GetConstantValue(Assert.Single(lookups[1].Call.ArgumentList.Arguments).Expression).Value);

            Assert.True(lookups[0].Call.Span.End < lookups[1].Call.SpanStart);

            IMethodSymbol constructor = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(
                Assert.Single(lookups[0].Call.Ancestors().OfType<BaseObjectCreationExpressionSyntax>()))).Constructor!;

            Assert.Equal(3, constructor.Parameters.Length);

            Assert.True(SymbolEqualityComparer.Default.Equals(constructor.Parameters[1].Type, constructor.Parameters[2].Type));
        }

        var keyed = calls.Where(pair => pair.Operation is { TargetMethod.Name: "AddKeyedScoped" or "KeyedScoped" }
            && pair.Operation.TargetMethod.TypeArguments.Length > 0
            && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], probe)).ToArray();

        Assert.Equal(unkeyedSafe ? 0 : shape == "competing-match" ? 3 : 2, keyed.Length);

        List<INamedTypeSymbol> closed = [];

        foreach (var registration in keyed)
        {
            IMethodSymbol selected = registration.Operation!.TargetMethod;

            bool descriptor = selected.Name == "KeyedScoped";

            Metadata(selected, descriptor ? "Microsoft.Extensions.DependencyInjection.ServiceDescriptor"
                : "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");

            Assert.Equal(descriptor ? 2 : 1, selected.TypeArguments.Length);

            Assert.Equal("Configure", Assert.Single(registration.Call.Ancestors().OfType<MethodDeclarationSyntax>()).Identifier.ValueText);

            string registrationKey = Assert.IsType<string>(model.GetConstantValue(registration.Call.ArgumentList.Arguments[0].Expression).Value);

            Assert.Contains(registrationKey, new[] { "selected", "other" });

            if (descriptor)
            {
                Assert.Equal("other", registrationKey);

                IInvocationOperation addEnumerable = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(
                    Assert.Single(registration.Call.Ancestors().OfType<InvocationExpressionSyntax>())));

                Metadata(addEnumerable.TargetMethod, "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions");

                Assert.Equal("TryAddEnumerable", addEnumerable.TargetMethod.Name);
            }

            AnonymousFunctionExpressionSyntax factory = Assert.IsAssignableFrom<AnonymousFunctionExpressionSyntax>(registration.Call.ArgumentList.Arguments[1].Expression);

            IAnonymousFunctionOperation selectedFactoryOperation = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(model.GetOperation(factory));

            Assert.Equal(2, selectedFactoryOperation.Symbol.Parameters.Length);

            Assert.Equal("System.IServiceProvider", selectedFactoryOperation.Symbol.Parameters[0].Type.ToDisplayString());

            Assert.Equal(SpecialType.System_Object, selectedFactoryOperation.Symbol.Parameters[1].Type.SpecialType);

            InvocationExpressionSyntax? resolution = factory.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault();

            if (resolution is null)
            {
                Assert.Equal("opaque-selected-factory", shape);

                Assert.Equal("selected", registrationKey);

                IPropertyReferenceOperation read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(
                    Assert.IsType<MemberAccessExpressionSyntax>(Assert.IsType<ParenthesizedLambdaExpressionSyntax>(factory).Body)));

                Assert.True(read.Property.IsStatic && read.Property.SetMethod is { IsInitOnly: false });

                Assert.True(SymbolEqualityComparer.Default.Equals(probe, read.Property.Type));

                Assert.Equal(compilation.Assembly.Identity, read.Property.ContainingAssembly.Identity);

                continue;
            }

            IInvocationOperation required = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resolution));

            Metadata(required.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");

            Assert.Equal("GetRequiredService", required.TargetMethod.Name);

            Assert.True(SymbolEqualityComparer.Default.Equals(selectedFactoryOperation.Symbol.Parameters[0],
                model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression).Symbol));

            INamedTypeSymbol actual = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(required.TargetMethod.TypeArguments));

            Assert.True(SymbolEqualityComparer.Default.Equals(carrier, actual.OriginalDefinition));

            IMethodSymbol actualConstructor = Assert.Single(actual.InstanceConstructors,
                static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(carrierConstructor, actualConstructor.OriginalDefinition));

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(actual.TypeArguments), Assert.Single(actualConstructor.Parameters).Type));

            Assert.Contains(Assert.Single(actual.TypeArguments).ToDisplayString(), new[] { owner + ".KeyedConstructionHarmless", holderName });

            if (descriptor) Assert.True(SymbolEqualityComparer.Default.Equals(actual, selected.TypeArguments[1]));

            closed.Add(actual);
        }

        if (unkeyedSafe)
        {
            Assert.Empty(keyed);

            Assert.DoesNotContain(syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(), creation =>
                model.GetOperation(creation) is IObjectCreationOperation { Constructor.ContainingType.Name: "ServiceDescriptor" });

            var safeRegistration = Assert.Single(calls, pair => pair.Operation is { TargetMethod.Name: "AddScoped", TargetMethod.TypeArguments.Length: 1 }
                && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], probe));

            Metadata(safeRegistration.Operation!.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");

            SimpleLambdaExpressionSyntax factory = Assert.IsType<SimpleLambdaExpressionSyntax>(Assert.Single(safeRegistration.Call.ArgumentList.Arguments).Expression);

            IAnonymousFunctionOperation factoryOperation = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(model.GetOperation(factory));

            Assert.True(factoryOperation.Symbol.IsStatic);

            InvocationExpressionSyntax resolutionSyntax = Assert.IsType<InvocationExpressionSyntax>(factory.Body);

            IInvocationOperation resolution = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resolutionSyntax));

            Metadata(resolution.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");

            Assert.Equal("GetRequiredService", resolution.TargetMethod.Name);

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(factoryOperation.Symbol.Parameters),
                model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(resolutionSyntax.Expression).Expression).Symbol));

            INamedTypeSymbol actual = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(resolution.TargetMethod.TypeArguments));

            Assert.True(SymbolEqualityComparer.Default.Equals(carrier, actual.OriginalDefinition));

            Assert.Equal(owner + ".KeyedConstructionHarmless", Assert.Single(actual.TypeArguments).ToDisplayString());

            Assert.DoesNotContain(calls, pair => pair.Operation is { TargetMethod.Name: "AddScoped", TargetMethod.TypeArguments.Length: 1 }
                && pair.Operation.TargetMethod.TypeArguments[0] is INamedTypeSymbol type
                && SymbolEqualityComparer.Default.Equals(carrier, type.OriginalDefinition)
                && Assert.Single(type.TypeArguments).ToDisplayString() == holderName);
        }
        else Assert.Contains(closed, type => Assert.Single(type.TypeArguments).ToDisplayString() == holderName);

        if (!unkeyedSafe && shape != "opaque-selected-factory")
        {
            INamedTypeSymbol first = Assert.Single(closed.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>(),
                type => Assert.Single(type.TypeArguments).ToDisplayString() == owner + ".KeyedConstructionHarmless");

            INamedTypeSymbol second = Assert.Single(closed.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>(),
                type => Assert.Single(type.TypeArguments).ToDisplayString() == holderName);

            Assert.False(SymbolEqualityComparer.Default.Equals(first, second));

            Assert.True(SymbolEqualityComparer.Default.Equals(first.OriginalDefinition, second.OriginalDefinition));
        }

        if (shape == "raw-selected-descriptor")
        {
            ObjectCreationExpressionSyntax rawSyntax = Assert.Single(syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
                creation => model.GetOperation(creation) is IObjectCreationOperation
                    { Constructor.ContainingType.Name: "ServiceDescriptor" });

            IObjectCreationOperation raw = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(rawSyntax));

            IMethodSymbol descriptorConstructor = Assert.IsAssignableFrom<IMethodSymbol>(raw.Constructor);

            Metadata(descriptorConstructor, "Microsoft.Extensions.DependencyInjection.ServiceDescriptor");

            Assert.Equal(MethodKind.Constructor, descriptorConstructor.MethodKind);

            Assert.Equal(4, descriptorConstructor.Parameters.Length);

            Assert.All(descriptorConstructor.Parameters, static parameter => Assert.Equal(RefKind.None, parameter.RefKind));

            Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName("System.Type"),
                descriptorConstructor.Parameters[0].Type));

            Assert.Equal(SpecialType.System_Object, descriptorConstructor.Parameters[1].Type.SpecialType);

            INamedTypeSymbol delegateType = Assert.IsAssignableFrom<INamedTypeSymbol>(descriptorConstructor.Parameters[2].Type);

            Assert.Equal(TypeKind.Delegate, delegateType.TypeKind);

            Assert.Equal("System.Func`3", delegateType.OriginalDefinition.MetadataName.Insert(0, "System."));

            IMethodSymbol invoke = Assert.IsAssignableFrom<IMethodSymbol>(delegateType.DelegateInvokeMethod);

            Assert.Equal(2, invoke.Parameters.Length);

            Assert.Equal("System.IServiceProvider", invoke.Parameters[0].Type.ToDisplayString());

            Assert.Equal(SpecialType.System_Object, invoke.Parameters[1].Type.SpecialType);

            Assert.Equal(SpecialType.System_Object, invoke.ReturnType.SpecialType);

            Assert.All(invoke.Parameters, static parameter => Assert.Equal(RefKind.None, parameter.RefKind));

            INamedTypeSymbol lifetime = Assert.IsAssignableFrom<INamedTypeSymbol>(descriptorConstructor.Parameters[3].Type);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceLifetime", lifetime.ToDisplayString());

            Assert.True(SymbolEqualityComparer.Default.Equals(descriptorConstructor.ContainingAssembly, lifetime.ContainingAssembly));

            Assert.Empty(lifetime.DeclaringSyntaxReferences);

            Assert.Equal(4, raw.Arguments.Length);

            ITypeOfOperation serviceType = Assert.IsAssignableFrom<ITypeOfOperation>(raw.Arguments[0].Value);

            Assert.True(SymbolEqualityComparer.Default.Equals(probe, serviceType.TypeOperand));

            IArgumentOperation rawKey = raw.Arguments[1];

            Assert.True(SymbolEqualityComparer.Default.Equals(descriptorConstructor.Parameters[1], rawKey.Parameter));

            IConversionOperation keyConversion = Assert.IsAssignableFrom<IConversionOperation>(rawKey.Value);

            Assert.True(keyConversion.IsImplicit && keyConversion.Conversion.IsReference);

            Assert.False(keyConversion.Conversion.IsUserDefined);

            Assert.Null(keyConversion.OperatorMethod);

            Assert.Equal(SpecialType.System_Object, keyConversion.Type!.SpecialType);

            ILiteralOperation keyOperand = Assert.IsAssignableFrom<ILiteralOperation>(keyConversion.Operand);

            Assert.Equal(SpecialType.System_String, keyOperand.Type!.SpecialType);

            Assert.Equal(rawSyntax.ArgumentList!.Arguments[1].Expression.Span, keyOperand.Syntax.Span);

            Assert.Equal("selected", keyOperand.ConstantValue.Value);

            Assert.Equal("selected", model.GetConstantValue(rawSyntax.ArgumentList.Arguments[1].Expression).Value);

            IFieldReferenceOperation scoped = Assert.IsAssignableFrom<IFieldReferenceOperation>(raw.Arguments[3].Value);

            Assert.Equal("Scoped", scoped.Field.Name);

            Assert.True(scoped.Field.HasConstantValue);

            Assert.True(SymbolEqualityComparer.Default.Equals(lifetime, scoped.Field.ContainingType));

            Assert.Equal(scoped.Field.ConstantValue, raw.Arguments[3].Value.ConstantValue.Value);

            AnonymousFunctionExpressionSyntax rawFactorySyntax = Assert.IsAssignableFrom<AnonymousFunctionExpressionSyntax>(
                rawSyntax.ArgumentList!.Arguments[2].Expression);

            IAnonymousFunctionOperation rawFactory = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(model.GetOperation(rawFactorySyntax));

            Assert.True(rawFactory.Symbol.IsStatic);

            Assert.Equal(2, rawFactory.Symbol.Parameters.Length);

            Assert.Equal("System.IServiceProvider", rawFactory.Symbol.Parameters[0].Type.ToDisplayString());

            Assert.Equal(SpecialType.System_Object, rawFactory.Symbol.Parameters[1].Type.SpecialType);

            InvocationExpressionSyntax rawRequiredSyntax = Assert.Single(rawFactorySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

            IInvocationOperation rawRequired = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rawRequiredSyntax));

            Metadata(rawRequired.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");

            Assert.Equal("GetRequiredService", rawRequired.TargetMethod.Name);

            Assert.True(SymbolEqualityComparer.Default.Equals(rawFactory.Symbol.Parameters[0], model.GetSymbolInfo(
                Assert.IsType<MemberAccessExpressionSyntax>(rawRequiredSyntax.Expression).Expression).Symbol));

            INamedTypeSymbol holderCarrier = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(rawRequired.TargetMethod.TypeArguments));

            Assert.True(SymbolEqualityComparer.Default.Equals(carrier, holderCarrier.OriginalDefinition));

            Assert.Equal(holderName, Assert.Single(holderCarrier.TypeArguments).ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, Assert.Single(holderCarrier.TypeArguments).ContainingAssembly.Identity);

            IMethodSymbol holderConstructor = Assert.Single(holderCarrier.InstanceConstructors,
                static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(carrierConstructor, holderConstructor.OriginalDefinition));

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(holderCarrier.TypeArguments),
                Assert.Single(holderConstructor.Parameters).Type));

            IFieldSymbol closedValue = Assert.Single(holderCarrier.GetMembers().OfType<IFieldSymbol>());

            Assert.True(closedValue.IsReadOnly && !closedValue.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(holderCarrier.TypeArguments), closedValue.Type));

            InvocationExpressionSyntax rawAddSyntax = Assert.Single(rawSyntax.Ancestors().OfType<InvocationExpressionSyntax>());

            IInvocationOperation rawAdd = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rawAddSyntax));

            IMethodSymbol add = rawAdd.TargetMethod;

            Assert.Equal("Add", add.Name);

            Assert.Equal(MethodKind.Ordinary, add.MethodKind);

            Assert.False(add.IsStatic);

            Assert.True(add.ReturnsVoid);

            Assert.Equal(SpecialType.System_Collections_Generic_ICollection_T, add.ContainingType.OriginalDefinition.SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1"),
                add.ContainingType.OriginalDefinition));

            Assert.True(SymbolEqualityComparer.Default.Equals(raw.Type, Assert.Single(add.ContainingType.TypeArguments)));

            Assert.True(SymbolEqualityComparer.Default.Equals(raw.Type, Assert.Single(add.Parameters).Type));

            Assert.Equal(RefKind.None, Assert.Single(add.Parameters).RefKind);

            Assert.Empty(add.DeclaringSyntaxReferences);

            Assert.Contains(compilation.References.OfType<PortableExecutableReference>(), reference =>
                SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(reference), add.ContainingAssembly));

            Assert.Equal(rawSyntax.Span, Assert.Single(rawAdd.Arguments).Value.Syntax.Span);

            MethodDeclarationSyntax configure = Assert.Single(rawAddSyntax.Ancestors().OfType<MethodDeclarationSyntax>());

            Assert.Equal("Configure", configure.Identifier.ValueText);

            Assert.Same(configure.Body, Assert.IsType<ExpressionStatementSyntax>(rawAddSyntax.Parent).Parent);

            IParameterSymbol services = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
                Assert.Single(configure.ParameterList.Parameters)));

            Assert.Equal("Microsoft.Extensions.DependencyInjection.IServiceCollection", services.Type.ToDisplayString());

            Assert.True(SymbolEqualityComparer.Default.Equals(services, model.GetSymbolInfo(
                Assert.IsType<MemberAccessExpressionSyntax>(rawAddSyntax.Expression).Expression).Symbol));

            var safeSelected = Assert.Single(keyed, registration => registration.Operation!.TargetMethod.Name == "AddKeyedScoped");

            Assert.Equal("selected", model.GetConstantValue(safeSelected.Call.ArgumentList.Arguments[0].Expression).Value);

            Assert.True(safeSelected.Call.Span.End < rawAddSyntax.SpanStart);

            Assert.Same(configure, Assert.Single(safeSelected.Call.Ancestors().OfType<MethodDeclarationSyntax>()));

            var retainedRegistration = Assert.Single(calls, pair => pair.Operation is { TargetMethod.Name: "AddScoped", TargetMethod.TypeArguments.Length: 1 }
                && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], holderCarrier));

            Metadata(retainedRegistration.Operation!.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");

            Assert.True(retainedRegistration.Call.Span.End < rawAddSyntax.SpanStart);

            Assert.Same(configure, Assert.Single(retainedRegistration.Call.Ancestors().OfType<MethodDeclarationSyntax>()));
        }

        INamedTypeSymbol coordinator = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("LongRestSweepCoordinator")));

        IMethodSymbol selectedConstructor = Assert.Single(coordinator.InstanceConstructors,
            static constructor => constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsStatic);

        INamedTypeSymbol coordinatorContract = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(owner + ".ILongRestSweepCoordinator"));

        var concreteRegistration = Assert.Single(calls, pair => pair.Operation is { TargetMethod.Name: "AddScoped", TargetMethod.TypeArguments.Length: 1 }
            && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], coordinator));

        var interfaceAlias = Assert.Single(calls, pair => pair.Operation is { TargetMethod.Name: "AddScoped", TargetMethod.TypeArguments.Length: 1 }
            && SymbolEqualityComparer.Default.Equals(pair.Operation.TargetMethod.TypeArguments[0], coordinatorContract));

        Metadata(concreteRegistration.Operation!.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");

        Metadata(interfaceAlias.Operation!.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");

        Assert.True(coordinator.IsSealed && !coordinator.IsGenericType);

        Assert.Equal(compilation.Assembly.Identity, coordinator.ContainingAssembly.Identity);

        Assert.Contains(coordinator.AllInterfaces, type => SymbolEqualityComparer.Default.Equals(type, coordinatorContract));

        Assert.True(concreteRegistration.Call.Span.End < interfaceAlias.Call.SpanStart);

        MethodDeclarationSyntax configuration = Assert.Single(concreteRegistration.Call.Ancestors().OfType<MethodDeclarationSyntax>());

        Assert.Equal("Configure", configuration.Identifier.ValueText);

        Assert.Same(configuration, Assert.Single(interfaceAlias.Call.Ancestors().OfType<MethodDeclarationSyntax>()));

        Assert.Same(configuration.Body, Assert.IsType<ExpressionStatementSyntax>(concreteRegistration.Call.Parent).Parent);

        Assert.Same(configuration.Body, Assert.IsType<ExpressionStatementSyntax>(interfaceAlias.Call.Parent).Parent);

        IParameterSymbol collection = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(Assert.Single(configuration.ParameterList.Parameters)));

        Assert.Equal("Microsoft.Extensions.DependencyInjection.IServiceCollection", collection.Type.ToDisplayString());

        foreach (var registration in new[] { concreteRegistration, interfaceAlias })
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(collection,
                model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(registration.Call.Expression).Expression).Symbol));

            IMethodSymbol declaration = registration.Operation!.TargetMethod.ReducedFrom ?? registration.Operation.TargetMethod;

            Assert.Equal(2, declaration.Parameters.Length);

            Assert.True(SymbolEqualityComparer.Default.Equals(collection.Type, declaration.Parameters[0].Type));

            INamedTypeSymbol factoryType = Assert.IsAssignableFrom<INamedTypeSymbol>(declaration.Parameters[1].Type);

            IMethodSymbol invoke = Assert.IsAssignableFrom<IMethodSymbol>(factoryType.DelegateInvokeMethod);

            Assert.Equal("System.IServiceProvider", Assert.Single(invoke.Parameters).Type.ToDisplayString());

            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(registration.Operation.TargetMethod.TypeArguments), invoke.ReturnType));
        }

        SimpleLambdaExpressionSyntax concreteFactory = Assert.IsType<SimpleLambdaExpressionSyntax>(
            Assert.Single(concreteRegistration.Call.ArgumentList.Arguments).Expression);

        IAnonymousFunctionOperation concreteFactoryOperation = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(model.GetOperation(concreteFactory));

        Assert.True(concreteFactoryOperation.Symbol.IsStatic);

        BaseObjectCreationExpressionSyntax concreteAllocationSyntax = Assert.IsAssignableFrom<BaseObjectCreationExpressionSyntax>(concreteFactory.Body);

        IObjectCreationOperation concreteAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(concreteAllocationSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(coordinator, concreteAllocation.Type));

        Assert.True(SymbolEqualityComparer.Default.Equals(selectedConstructor, concreteAllocation.Constructor));

        Assert.All(lookups, lookup => Assert.True(concreteAllocationSyntax.Span.Contains(lookup.Call.Span)));

        foreach (InvocationExpressionSyntax dependency in concreteAllocationSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(concreteFactoryOperation.Symbol.Parameters),
                model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(dependency.Expression).Expression).Symbol));
        }

        SimpleLambdaExpressionSyntax aliasFactory = Assert.IsType<SimpleLambdaExpressionSyntax>(
            Assert.Single(interfaceAlias.Call.ArgumentList.Arguments).Expression);

        IAnonymousFunctionOperation aliasFactoryOperation = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(model.GetOperation(aliasFactory));

        Assert.True(aliasFactoryOperation.Symbol.IsStatic);

        InvocationExpressionSyntax aliasResolutionSyntax = Assert.IsType<InvocationExpressionSyntax>(aliasFactory.Body);

        IInvocationOperation aliasResolution = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(aliasResolutionSyntax));

        Metadata(aliasResolution.TargetMethod, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");

        Assert.Equal("GetRequiredService", aliasResolution.TargetMethod.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(coordinator, Assert.Single(aliasResolution.TargetMethod.TypeArguments)));

        Assert.Empty(aliasResolutionSyntax.ArgumentList.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(aliasFactoryOperation.Symbol.Parameters),
            model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(aliasResolutionSyntax.Expression).Expression).Symbol));

        Assert.True(model.GetConversion(aliasResolutionSyntax).IsReference && !model.GetConversion(aliasResolutionSyntax).IsUserDefined);

        Assert.Null(model.GetConversion(aliasResolutionSyntax).MethodSymbol);

        foreach (string fieldName in secondSelection ? new[] { "selectedStorage", "capturedStorage" } : new[] { "selectedStorage" })
        {
            IFieldSymbol retained = Assert.Single(coordinator.GetMembers(fieldName).OfType<IFieldSymbol>());

            Assert.True(retained.IsReadOnly && !retained.IsStatic);

            VariableDeclaratorSyntax declaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(retained.DeclaringSyntaxReferences).GetSyntax());

            IParameterSymbol incoming = Assert.IsAssignableFrom<IParameterSymbol>(model.GetSymbolInfo(declaration.Initializer!.Value).Symbol);

            Assert.True(SymbolEqualityComparer.Default.Equals(selectedConstructor, incoming.ContainingSymbol));

            Assert.True(SymbolEqualityComparer.Default.Equals(retained.Type, incoming.Type));
        }

        output.WriteLine("KEYED_CONSTRUCTION_PREMISES shape=" + shape + " actualMetadataKeysFactoriesStorageClosedSubstitutionsAndPhysicalBodies=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        string Coordinate(SyntaxNode node) => "/site@" + tree.FilePath + ":" + node.SpanStart;

        InvocationExpressionSyntax acquisition = Assert.Single(Method("Worker", "RunOneAsync").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.EndsWith(Coordinate(acquisition), acquired.OperationId);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        Assert.NotNull(acquired.CapsuleId);

        HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
            && site.EnclosingType == owner + ".LongRestSweepCoordinator" && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, physical.Length);

        foreach (string member in new[] { "SendFirstAsync", "SendSecondAsync" })
        {
            MethodDeclarationSyntax body = Method("LongRestSweepCoordinator", member);

            InvocationExpressionSyntax sinkSyntax = Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            IInvocationOperation sinkOperation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(sinkSyntax));

            Assert.Empty(sinkOperation.TargetMethod.DeclaringSyntaxReferences);

            Assert.Equal("System.IO.File", sinkOperation.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Delete", sinkOperation.TargetMethod.Name);

            Assert.Contains(compilation.References.OfType<PortableExecutableReference>(), reference =>
                SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(reference), sinkOperation.TargetMethod.ContainingAssembly));

            InvocationExpressionSyntax groupSyntax = Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" });

            HostedProducerSite sink = Assert.Single(physical, site => site.Member == member);

            HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                && site.EnclosingType == owner + ".LongRestSweepCoordinator" && site.Member == member
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.EndsWith(Coordinate(sinkSyntax), sink.OperationId);

            Assert.EndsWith(Coordinate(groupSyntax), group.OperationId);

            if (unkeyedSafe)
            {
                Assert.Equal(acquired.CapsuleId, group.WorkFrontierCapsuleId);

                Assert.NotNull(group.CapsuleId);

                Assert.Equal(acquired.CapsuleId, sink.WorkFrontierCapsuleId);

                Assert.Equal(group.CapsuleId, sink.EffectFrontierCapsuleId);
            }
            else
            {
                Assert.Null(group.WorkFrontierCapsuleId);

                Assert.Null(sink.WorkFrontierCapsuleId);

                if (shape == "raw-selected-descriptor")
                {
                    Assert.Null(sink.EffectFrontierCapsuleId);

                    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                        && diagnostic.Detail == "System.IO.File.Delete"
                        && diagnostic.Identity.StartsWith(acquired.AuthorityOperationId + "/", StringComparison.Ordinal)
                        && diagnostic.Identity.EndsWith(Coordinate(sinkSyntax), StringComparison.Ordinal));
                }

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.StartsWith(acquired.AuthorityOperationId + "/", StringComparison.Ordinal)
                    && diagnostic.Identity.EndsWith(Coordinate(sinkSyntax), StringComparison.Ordinal));
            }
        }

        if (unkeyedSafe)
        {
            Assert.NotEqual(physical[0].EffectFrontierCapsuleId, physical[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
