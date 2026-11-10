using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private static bool IsExactJsonMetadataDictionary(ITypeSymbol type, string definition) =>
            type is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_String }, { } value] }
            && IsExactFrameworkType(type, definition, typeof(Dictionary<,>).Assembly.GetName())
            && IsExactFrameworkType(value, "System.Text.Json.JsonElement", typeof(System.Text.Json.JsonElement).Assembly.GetName());

        private static bool IsExactGuildMetadataLookup(IMethodSymbol method) =>
            method is { IsStatic: false, Name: "TryGetValue", Arity: 0, ReturnType.SpecialType: SpecialType.System_Boolean }
            && IsExactJsonMetadataDictionary(method.ContainingType, "System.Collections.Generic.IDictionary`2")
            && method.Parameters is [{ RefKind: RefKind.None, Type.SpecialType: SpecialType.System_String }, { RefKind: RefKind.Out } value]
            && IsExactFrameworkType(value.Type, "System.Text.Json.JsonElement", typeof(System.Text.Json.JsonElement).Assembly.GetName());

        private bool IsReviewedGuildMetadataLookup(IMethodSymbol method, SyntaxNode? node, AuthoredMember? member) =>
            member is not null && node is not null && IsExactGuildMetadataLookup(method)
            && member.Model.GetOperation(node) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver }
            && HasClosedGuildMetadataOrigin(member, receiver, new CleanupProvenanceContext());

        private bool HasClosedGuildMetadataOrigin(AuthoredMember? member, ExpressionSyntax expression, CleanupProvenanceContext proof,
            SemanticModel? suppliedModel = null)
        {
            SemanticModel? model = suppliedModel ?? member?.Model;

            if (model is null) return false;

            bool Origin(ExpressionSyntax value, SemanticModel ownerModel, HashSet<ISymbol> path, AuthoredMember? owner)
            {
                if (proof.RemainingWork-- <= 0) return false;

                value = StripTransparentExpression(value);

                IOperation? operation = ownerModel.GetOperation(value);

                if (operation is IConversionOperation { OperatorMethod: not null }) return false;

                if (operation?.ConstantValue is { HasValue: true, Value: null }) return true;

                // A mutable SDK Metadata slot does not close a dictionary's stored
                // comparer or reimplemented interface slot. Only explicit constructions
                // and stable bindings to those constructions are reviewed here.
                if (IsClosedGuildMetadataConstruction(value, ownerModel)) return true;

                if (owner is null) return false;

                ISymbol? symbol = ownerModel.GetSymbolInfo(value).Symbol;

                if (symbol is ILocalSymbol or IParameterSymbol && HasCollectionValueReplacement(owner, symbol)) return false;

                if (symbol is not null && CanUseCurrentInstanceStorageBinding(symbol, value)
                    && owner.ValueBindings?.TryGetValue(symbol, out BoundValueSource? bound) == true && bound is not null)
                {
                    if (!path.Add(symbol)) return false;

                    try { return Origin(bound.Expression, bound.Caller.Model, path, bound.Caller); }
                    finally { path.Remove(symbol); }
                }

                return HasOnlyReviewedValueSources(value, ownerModel, path, owner, Origin);
            }

            return Origin(expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member);
        }

        private static bool IsClosedGuildMetadataConstruction(ExpressionSyntax expression, SemanticModel model)
        {
            if (expression is CollectionExpressionSyntax { Elements.Count: 0 }
                && model.GetTypeInfo(expression).ConvertedType is { } collection
                && IsExactJsonMetadataDictionary(collection, "System.Collections.Generic.Dictionary`2")) return true;

            if (model.GetOperation(expression) is not IObjectCreationOperation { Constructor: { } constructor } creation
                || !IsExactJsonMetadataDictionary(constructor.ContainingType, "System.Collections.Generic.Dictionary`2")) return false;

            return creation.Arguments.All(argument => ArgumentBoundaryUsesOnlyBuiltInConversions(argument)
                && (argument.Parameter?.Type.SpecialType == SpecialType.System_Int32
                    || argument.Parameter?.Type is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_String }] } comparer
                        && IsExactFrameworkType(comparer, "System.Collections.Generic.IEqualityComparer`1", typeof(IEqualityComparer<>).Assembly.GetName())
                        && (argument.Value.ConstantValue is { HasValue: true, Value: null }
                            || argument.Value.Syntax is ExpressionSyntax value && model.GetSymbolInfo(value).Symbol is IPropertySymbol
                            { IsStatic: true, Name: "Ordinal" or "OrdinalIgnoreCase", GetMethod.IsVirtual: false } ordinal
                            && IsExactFrameworkType(ordinal.ContainingType, "System.StringComparer", typeof(StringComparer).Assembly.GetName()))));
        }

        private static bool CouldBeMetadataString(ITypeSymbol type) => type.SpecialType == SpecialType.System_String || type is ITypeParameterSymbol;

        private static bool IsPotentialJsonDictionary(INamedTypeSymbol type, string definition = "System.Collections.Generic.Dictionary`2") =>
            IsExactFrameworkType(type, definition, typeof(Dictionary<,>).Assembly.GetName())
            && type.TypeArguments is [{ } key, { } value] && CouldBeMetadataString(key)
            && (value is ITypeParameterSymbol || IsExactFrameworkType(value, "System.Text.Json.JsonElement", typeof(System.Text.Json.JsonElement).Assembly.GetName()));
    }
}
