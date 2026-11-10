using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // This extends the existing owned-instrument observation contract to duration values.
        // It is not a purity claim about ambient MeterListener callbacks or their lifetimes.
        private static bool IsExactGuildDurationHistogramRecord(IMethodSymbol method) =>
            !method.IsStatic && method.Name == "Record" && method.Arity == 0 && method.ReturnsVoid
            && method.ContainingType.TypeArguments is [{ SpecialType: SpecialType.System_Double }]
            && IsExactFrameworkType(method.ContainingType, "System.Diagnostics.Metrics.Histogram`1", typeof(System.Diagnostics.Metrics.Histogram<>).Assembly.GetName())
            && method.Parameters.Length is >= 1 and <= 4
            && method.Parameters[0] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Double }
            && method.Parameters.Skip(1).All(static tag => tag is { RefKind: RefKind.None, IsParams: false }
                && tag.Type is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_String }, { SpecialType: SpecialType.System_Object }] } pair
                && IsExactFrameworkType(pair, "System.Collections.Generic.KeyValuePair`2", typeof(KeyValuePair<,>).Assembly.GetName()));

        private bool IsReviewedGuildDurationHistogramObservation(IMethodSymbol method, SyntaxNode? node, SemanticModel? model, AuthoredMember? context)
        {
            if (!IsExactGuildDurationHistogramRecord(method)
                || context is null || node is not InvocationExpressionSyntax
                || model?.GetOperation(node) is not IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver } invocation
                || !invocation.Arguments.All(ArgumentBoundaryUsesOnlyBuiltInConversions)
                || !invocation.Arguments.Where(static argument => argument.Parameter?.Ordinal != 0)
                    .All(argument => IsReviewedArcanumTokenTag(argument, context))
                || model.GetSymbolInfo(StripTransparentExpression(receiver)).Symbol is not IFieldSymbol field) return false;

            bool ExactMeter(IFieldSymbol meter)
            {
                if (!meter.IsReadOnly || !meter.IsStatic || meter.Name != "Meter"
                    || TypeKey(meter.ContainingType) != "RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics"
                    || !IsExactFrameworkType(meter.Type, "System.Diagnostics.Metrics.Meter", typeof(System.Diagnostics.Metrics.Meter).Assembly.GetName())
                    || HasAuthoredStorageWrite(meter)
                    || meter.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                    || declaration.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } value }
                    || !semanticModels.TryGetValue(value.SyntaxTree, out SemanticModel? meterModel)
                    || meterModel.GetOperation(value) is not IObjectCreationOperation { Constructor: { } constructor, Initializer: null } creation) return false;

                return IsExactFrameworkType(constructor.ContainingType, "System.Diagnostics.Metrics.Meter", typeof(System.Diagnostics.Metrics.Meter).Assembly.GetName())
                    && HasExactParameterTypes(constructor, "System.String", "System.String")
                    && creation.Arguments is [{ Value.ConstantValue: { HasValue: true, Value: "Arcanum" } },
                    { Value.ConstantValue: { HasValue: true, Value: "0.1.0" } }];
            }

            if (!field.IsReadOnly || !field.IsStatic || HasAuthoredStorageWrite(field)
                || TypeKey(field.ContainingType) != "RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics"
                || field.Type is not INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_Double }] }
                || !IsExactFrameworkType(field.Type, "System.Diagnostics.Metrics.Histogram`1", typeof(System.Diagnostics.Metrics.Histogram<>).Assembly.GetName())
                || field.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                || declaration.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                || !semanticModels.TryGetValue(initializer.SyntaxTree, out SemanticModel? fieldModel)
                || fieldModel.GetOperation(initializer) is not IInvocationOperation { Instance: IFieldReferenceOperation meterReference } created) return false;

            IMethodSymbol factory = created.TargetMethod;

            // Only the (name, unit, description) overload is reviewed: the instrument-tags and advice overloads
            // carry authored data into the instrument, and a constant argument keeps static initialization free
            // of work that no traversal reaches.
            return !factory.IsStatic && factory.Name == "CreateHistogram" && factory.Arity == 1
                && factory.TypeArguments is [{ SpecialType: SpecialType.System_Double }]
                && IsExactFrameworkType(factory.ContainingType, "System.Diagnostics.Metrics.Meter", typeof(System.Diagnostics.Metrics.Meter).Assembly.GetName())
                && HasExactParameterTypes(factory, "System.String", "System.String", "System.String")
                && factory.ReturnType is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_Double }] } returned
                && IsExactFrameworkType(returned, "System.Diagnostics.Metrics.Histogram`1", typeof(System.Diagnostics.Metrics.Histogram<>).Assembly.GetName())
                && created.Arguments is [
                { ArgumentKind: ArgumentKind.Explicit, Value.ConstantValue: { HasValue: true, Value: string name } },
                { ArgumentKind: ArgumentKind.Explicit, Value.ConstantValue: { HasValue: true, Value: null or string } },
                { ArgumentKind: ArgumentKind.Explicit, Value.ConstantValue: { HasValue: true, Value: null or string } }]
                && name == "arcanum_conclave_sending_duration_seconds"
                && ExactMeter(meterReference.Field);
        }
    }
}
