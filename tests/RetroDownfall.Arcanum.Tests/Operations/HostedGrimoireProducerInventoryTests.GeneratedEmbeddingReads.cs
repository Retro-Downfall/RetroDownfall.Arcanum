using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("count", true)]
    [InlineData("index", true)]
    [InlineData("index-conversion", false)]
    [InlineData("counterfeit-count", false)]
    [InlineData("counterfeit-index", false)]
    public void ApiGeneratedEmbeddingReadsRetainExactSelectedGetterAndIndexEffects(string shape, bool pure)
    {
        bool count = shape is "count" or "counterfeit-count";

        bool counterfeit = shape.StartsWith("counterfeit-", StringComparison.Ordinal);

        string read = count ? "result.Count" : shape == "index-conversion" ? "result[new IndexProbe()]" : "result[0]";

        string helper = shape switch
        {
            "index-conversion" => "sealed class IndexProbe { public static implicit operator int(IndexProbe value) { System.IO.File.Delete(\"index-conversion\"); return 0; } }",
            "counterfeit-count" => "namespace Microsoft.Extensions.AI { public sealed class GeneratedEmbeddings<T> { public int Count { get { System.IO.File.Delete(\"counterfeit-count\"); return 1; } } } }",
            "counterfeit-index" => "namespace Microsoft.Extensions.AI { public sealed class GeneratedEmbeddings<T> { public T this[int index] { get { System.IO.File.Delete(\"counterfeit-index\"); return default!; } } } }",
            _ => "",
        };

        string value = counterfeit ? "new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>()" : "null!";

        string source = R2Source("Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>> result = "
            + value + "; _ = " + read + "; await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ExpressionSyntax readSyntax = count
            ? Assert.Single(syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                static access => access.Expression is IdentifierNameSyntax { Identifier.ValueText: "result" })
            : Assert.Single(syntax.DescendantNodes().OfType<ElementAccessExpressionSyntax>());

        var selectedRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(readSyntax));

        IPropertySymbol property = selectedRead.Property;

        IMethodSymbol getter = Assert.IsAssignableFrom<IMethodSymbol>(property.GetMethod);

        INamedTypeSymbol resultType = property.ContainingType;

        Assert.Equal("Microsoft.Extensions.AI", resultType.ContainingNamespace.ToDisplayString());

        Assert.Equal("GeneratedEmbeddings`1", resultType.OriginalDefinition.MetadataName);

        Assert.True(resultType.IsSealed);

        Assert.Equal(MethodKind.PropertyGet, getter.MethodKind);

        Assert.False(getter.ReturnsByRef || getter.ReturnsByRefReadonly);

        Assert.True(SymbolEqualityComparer.Default.Equals(property.Type, getter.ReturnType));

        Assert.False(property.IsStatic || getter.IsStatic || getter.IsVirtual || getter.IsOverride || getter.IsAbstract);

        INamedTypeSymbol embedding = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(resultType.TypeArguments));

        Assert.Equal("Microsoft.Extensions.AI", embedding.ContainingNamespace.ToDisplayString());

        Assert.Equal("Embedding`1", embedding.OriginalDefinition.MetadataName);

        Assert.Equal(SpecialType.System_Single, Assert.Single(embedding.TypeArguments).SpecialType);

        Assert.Empty(embedding.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, embedding.ContainingAssembly);

        var actualReceiver = Assert.IsAssignableFrom<ILocalReferenceOperation>(selectedRead.Instance);

        Assert.Equal("result", actualReceiver.Local.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(resultType, actualReceiver.Local.Type));

        if (count)
        {
            Assert.Equal("Count", property.Name);

            Assert.Equal("get_Count", getter.MetadataName);

            Assert.False(property.IsIndexer);

            Assert.Equal(SpecialType.System_Int32, property.Type.SpecialType);

            Assert.Empty(property.Parameters);

            Assert.Empty(getter.Parameters);

            Assert.Null(property.SetMethod);
        }
        else
        {
            Assert.True(property.IsIndexer);

            Assert.Equal("get_Item", getter.MetadataName);

            IParameterSymbol propertyIndex = Assert.Single(property.Parameters);

            Assert.Equal(RefKind.None, propertyIndex.RefKind);

            Assert.Equal(SpecialType.System_Int32, propertyIndex.Type.SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(embedding, property.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(embedding, getter.ReturnType));

            IParameterSymbol index = Assert.Single(getter.Parameters);

            Assert.Equal(RefKind.None, index.RefKind);

            Assert.Equal(SpecialType.System_Int32, index.Type.SpecialType);

            IArgumentOperation argument = Assert.Single(selectedRead.Arguments);

            Assert.Equal(ArgumentKind.Explicit, argument.ArgumentKind);

            Assert.Equal(RefKind.None, argument.Parameter!.RefKind);

            Assert.True(argument.InConversion.IsIdentity && argument.OutConversion.IsIdentity);

            if (shape == "index-conversion")
            {
                var conversion = Assert.IsAssignableFrom<IConversionOperation>(argument.Value);

                IMethodSymbol authored = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

                Assert.True(conversion.Conversion.IsUserDefined);

                Assert.Equal(MethodKind.Conversion, authored.MethodKind);

                Assert.True(authored.IsStatic);

                Assert.Equal("op_Implicit", authored.MetadataName);

                IParameterSymbol conversionInput = Assert.Single(authored.Parameters);

                Assert.Equal(RefKind.None, conversionInput.RefKind);

                Assert.True(SymbolEqualityComparer.Default.Equals(authored.ContainingType, conversionInput.Type));

                var conversionOperand = Assert.IsAssignableFrom<IObjectCreationOperation>(conversion.Operand);

                Assert.True(SymbolEqualityComparer.Default.Equals(conversionInput.Type, conversionOperand.Type));

                Assert.Equal("IndexProbe", authored.ContainingType.Name);

                Assert.Equal(SpecialType.System_Int32, authored.ReturnType.SpecialType);

                Assert.Equal(compilation.Assembly.Identity, authored.ContainingAssembly.Identity);

                Assert.NotEmpty(authored.DeclaringSyntaxReferences);
            }
            else Assert.Equal(0, Assert.IsAssignableFrom<ILiteralOperation>(argument.Value).ConstantValue.Value);
        }

        if (counterfeit)
        {
            VariableDeclaratorSyntax local = Assert.IsType<VariableDeclaratorSyntax>(
                Assert.Single(actualReceiver.Local.DeclaringSyntaxReferences).GetSyntax());

            var construction = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(local.Initializer!.Value));

            Assert.True(SymbolEqualityComparer.Default.Equals(resultType, construction.Type));

            Assert.Equal(compilation.Assembly.Identity, construction.Constructor!.ContainingAssembly.Identity);

            Assert.Equal(compilation.Assembly.Identity, getter.ContainingAssembly.Identity);

            Assert.NotEmpty(property.DeclaringSyntaxReferences);

            Assert.NotEmpty(getter.DeclaringSyntaxReferences);
        }
        else
        {
            System.Reflection.AssemblyName expected = typeof(Microsoft.Extensions.AI.GeneratedEmbeddings<>).Assembly.GetName();

            Assert.Equal("Microsoft.Extensions.AI.Abstractions", getter.ContainingAssembly.Identity.Name);

            Assert.Equal(expected.Name, getter.ContainingAssembly.Identity.Name);

            Assert.Equal(expected.Version, getter.ContainingAssembly.Identity.Version);

            Assert.Equal(expected.CultureName ?? string.Empty, getter.ContainingAssembly.Identity.CultureName);

            Assert.Equal(Convert.ToHexString(expected.GetPublicKeyToken()!),
                Convert.ToHexString(getter.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

            Assert.Equal(embedding.ContainingAssembly.Identity, getter.ContainingAssembly.Identity);

            Assert.Empty(property.DeclaringSyntaxReferences);

            Assert.Empty(getter.DeclaringSyntaxReferences);

            PortableExecutableReference reference = AssertMetadataReferenceOwns(compilation, getter.ContainingAssembly);

            output.WriteLine("GENERATED_EMBEDDING_READ_REFERENCE shape=" + shape + " identity=" + getter.ContainingAssembly.Identity
                + " member=" + getter.ToDisplayString() + " reference=" + reference.FilePath);
        }

        InvocationExpressionSyntax? effectSyntax = pure ? null : Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        if (effectSyntax is not null)
        {
            var effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

            Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

            IMethodSymbol owner = counterfeit ? getter : Assert.IsAssignableFrom<IMethodSymbol>(
                model.GetDeclaredSymbol(Assert.Single(syntax.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>())));

            Assert.True(Assert.Single(owner.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));
        }

        output.WriteLine("GENERATED_EMBEDDING_READ_PREMISES shape=" + shape + " selectedMemberAndEffect=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (pure)
        {
            Assert.Empty(result.Items);

            Assert.Empty(result.Diagnostics);
        }
        else
        {
            string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax!.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
                && site.Callee == "System.IO.File.Delete").ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.EndsWith(coordinate, site.OperationId);

                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
        }
    }
}
