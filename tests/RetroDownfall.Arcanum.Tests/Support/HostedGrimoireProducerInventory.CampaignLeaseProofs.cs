using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private bool IsReviewedAbsentHttpClientCleanup(IMethodSymbol method, SyntaxNode? node, SemanticModel? model, AuthoredMember? context)
        {
            if (method.IsStatic || method.Name != "Dispose" || !method.ReturnsVoid
                || !HasExactParameterTypes(method)
                || !IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpMessageInvoker", typeof(HttpMessageInvoker).Assembly.GetName())
                || node is not InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } call
                || call.Parent is not ConditionalAccessExpressionSyntax conditional
                || conditional.WhenNotNull != call || model is null || context is null)
            {
                return false;
            }

            ExpressionSyntax receiver = UnwrapCampaignLeaseNullSyntax(conditional.Expression);

            if (model.GetSymbolInfo(receiver).Symbol is not IFieldSymbol
                {
                    IsStatic: false,
                    IsReadOnly: true,
                    DeclaredAccessibility: Accessibility.Private,
                } field
                || !IsExactCampaignLeaseHttpClientType(field.Type)
                || !CanUseCurrentInstanceStorageBinding(field, receiver)
                || !SameBoundType(field.ContainingType, model.Compilation,
                    context.Symbol.ContainingType, context.Model.Compilation))
            {
                return false;
            }

            // A conditional call on the constructor's proven null owned transport cannot
            // execute. A non-null transport can invoke arbitrary handler cleanup and remains
            // a boundary even when the same lease also holds an authenticated SDK adapter.
            // Follow only stable constructor bindings to a typed null; a conversion of null
            // through a caller-owned transport type can construct a live HttpClient.
            return HasExactNullCampaignLeaseTransport(context, receiver,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        }

        private static bool IsExactCampaignLeaseHttpClientType(ITypeSymbol type) =>
            IsExactFrameworkType(type, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName());

        private static ExpressionSyntax UnwrapCampaignLeaseNullSyntax(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => UnwrapCampaignLeaseNullSyntax(parenthesized.Expression),
            PostfixUnaryExpressionSyntax suppression
                when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                    UnwrapCampaignLeaseNullSyntax(suppression.Operand),
            _ => expression,
        };

        private bool HasExactNullCampaignLeaseTransport(AuthoredMember member, ExpressionSyntax expression, HashSet<ISymbol> path)
        {
            expression = UnwrapCampaignLeaseNullSyntax(expression);

            if (expression is CastExpressionSyntax cast)
            {
                return member.Model.GetOperation(cast) is IConversionOperation { OperatorMethod: null, Type: { } type }
                    && IsExactCampaignLeaseHttpClientType(type)
                    && HasExactNullCampaignLeaseTransport(member, cast.Expression, path);
            }

            ITypeSymbol? valueType = member.Model.GetTypeInfo(expression).Type;

            if (valueType is not null && !IsExactCampaignLeaseHttpClientType(valueType))
            {
                return false;
            }

            if (member.Model.GetConstantValue(expression) is { HasValue: true, Value: null }
                || valueType is not null
                    && (expression is DefaultExpressionSyntax || expression.IsKind(SyntaxKind.DefaultLiteralExpression)))
            {
                return true;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is IFieldSymbol field)
            {
                if (field is not { IsStatic: false, IsReadOnly: true, DeclaredAccessibility: Accessibility.Private }
                    || !CanUseCurrentInstanceStorageBinding(field, expression)
                    || !SameBoundType(field.ContainingType, member.Model.Compilation,
                        member.Symbol.ContainingType, member.Model.Compilation))
                {
                    return false;
                }
            }
            else if (symbol is not IParameterSymbol
                {
                    RefKind: RefKind.None,
                    ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor },
                } parameter
                || HasConstructorDependencyMutation(member, parameter))
            {
                return false;
            }

            if (symbol is null || !path.Add(symbol))
            {
                return false;
            }

            try
            {
                return TryResolveStableBoundValueSource(member, expression, out BoundValueSource? source)
                    && source is not null
                    && HasExactNullCampaignLeaseTransport(source.Caller, source.Expression, path);
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private AuthoredMember BindExactConstructedReceiverPropertyContext(AuthoredMember bound, AuthoredMember target,
            BoundValueSource exactReceiver, CleanupProvenanceContext context)
        {
            Dictionary<ISymbol, BoundValueSource> values = bound.ValueBindings?.ToDictionary(
                static pair => pair.Key, static pair => pair.Value, SymbolEqualityComparer.Default)
                ?? new(SymbolEqualityComparer.Default);

            Dictionary<ISymbol, ITypeSymbol> concrete = bound.ConcreteBindings?.ToDictionary(
                static pair => pair.Key, static pair => pair.Value, SymbolEqualityComparer.Default)
                ?? new(SymbolEqualityComparer.Default);

            IPropertySymbol[] properties = target.Syntax.DescendantNodesAndSelf(node => node == target.Syntax
                    || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<ExpressionSyntax>()
                .Select(expression => (Expression: expression, Symbol: target.Model.GetSymbolInfo(expression).Symbol))
                .Where(value => value.Symbol is not null
                    && CanUseCurrentInstanceStorageBinding(value.Symbol, value.Expression))
                .Select(static value => value.Symbol)
                .OfType<IPropertySymbol>()
                .Where(property => !property.IsStatic && property.SetMethod is not { IsInitOnly: false }
                    && SameBoundType(property.ContainingType, target.Model.Compilation,
                        target.Symbol.ContainingType, target.Model.Compilation))
                .DistinctBy(static property => property.OriginalDefinition, SymbolEqualityComparer.Default)
                .ToArray();

            foreach (IPropertySymbol property in properties)
            {
                if (PropertyValueFromExactConstruction(exactReceiver, property, context) is not { Complete: true } propertyFlow)
                {
                    continue;
                }

                if (propertyFlow.Values is [BoundValueSource propertyValue])
                {
                    values[property] = propertyValue;
                }
                else if (TryResolveConvergentCleanupPropertyType(propertyFlow, context, out ITypeSymbol propertyType))
                {
                    concrete[property] = propertyType;
                }
            }

            return bound with { ConcreteBindings = concrete, ValueBindings = values };
        }
    }
}
