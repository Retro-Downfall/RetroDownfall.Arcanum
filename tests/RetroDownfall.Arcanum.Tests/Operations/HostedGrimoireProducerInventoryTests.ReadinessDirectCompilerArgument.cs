using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("built-in", true)]
    [InlineData("authored-replacement", false)]
    [InlineData("optional-default", false)]
    public void Readiness_direct_handler_argument_retains_actual_compiler_source(string shape, bool preserved)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        const string original = "new PerUnitRecoveryHandler(workAdmission!)";

        Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

        string replacement = shape switch
        {
            "built-in" => "new PerUnitRecoveryHandler((RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission)workAdmission!)",
            "authored-replacement" => "new PerUnitRecoveryHandler((RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission)(RetroDownfall.Arcanum.Infrastructure.Data.LongRest.DirectHolderReplacement)workAdmission!)",
            "optional-default" => "new PerUnitRecoveryHandler()",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        source = source.Replace(original, replacement, StringComparison.Ordinal);

        if (shape == "authored-replacement")
        {
            source += """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal sealed class DirectHolderReplacement
                    {
                        public static implicit operator DirectHolderReplacement(LongRestWorkAdmission source) => new();

                        public static explicit operator LongRestWorkAdmission(DirectHolderReplacement source) => new LongRestWorkAdmission();
                    }
                }
                """;
        }
        else if (shape == "optional-default")
        {
            const string required = "PerUnitRecoveryHandler(LongRestWorkAdmission workAdmission)";

            Assert.Equal(1, source.Split(required, StringSplitOptions.None).Length - 1);

            source = source.Replace(required, "PerUnitRecoveryHandler(LongRestWorkAdmission? workAdmission = null)", StringComparison.Ordinal);
        }

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BaseObjectCreationExpressionSyntax creation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>(), expression =>
            model.GetOperation(expression) is IObjectCreationOperation operation
                && operation.Constructor?.ContainingType.ToDisplayString() == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler");

        IObjectCreationOperation constructed = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(creation));

        IArgumentOperation argument = Assert.Single(constructed.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(argument.Parameter, Assert.Single(constructed.Constructor!.Parameters)));
        Assert.Equal(RefKind.None, argument.Parameter!.RefKind);

        if (shape == "optional-default")
        {
            Assert.Equal(ArgumentKind.DefaultValue, argument.ArgumentKind);
            Assert.True(argument.IsImplicit);
            Assert.True(argument.Value.ConstantValue.HasValue);
            Assert.Null(argument.Value.ConstantValue.Value);
            Assert.Empty(creation.ArgumentList!.Arguments);
        }
        else
        {
            Assert.Equal(ArgumentKind.Explicit, argument.ArgumentKind);
            Assert.True(argument.InConversion.IsIdentity);

            CastExpressionSyntax[] casts = Assert.Single(creation.ArgumentList!.Arguments).Expression
                .DescendantNodesAndSelf().OfType<CastExpressionSyntax>().ToArray();

            Assert.Equal(shape == "built-in" ? 1 : 2, casts.Length);

            if (shape == "authored-replacement")
            {
                IConversionOperation effective = Assert.IsAssignableFrom<IConversionOperation>(argument.Value);

                Assert.NotNull(effective.OperatorMethod);
                Assert.True(effective.Conversion.IsUserDefined);
                Assert.True(SymbolEqualityComparer.Default.Equals(effective.OperatorMethod,
                    Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(casts[0])).OperatorMethod));
            }

            foreach (CastExpressionSyntax cast in casts)
            {
                IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

                if (shape == "built-in")
                {
                    Assert.Null(conversion.OperatorMethod);
                    Assert.True(conversion.Conversion.IsIdentity);
                }
                else
                {
                    Assert.NotNull(conversion.OperatorMethod);
                    Assert.Equal(MethodKind.Conversion, conversion.OperatorMethod!.MethodKind);
                    Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.DirectHolderReplacement",
                        conversion.OperatorMethod.ContainingType.ToDisplayString());
                    Assert.True(conversion.Conversion.IsUserDefined);
                }
            }
        }

        output.WriteLine($"R_DIRECT_ARGUMENT_PREMISE={shape}|argument={argument.ArgumentKind}|implicit={argument.IsImplicit}|syntax={argument.Syntax.Kind()}|value={argument.Value.Syntax.Kind()}");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", preserved ? "owned-lease" : shape);
    }
}
