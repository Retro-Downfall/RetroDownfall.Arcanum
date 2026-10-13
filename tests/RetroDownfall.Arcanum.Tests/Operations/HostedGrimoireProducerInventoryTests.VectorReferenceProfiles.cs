using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualEmbeddingNormRetainsItsClosedFloatVectorAnalysisAcrossReferenceProfiles(bool productionReferencePack)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src/RetroDownfall.Arcanum.Core/Primitives/EmbeddingBlobCodec.cs");

        string method = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "NormSquared").ToFullString();

        string source = "using System.Numerics; " + FixtureSource("_ = NumericSlice.NormSquared(new float[33]);", "static class NumericSlice { " + method + " }");

        string profile = productionReferencePack ? "production-reference-pack" : "runtime";

        CSharpCompilation compilation = productionReferencePack
            ? CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            : Compile(source);

        AssertFloatVectorNormProfile(profile, compilation);
    }

    private void AssertFloatVectorNormProfile(string profile, CSharpCompilation compilation)
    {
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MemberAccessExpressionSyntax countAccess = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            static access => access.Name.Identifier.ValueText == "Count");

        IPropertySymbol count = Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(countAccess).Symbol);

        Assert.True(count.IsStatic);

        Assert.Equal(SpecialType.System_Int32, count.Type.SpecialType);

        Assert.Null(count.SetMethod);

        IMethodSymbol getter = Assert.IsAssignableFrom<IMethodSymbol>(count.GetMethod);

        Assert.True(getter.IsStatic);

        Assert.Empty(getter.Parameters);

        Assert.Equal(SpecialType.System_Int32, getter.ReturnType.SpecialType);

        Assert.Equal("Vector`1", count.ContainingType.OriginalDefinition.MetadataName);

        Assert.Equal("System.Numerics", count.ContainingType.ContainingNamespace.ToDisplayString());

        AssertFloatTypeArgument(count.ContainingType);

        ImplicitObjectCreationExpressionSyntax construction = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>());

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(construction));

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(creation.Constructor);

        Assert.Equal(MethodKind.Constructor, constructor.MethodKind);

        Assert.Equal("Vector`1", constructor.ContainingType.OriginalDefinition.MetadataName);

        Assert.Equal("System.Numerics", constructor.ContainingType.ContainingNamespace.ToDisplayString());

        AssertFloatTypeArgument(constructor.ContainingType);

        IParameterSymbol spanParameter = Assert.Single(constructor.Parameters);

        Assert.Equal(RefKind.None, spanParameter.RefKind);

        Assert.Equal("ReadOnlySpan`1", Assert.IsAssignableFrom<INamedTypeSymbol>(spanParameter.Type).OriginalDefinition.MetadataName);

        Assert.Equal("System", Assert.IsAssignableFrom<INamedTypeSymbol>(spanParameter.Type).ContainingNamespace.ToDisplayString());

        AssertFloatTypeArgument(Assert.IsAssignableFrom<INamedTypeSymbol>(spanParameter.Type));

        InvocationExpressionSyntax dotInvocation = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Dot");

        IMethodSymbol dot = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(dotInvocation).Symbol);

        Assert.Equal("Vector", dot.ContainingType.MetadataName);

        Assert.Equal("System.Numerics", dot.ContainingType.ContainingNamespace.ToDisplayString());

        Assert.True(dot.IsStatic);

        Assert.Equal("Dot", dot.Name);

        Assert.Equal(SpecialType.System_Single, dot.ReturnType.SpecialType);

        Assert.Equal(SpecialType.System_Single, Assert.Single(dot.TypeArguments).SpecialType);

        Assert.Equal(2, dot.Parameters.Length);

        Assert.All(dot.Parameters, parameter =>
        {
            Assert.Equal(RefKind.None, parameter.RefKind);

            Assert.Equal("Vector`1", Assert.IsAssignableFrom<INamedTypeSymbol>(parameter.Type).OriginalDefinition.MetadataName);

            Assert.Equal("System.Numerics", Assert.IsAssignableFrom<INamedTypeSymbol>(parameter.Type).ContainingNamespace.ToDisplayString());

            AssertFloatTypeArgument(Assert.IsAssignableFrom<INamedTypeSymbol>(parameter.Type));
        });

        PortableExecutableReference countReference = AssertMetadataReferenceOwns(compilation, count.ContainingAssembly);

        AssertMetadataReferenceOwns(compilation, getter.ContainingAssembly);

        PortableExecutableReference constructorReference = AssertMetadataReferenceOwns(compilation, constructor.ContainingAssembly);

        PortableExecutableReference dotReference = AssertMetadataReferenceOwns(compilation, dot.ContainingAssembly);

        Assert.False(count.Locations.Any(static location => location.IsInSource));

        Assert.False(getter.Locations.Any(static location => location.IsInSource));

        Assert.False(constructor.Locations.Any(static location => location.IsInSource));

        Assert.False(dot.Locations.Any(static location => location.IsInSource));

        string vectorIdentity = count.ContainingAssembly.Identity.ToString();

        Assert.Equal(vectorIdentity, constructor.ContainingAssembly.Identity.ToString());

        Assert.Equal(vectorIdentity, dot.ContainingAssembly.Identity.ToString());

        output.WriteLine($"R314_VECTOR_PROFILE={profile}|vectorAssembly={vectorIdentity}|count={count.ToDisplayString()}|countAssembly={count.ContainingAssembly.Identity}|countReference={countReference.FilePath}|getter={getter.ToDisplayString()}|getterAssembly={getter.ContainingAssembly.Identity}|constructor={constructor.ToDisplayString()}|constructorAssembly={constructor.ContainingAssembly.Identity}|constructorReference={constructorReference.FilePath}|dot={dot.ToDisplayString()}|dotAssembly={dot.ContainingAssembly.Identity}|dotReference={dotReference.FilePath}");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]),
            [],
            []);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("known-array", true)]
    [InlineData("counterfeit", false)]
    [InlineData("span-conversion", false)]
    [InlineData("vector-conversion", false)]
    public void ApiReferenceVectorProfilesRetainActualAuthoredEffects(string shape, bool reviewed)
    {
        string body = shape switch
        {
            "known-array" => "_ = new System.Numerics.Vector<float>((ReadOnlySpan<float>)new float[32]);",
            "counterfeit" => "_ = System.Numerics.Vector<float>.Count;",
            "span-conversion" => "_ = new System.Numerics.Vector<float>(new SpanProbe());",
            _ => "_ = System.Numerics.Vector.Dot<float>(new VectorProbe(), default);",
        };

        string helper = shape switch
        {
            "counterfeit" => "namespace System.Numerics { public struct Vector<T> { public static int Count { get { System.IO.File.Delete(\"counterfeit\"); return 1; } } } }",
            "span-conversion" => "sealed class SpanProbe { public static implicit operator ReadOnlySpan<float>(SpanProbe value) { System.IO.File.Delete(\"span-conversion\"); return new float[32]; } }",
            "vector-conversion" => "sealed class VectorProbe { public static implicit operator System.Numerics.Vector<float>(VectorProbe value) { System.IO.File.Delete(\"vector-conversion\"); return default; } }",
            _ => "",
        };

        string source = R2Source(body + " await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        IMethodSymbol? selectedConversion = null;

        IMethodSymbol? counterfeitGetter = null;

        if (shape == "counterfeit")
        {
            MemberAccessExpressionSyntax countAccess = Assert.Single(syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                static access => access.Name.Identifier.ValueText == "Count");

            var read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(countAccess));

            Assert.Equal("System.Numerics.Vector<float>", read.Property.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, read.Property.ContainingAssembly.Identity);

            Assert.NotEmpty(read.Property.DeclaringSyntaxReferences);

            Assert.NotEmpty(read.Property.GetMethod!.DeclaringSyntaxReferences);

            counterfeitGetter = read.Property.GetMethod;
        }
        else
        {
            IOperation consumer;

            IMethodSymbol selected;

            IArgumentOperation payload;

            if (shape == "vector-conversion")
            {
                InvocationExpressionSyntax dotSyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    static invocation => invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Dot");

                var invocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(dotSyntax));

                consumer = invocation;

                selected = invocation.TargetMethod;

                payload = invocation.Arguments[0];

                Assert.True(selected.IsStatic);

                Assert.Equal("Dot", selected.Name);

                Assert.Equal(SpecialType.System_Single, Assert.Single(selected.TypeArguments).SpecialType);

                Assert.Equal(SpecialType.System_Single, selected.ReturnType.SpecialType);

                Assert.Equal(2, selected.Parameters.Length);

                Assert.All(selected.Parameters, parameter =>
                {
                    Assert.Equal(RefKind.None, parameter.RefKind);

                    AssertFloatTypeArgument(Assert.IsAssignableFrom<INamedTypeSymbol>(parameter.Type));
                });
            }
            else
            {
                ObjectCreationExpressionSyntax vectorSyntax = Assert.Single(syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
                    creation => model.GetOperation(creation) is IObjectCreationOperation operation
                        && operation.Type?.ToDisplayString() == "System.Numerics.Vector<float>");

                var creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(vectorSyntax));

                consumer = creation;

                selected = Assert.IsAssignableFrom<IMethodSymbol>(creation.Constructor);

                payload = Assert.Single(creation.Arguments);

                Assert.Equal(MethodKind.Constructor, selected.MethodKind);

                IParameterSymbol parameter = Assert.Single(selected.Parameters);

                Assert.Equal(RefKind.None, parameter.RefKind);

                Assert.Equal("System.ReadOnlySpan<float>", parameter.Type.ToDisplayString());
            }

            Assert.Equal("System.Numerics.Vectors", selected.ContainingAssembly.Identity.Name);

            Assert.Equal(new Version(10, 0, 0, 0), selected.ContainingAssembly.Identity.Version);

            Assert.Equal("B03F5F7F11D50A3A", Convert.ToHexString(selected.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

            Assert.Empty(selected.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, selected.ContainingAssembly);

            Assert.Equal(ArgumentKind.Explicit, payload.ArgumentKind);

            Assert.Equal(RefKind.None, payload.Parameter!.RefKind);

            Assert.True(payload.InConversion.IsIdentity && payload.OutConversion.IsIdentity);

            if (reviewed)
            {
                Assert.Contains(consumer.DescendantsAndSelf(), static operation => operation is IArrayCreationOperation
                    { Type: IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Single } });

                Assert.DoesNotContain(consumer.DescendantsAndSelf().OfType<IConversionOperation>(),
                    conversion => conversion.OperatorMethod?.DeclaringSyntaxReferences.Length > 0);
            }
            else
            {
                var conversion = Assert.IsAssignableFrom<IConversionOperation>(payload.Value);

                selectedConversion = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

                Assert.Equal(shape == "span-conversion" ? "SpanProbe" : "VectorProbe", selectedConversion.ContainingType.Name);

                Assert.Equal(compilation.Assembly.Identity, selectedConversion.ContainingAssembly.Identity);

                Assert.NotEmpty(selectedConversion.DeclaringSyntaxReferences);

                Assert.Equal(shape == "span-conversion" ? "System.ReadOnlySpan<float>" : "System.Numerics.Vector<float>",
                    selectedConversion.ReturnType.ToDisplayString());

                Assert.IsAssignableFrom<IObjectCreationOperation>(conversion.Operand);
            }
        }

        InvocationExpressionSyntax? effectSyntax = reviewed ? null : Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        if (effectSyntax is not null)
        {
            var effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

            Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Delete", effect.TargetMethod.Name);

            Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

            Assert.Equal(SpecialType.System_String, Assert.Single(effect.TargetMethod.Parameters).Type.SpecialType);

            if (selectedConversion is not null)
                Assert.True(Assert.Single(selectedConversion.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));

            if (counterfeitGetter is not null)
                Assert.True(Assert.Single(counterfeitGetter.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));
        }

        output.WriteLine("API_VECTOR_CONTROL_PREMISES shape=" + shape + " selectedReferenceMemberAndAuthoredEffect=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (reviewed)
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

    private static void AssertFloatTypeArgument(INamedTypeSymbol type)
    {
        Assert.Collection(type.TypeArguments, argument => Assert.Equal(SpecialType.System_Single, argument.SpecialType));
    }

    private static PortableExecutableReference AssertMetadataReferenceOwns(CSharpCompilation compilation, IAssemblySymbol assembly)
    {
        MetadataReference reference = Assert.Single(compilation.References,
            candidate => SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(candidate), assembly));

        PortableExecutableReference portable = Assert.IsAssignableFrom<PortableExecutableReference>(reference);

        Assert.NotNull(portable.FilePath);

        return portable;
    }
}
