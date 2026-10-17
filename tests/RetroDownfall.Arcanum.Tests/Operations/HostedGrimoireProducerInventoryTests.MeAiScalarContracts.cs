using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void ExactMeAiScalarStorageAndValidationMembersHaveNoExtraSites()
    {
        const string body = """
            Microsoft.Extensions.AI.ChatOptions chat = null!;
            chat.ModelId = "model";
            _ = chat.ModelId;
            Microsoft.Extensions.AI.ChatResponse response = null!;
            _ = response.Usage;
            _ = response.Messages;
            var embeddingOptions = new Microsoft.Extensions.AI.EmbeddingGenerationOptions();
            embeddingOptions.ModelId = "model";
            _ = embeddingOptions.ModelId;
            embeddingOptions.Dimensions = 32;
            _ = embeddingOptions.Dimensions;
            Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>> generated = null!;
            _ = generated.Usage;
            """;

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        IPropertySymbol chatModel = Property(model, tree, "Microsoft.Extensions.AI.ChatOptions", "ModelId");

        AssertMetadataSdkProperty(compilation, chatModel, typeof(Microsoft.Extensions.AI.ChatOptions), hasGetter: true, hasSetter: true);

        Assert.Equal(SpecialType.System_String, chatModel.Type.SpecialType);

        Assert.Equal(2, PropertyAccesses(model, tree, "Microsoft.Extensions.AI.ChatOptions", "ModelId").Length);

        IPropertySymbol responseUsage = Property(model, tree, "Microsoft.Extensions.AI.ChatResponse", "Usage");

        AssertMetadataSdkProperty(compilation, responseUsage, typeof(Microsoft.Extensions.AI.ChatResponse), hasGetter: true, hasSetter: true);

        AssertPropertyRead(model, tree, "response.Usage", responseUsage);

        IPropertySymbol responseMessages = Property(model, tree, "Microsoft.Extensions.AI.ChatResponse", "Messages");

        AssertMetadataSdkProperty(compilation, responseMessages, typeof(Microsoft.Extensions.AI.ChatResponse), hasGetter: true, hasSetter: true);

        AssertPropertyRead(model, tree, "response.Messages", responseMessages);

        Assert.IsAssignableFrom<INamedTypeSymbol>(responseMessages.Type);

        Assert.Equal("IList`1", Assert.IsAssignableFrom<INamedTypeSymbol>(responseMessages.Type).OriginalDefinition.MetadataName);

        Assert.Equal("ChatMessage", Assert.IsAssignableFrom<INamedTypeSymbol>(responseMessages.Type).TypeArguments[0].Name);

        ObjectCreationExpressionSyntax optionsCreationSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            static creation => creation.Type.ToString() == "Microsoft.Extensions.AI.EmbeddingGenerationOptions");

        IObjectCreationOperation optionsCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(optionsCreationSyntax));

        IMethodSymbol optionsConstructor = Assert.IsAssignableFrom<IMethodSymbol>(optionsCreation.Constructor);

        Assert.Equal(MethodKind.Constructor, optionsConstructor.MethodKind);

        Assert.Empty(optionsConstructor.Parameters);

        AssertMetadataSdkMember(compilation, optionsConstructor, typeof(Microsoft.Extensions.AI.EmbeddingGenerationOptions));

        IPropertySymbol embeddingModel = Property(model, tree, "Microsoft.Extensions.AI.EmbeddingGenerationOptions", "ModelId");

        AssertMetadataSdkProperty(compilation, embeddingModel, typeof(Microsoft.Extensions.AI.EmbeddingGenerationOptions), hasGetter: true, hasSetter: true);

        Assert.Equal(SpecialType.System_String, embeddingModel.Type.SpecialType);

        Assert.Equal(2, PropertyAccesses(model, tree, "Microsoft.Extensions.AI.EmbeddingGenerationOptions", "ModelId").Length);

        IPropertySymbol dimensions = Property(model, tree, "Microsoft.Extensions.AI.EmbeddingGenerationOptions", "Dimensions");

        AssertMetadataSdkProperty(compilation, dimensions, typeof(Microsoft.Extensions.AI.EmbeddingGenerationOptions), hasGetter: true, hasSetter: true);

        Assert.True(dimensions.Type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            TypeArguments: [{ SpecialType: SpecialType.System_Int32 }],
        });

        IParameterSymbol dimensionsParameter = Assert.Single(dimensions.SetMethod!.Parameters);

        Assert.Equal(RefKind.None, dimensionsParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(dimensions.Type, dimensionsParameter.Type));

        Assert.Equal(2, PropertyAccesses(model, tree, "Microsoft.Extensions.AI.EmbeddingGenerationOptions", "Dimensions").Length);

        IPropertySymbol generatedUsage = Property(model, tree,
            "Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>", "Usage");

        AssertMetadataSdkProperty(compilation, generatedUsage, typeof(Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>), hasGetter: true, hasSetter: true);

        AssertPropertyRead(model, tree, "generated.Usage", generatedUsage);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);

        output.WriteLine("MEAI_SCALAR_CONTRACTS_PRODUCTION_REFERENCE=" + string.Join("|",
            chatModel.ContainingAssembly.Identity,
            responseUsage.ContainingAssembly.Identity,
            responseMessages.ContainingAssembly.Identity,
            optionsConstructor.ContainingAssembly.Identity,
            embeddingModel.ContainingAssembly.Identity,
            dimensions.ContainingAssembly.Identity,
            generatedUsage.ContainingAssembly.Identity));
    }

    [Theory]
    [InlineData("authored-conversion")]
    [InlineData("authored-getter")]
    public void MeAiScalarStorageDoesNotHideAuthoredEffects(string shape)
    {
        string body = shape == "authored-conversion"
            ? "Microsoft.Extensions.AI.ChatOptions options = null!; options.ModelId = new ModelIdProbe();"
            : "_ = new ResponseGetterProbe().Value.Usage;";

        string helpers = shape == "authored-conversion"
            ? "sealed class ModelIdProbe { public static implicit operator string(ModelIdProbe value) { System.IO.File.Delete(\"model-id-conversion\"); return \"model\"; } }"
            : "sealed class ResponseGetterProbe { public Microsoft.Extensions.AI.ChatResponse Value { get { System.IO.File.Delete(\"response-usage-getter\"); return null!; } } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, helpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax effectSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        IInvocationOperation effectOperation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effectOperation.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effectOperation.TargetMethod.Name);

        Assert.Empty(effectOperation.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, effectOperation.TargetMethod.ContainingAssembly);

        IPropertySymbol selectedSdkProperty;

        if (shape == "authored-conversion")
        {
            AssignmentExpressionSyntax assignment = Assert.Single(tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                static candidate => candidate.Left.ToString() == "options.ModelId");

            selectedSdkProperty = Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(assignment.Left).Symbol);

            ISimpleAssignmentOperation assignmentOperation = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(assignment));

            IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(assignmentOperation.Value);

            IMethodSymbol selectedConversion = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

            Assert.Equal(MethodKind.Conversion, selectedConversion.MethodKind);

            Assert.Equal("ModelIdProbe", selectedConversion.ContainingType.Name);

            Assert.Equal(compilation.Assembly.Identity, selectedConversion.ContainingAssembly.Identity);

            Assert.Equal(SpecialType.System_String, selectedConversion.ReturnType.SpecialType);

            Assert.Single(selectedConversion.Parameters, static parameter => parameter.Type.Name == "ModelIdProbe");

            Assert.True(Assert.Single(selectedConversion.DeclaringSyntaxReferences).GetSyntax().Span.Contains(effectSyntax.Span));

            IPropertyReferenceOperation assignmentTarget = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(assignment.Left));

            Assert.True(SymbolEqualityComparer.Default.Equals(selectedSdkProperty, assignmentTarget.Property));
        }
        else
        {
            MemberAccessExpressionSyntax usageAccess = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                static access => access.Name.Identifier.ValueText == "Usage");

            selectedSdkProperty = Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(usageAccess).Symbol);

            IPropertySymbol authoredGetterProperty = Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(
                Assert.Single(tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                    static access => access.Name.Identifier.ValueText == "Value")).Symbol);

            Assert.Equal("ResponseGetterProbe", authoredGetterProperty.ContainingType.Name);

            Assert.Equal(compilation.Assembly.Identity, authoredGetterProperty.ContainingAssembly.Identity);

            Assert.True(Assert.Single(authoredGetterProperty.GetMethod!.DeclaringSyntaxReferences).GetSyntax().Span.Contains(effectSyntax.Span));

            IPropertyReferenceOperation usageReference = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(usageAccess));

            Assert.True(SymbolEqualityComparer.Default.Equals(selectedSdkProperty, usageReference.Property));

            Assert.NotNull(usageReference.Property.GetMethod);
        }

        Assert.Equal(shape == "authored-conversion"
            ? "Microsoft.Extensions.AI.ChatOptions"
            : "Microsoft.Extensions.AI.ChatResponse", selectedSdkProperty.ContainingType.ToDisplayString());

        AssertMetadataSdkProperty(compilation, selectedSdkProperty, shape == "authored-conversion"
            ? typeof(Microsoft.Extensions.AI.ChatOptions)
            : typeof(Microsoft.Extensions.AI.ChatResponse), hasGetter: true, hasSetter: true);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        HostedProducerSite physical = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete");

        Assert.EndsWith(coordinate, physical.OperationId);

        Assert.Null(physical.WorkFrontierCapsuleId);

        Assert.Null(physical.EffectFrontierCapsuleId);

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.Extensions.AI.", StringComparison.Ordinal));
    }

    private static IPropertySymbol Property(SemanticModel model, SyntaxTree tree, string containingType, string name)
    {
        IPropertySymbol[] properties = PropertyAccesses(model, tree, containingType, name);

        Assert.NotEmpty(properties);

        Assert.All(properties, property => Assert.True(SymbolEqualityComparer.Default.Equals(properties[0], property)));

        return properties[0];
    }

    private static IPropertySymbol[] PropertyAccesses(SemanticModel model, SyntaxTree tree, string containingType, string name) =>
        tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Select(access => model.GetSymbolInfo(access).Symbol)
            .OfType<IPropertySymbol>()
            .Where(property => property.Name == name && property.ContainingType.ToDisplayString() == containingType)
            .ToArray();

    private static IPropertyReferenceOperation AssertPropertyRead(
        SemanticModel model,
        SyntaxTree tree,
        string expression,
        IPropertySymbol expectedProperty)
    {
        MemberAccessExpressionSyntax access = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            candidate => candidate.ToString() == expression);

        IPropertyReferenceOperation reference = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(access));

        Assert.True(SymbolEqualityComparer.Default.Equals(expectedProperty, reference.Property));

        Assert.NotNull(reference.Property.GetMethod);

        return reference;
    }

    private static void AssertMetadataSdkProperty(
        CSharpCompilation compilation,
        IPropertySymbol property,
        Type trustedDeclarationType,
        bool hasGetter,
        bool hasSetter)
    {
        AssertMetadataSdkMember(compilation, property, trustedDeclarationType);

        Assert.Equal(hasGetter, property.GetMethod is not null);

        Assert.Equal(hasSetter, property.SetMethod is not null);

        foreach (IMethodSymbol accessor in new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>())
        {
            Assert.False(accessor.IsVirtual || accessor.IsAbstract || accessor.IsOverride);

            Assert.Empty(accessor.DeclaringSyntaxReferences);
        }
    }

    private static void AssertMetadataSdkMember(CSharpCompilation compilation, ISymbol member, Type trustedDeclarationType)
    {
        System.Reflection.AssemblyName trustedIdentity = trustedDeclarationType.Assembly.GetName();

        Assert.Equal(trustedIdentity.Name, member.ContainingAssembly.Identity.Name);

        Assert.Equal(trustedIdentity.Version, member.ContainingAssembly.Identity.Version);

        Assert.Equal(trustedIdentity.CultureName ?? string.Empty, member.ContainingAssembly.Identity.CultureName);

        Assert.Equal(trustedIdentity.GetPublicKeyToken() ?? [], member.ContainingAssembly.Identity.PublicKeyToken.ToArray());

        AssertMetadataReferenceOwns(compilation, member.ContainingAssembly);

        Assert.Empty(member.DeclaringSyntaxReferences);
    }
}
