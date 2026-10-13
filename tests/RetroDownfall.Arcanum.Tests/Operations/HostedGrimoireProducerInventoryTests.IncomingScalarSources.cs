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
    [InlineData("index", "literal")]
    [InlineData("index", "local")]
    [InlineData("int128-int32", "literal")]
    [InlineData("int128-int32", "local")]
    [InlineData("int128-int64", "literal")]
    [InlineData("int128-int64", "local")]
    [InlineData("index", "authored-conversion")]
    [InlineData("int128-int64", "authored-conversion")]
    [InlineData("index", "opaque-source")]
    [InlineData("int128-int64", "opaque-source")]
    public void ExactScalarIncomingSourceDemandRetainsPrimitiveSourcesAndExecutionBoundaries(string shape, string sourceKind)
    {
        string inputType = shape == "int128-int64" ? "long" : "int";

        string literal = shape == "int128-int64" ? "4L" : "4";

        string targetType = shape == "index" ? "System.Index" : "System.Int128";

        string initializer = shape == "index" ? "value" : "(System.Int128)value";

        string argument = sourceKind switch
        {
            "literal" => literal,
            "local" => "primitive",
            "authored-conversion" => "new ScalarArgumentProbe()",
            _ => "OpaqueScalarInput.Read()",
        };

        string body = (sourceKind == "local" ? inputType + " primitive = " + literal + "; " : "")
            + "ScalarDemandProbe.Consume(" + argument + "); await Task.CompletedTask;";

        string helper = "static class ScalarDemandProbe { public static void Consume(" + inputType + " value) { "
            + targetType + " converted = " + initializer + "; _ = converted; } }";

        if (sourceKind == "authored-conversion")
            helper += "sealed class ScalarArgumentProbe { public static implicit operator " + inputType
                + "(ScalarArgumentProbe value) { System.IO.File.Delete(\"scalar-input-effect\"); return " + literal + "; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, helper), "RetroDownfall.Arcanum.Api");

        PortableExecutableReference? opaqueReference = null;

        if (sourceKind == "opaque-source")
        {
            CSharpCompilation opaque = CompileWithProductionReferencePack("public static class OpaqueScalarInput { public static "
                + inputType + " Read() => " + literal + "; }", "RetroDownfall.Arcanum.Api").WithAssemblyName("Unreviewed.ScalarInput");

            using MemoryStream image = new();

            Microsoft.CodeAnalysis.Emit.EmitResult emitted = opaque.Emit(image);

            Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

            opaqueReference = MetadataReference.CreateFromImage(image.ToArray());

            compilation = compilation.AddReferences(opaqueReference);
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Consume");

        IMethodSymbol helperSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(declaration));

        Assert.True(helperSymbol.IsStatic && helperSymbol.ReturnsVoid);

        Assert.Equal(compilation.Assembly.Identity, helperSymbol.ContainingAssembly.Identity);

        IParameterSymbol parameter = Assert.Single(helperSymbol.Parameters);

        Assert.Equal(shape == "int128-int64" ? SpecialType.System_Int64 : SpecialType.System_Int32, parameter.Type.SpecialType);

        Assert.Equal(RefKind.None, parameter.RefKind);

        VariableDeclaratorSyntax converted = Assert.Single(declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "converted");

        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(converted.Initializer!));

        IConversionOperation frameworkConversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(frameworkConversion.OperatorMethod);

        Assert.Equal("op_Implicit", selected.Name);

        Assert.True(selected.IsStatic);

        Assert.Equal(0, selected.Arity);

        Assert.Equal(targetType, selected.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(selected.ContainingType, selected.ReturnType));

        Assert.Empty(selected.DeclaringSyntaxReferences);

        Assert.Equal(parameter.Type.SpecialType, Assert.Single(selected.Parameters).Type.SpecialType);

        Assert.Equal(RefKind.None, selected.Parameters[0].RefKind);

        Assert.Equal("System.Runtime", selected.ContainingAssembly.Identity.Name);

        Assert.Equal(new Version(10, 0, 0, 0), selected.ContainingAssembly.Identity.Version);

        Assert.Equal(string.Empty, selected.ContainingAssembly.Identity.CultureName);

        Assert.Equal("B03F5F7F11D50A3A", Convert.ToHexString(selected.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        AssertMetadataReferenceOwns(compilation, selected.ContainingAssembly);

        IParameterReferenceOperation consumed = Assert.IsAssignableFrom<IParameterReferenceOperation>(frameworkConversion.Operand);

        Assert.True(SymbolEqualityComparer.Default.Equals(parameter, consumed.Parameter));

        Assert.True(declaration.Span.Contains(frameworkConversion.Syntax.Span));

        InvocationExpressionSyntax callSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "ScalarDemandProbe.Consume");

        IInvocationOperation call = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(callSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(helperSymbol, call.TargetMethod));

        IArgumentOperation payload = Assert.Single(call.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(parameter, payload.Parameter));

        Assert.True(payload.InConversion.IsIdentity && payload.OutConversion.IsIdentity);

        InvocationExpressionSyntax? effectSyntax = null;

        IInvocationOperation? opaqueCall = null;

        if (sourceKind == "literal")
        {
            Assert.IsAssignableFrom<ILiteralOperation>(payload.Value);

            Assert.True(payload.Value.ConstantValue.HasValue);
        }
        else if (sourceKind == "local")
        {
            ILocalReferenceOperation local = Assert.IsAssignableFrom<ILocalReferenceOperation>(payload.Value);

            Assert.Equal("primitive", local.Local.Name);

            Assert.Equal(parameter.Type.SpecialType, local.Local.Type.SpecialType);

            VariableDeclaratorSyntax localDeclaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(local.Local.DeclaringSyntaxReferences).GetSyntax());

            Assert.Equal(literal, localDeclaration.Initializer!.Value.ToString());

            Assert.True(localDeclaration.Span.End < callSyntax.SpanStart);
        }
        else if (sourceKind == "authored-conversion")
        {
            IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(payload.Value);

            IMethodSymbol authored = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

            Assert.Equal(MethodKind.Conversion, authored.MethodKind);

            Assert.Equal("ScalarArgumentProbe", authored.ContainingType.Name);

            Assert.Equal(compilation.Assembly.Identity, authored.ContainingAssembly.Identity);

            Assert.Equal(parameter.Type.SpecialType, authored.ReturnType.SpecialType);

            Assert.IsAssignableFrom<IObjectCreationOperation>(conversion.Operand);

            effectSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

            Assert.True(Assert.Single(authored.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));

            IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

            Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Delete", effect.TargetMethod.Name);

            Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

            Assert.Equal(SpecialType.System_String, Assert.Single(effect.TargetMethod.Parameters).Type.SpecialType);

            AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);
        }
        else
        {
            opaqueCall = Assert.IsAssignableFrom<IInvocationOperation>(payload.Value);

            Assert.Equal("OpaqueScalarInput", opaqueCall.TargetMethod.ContainingType.Name);

            Assert.Equal("Read", opaqueCall.TargetMethod.Name);

            Assert.Equal("Unreviewed.ScalarInput", opaqueCall.TargetMethod.ContainingAssembly.Identity.Name);

            Assert.Empty(opaqueCall.TargetMethod.DeclaringSyntaxReferences);

            Assert.Empty(opaqueCall.Arguments);

            Assert.Equal(parameter.Type.SpecialType, opaqueCall.TargetMethod.ReturnType.SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(opaqueReference!),
                opaqueCall.TargetMethod.ContainingAssembly));
        }

        output.WriteLine("INCOMING_SCALAR_PREMISES shape=" + shape + " source=" + sourceKind + " exactFrameworkConversionFormalAndArgument=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (sourceKind is "literal" or "local")
        {
            Assert.Empty(result.Items);

            Assert.Empty(result.Diagnostics);

            return;
        }

        string conversionCoordinate = tree.FilePath + ":" + frameworkConversion.Syntax.SpanStart;

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == (shape == "index" ? "System.Index.op_Implicit" : "System.Int128.op_Implicit")
            && diagnostic.Identity == conversionCoordinate);

        if (effectSyntax is not null)
        {
            string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

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
        else
        {
            string coordinate = tree.FilePath + ":" + opaqueCall!.Syntax.SpanStart;

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "OpaqueScalarInput.Read" && diagnostic.Identity == coordinate);
        }
    }
}
