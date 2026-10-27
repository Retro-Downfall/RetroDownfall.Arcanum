using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Only the returned identity is proved here. The wrapper and every argument retain
        // ordinary traversal, including logging, conversions and any real producer effects.
        private bool IsReturnedRecoveryTerminal(AuthoredMember member, InvocationExpressionSyntax terminal)
        {
            ExpressionSyntax value = terminal;

            for (int depth = 0; depth < 8; depth++)
            {
                while (value.Parent is ParenthesizedExpressionSyntax parenthesized) value = parenthesized;

                if (value.Parent is ReturnStatementSyntax { Expression: { } returned } && returned == value) return true;

                if (value.Parent is not ArgumentSyntax argument
                    || argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax wrapper }
                    || member.Model.GetOperation(wrapper) is not IInvocationOperation invocation
                    || invocation.IsVirtual
                    || invocation.TargetMethod is { IsStatic: false, IsVirtual: true }
                    || invocation.TargetMethod.IsAbstract || invocation.TargetMethod.IsOverride || invocation.TargetMethod.IsExtern
                    || invocation.TargetMethod.IsVararg || invocation.TargetMethod.Arity != 0 || invocation.TargetMethod.ReducedFrom is not null
                    || invocation.Arguments.SingleOrDefault(item => item.Syntax == argument) is not { Parameter: { RefKind: RefKind.None } selected } supplied
                    || !supplied.InConversion.IsIdentity || !supplied.OutConversion.IsIdentity
                    || Resolve(invocation.TargetMethod, member.Model.Compilation) is not { } target
                    || !SymbolEqualityComparer.Default.Equals(selected.Type, invocation.TargetMethod.ReturnType)
                    || selected.Ordinal >= target.Symbol.Parameters.Length)
                {
                    return false;
                }

                IParameterSymbol parameter = target.Symbol.Parameters[selected.Ordinal];

                if (HasCollectionValueReplacement(target, parameter) || IsIncrementedRecoveryParameter(target, parameter)) return false;

                ExpressionSyntax[] returns = ReturnedExpressions(target.Syntax);

                if (returns.Length == 0 || returns.Any(expression => !ReturnsUnchangedParameter(target, expression, parameter))) return false;

                value = wrapper;
            }

            return false;
        }

        private static bool IsIncrementedRecoveryParameter(AuthoredMember member, IParameterSymbol parameter)
        {
            foreach (ExpressionSyntax expression in member.Syntax.DescendantNodes().OfType<ExpressionSyntax>())
            {
                ExpressionSyntax? operand = expression switch
                {
                    PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                    _ => null,
                };

                if (operand?.DescendantNodesAndSelf().OfType<ExpressionSyntax>()
                    .Any(value => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(value).Symbol, parameter)) == true) return true;
            }

            return false;
        }

        private static bool ReturnsUnchangedParameter(AuthoredMember member, ExpressionSyntax expression, IParameterSymbol parameter)
        {
            IOperation? operation = member.Model.GetOperation(expression);

            while (operation is IParenthesizedOperation or IConversionOperation { OperatorMethod: null, Conversion.IsIdentity: true })
            {
                operation = operation is IParenthesizedOperation parenthesized ? parenthesized.Operand : ((IConversionOperation)operation).Operand;
            }

            return operation is IParameterReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter);
        }

        private bool IsReviewedClosedScalarLog(IMethodSymbol method, SyntaxNode? node, SemanticModel? model)
        {
            method = method.ReducedFrom ?? method;

            if (inventoryFacts.AuthoredLoggerType is not null
                || !method.IsStatic || method.Arity != 0 || method.Name != "Log" || !method.ReturnsVoid
                || !IsExactFrameworkType(method.ContainingType, "Microsoft.Extensions.Logging.LoggerExtensions", typeof(Microsoft.Extensions.Logging.LoggerExtensions).Assembly.GetName())
                || method.Parameters.Length != 4
                || !method.Parameters.Take(3).Select(static parameter => TypeKey(parameter.Type)).SequenceEqual(
                    ["Microsoft.Extensions.Logging.ILogger", "Microsoft.Extensions.Logging.LogLevel", "System.String"], StringComparer.Ordinal)
                || method.Parameters[3] is not { IsParams: true, RefKind: RefKind.None, Type: IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Object } }
                || node is not InvocationExpressionSyntax || model?.GetOperation(node) is not IInvocationOperation invocation
                || !invocation.Arguments.All(ArgumentBoundaryUsesOnlyBuiltInConversions)
                || invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.IsParams == true)?.Value is not IArrayCreationOperation { Initializer: { } initializer })
            {
                return false;
            }

            return initializer.ElementValues.All(IsClosedFrameworkFormattingValue);
        }

        private static bool IsClosedFrameworkFormattingValue(IOperation value)
        {
            while (value is IParenthesizedOperation or IConversionOperation { OperatorMethod: null })
            {
                value = value is IParenthesizedOperation parenthesized ? parenthesized.Operand : ((IConversionOperation)value).Operand;
            }

            if (value.ConstantValue is { HasValue: true, Value: null }) return true;

            ITypeSymbol? type = value.Type;

            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var element] }) type = element;

            if (type is null) return false;

            if (type.TypeKind == TypeKind.Enum) return true;

            return type.SpecialType != SpecialType.None && IsIntrinsicFormatType(type) && HasExactSpecialTypeIdentity(type)
                || TypeKey(type) is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan"
                    && IsExactFrameworkType(type, TypeKey(type), typeof(Guid).Assembly.GetName());
        }
        // GetValue invokes this exact factory synchronously. A Task returned as TValue is
        // stored, so its completion is deliberately not inherited by the factory body.
        private static bool IsExactWeakTableValueFactory(IMethodSymbol method)
        {
            IMethodSymbol definition = method.OriginalDefinition;

            if (definition.IsStatic || definition.IsVirtual || definition.IsAbstract || definition.Arity != 0 || definition.Name != "GetValue"
                || !IsExactFrameworkType(definition.ContainingType, "System.Runtime.CompilerServices.ConditionalWeakTable`2", typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>).Assembly.GetName())
                || definition.ContainingType.TypeParameters is not [var key, var value]
                || definition.Parameters is not
                [
                { RefKind: RefKind.None, Type: { } suppliedKey },
                { RefKind: RefKind.None, Type: INamedTypeSymbol { TypeKind: TypeKind.Delegate, Name: "CreateValueCallback", DelegateInvokeMethod: { } invoke } callback },
                ]
                || !SymbolEqualityComparer.Default.Equals(callback.ContainingType, definition.ContainingType)
                || !SymbolEqualityComparer.Default.Equals(suppliedKey, key)
                || !SymbolEqualityComparer.Default.Equals(definition.ReturnType, value)
                || invoke.Parameters is not [{ RefKind: RefKind.None, Type: { } callbackKey }])
            {
                return false;
            }

            return SymbolEqualityComparer.Default.Equals(callbackKey, key)
                && SymbolEqualityComparer.Default.Equals(invoke.ReturnType, value);
        }

        private static bool IsReviewedInvariantScalarInterpolation(IMethodSymbol method, SyntaxNode? node, SemanticModel? model)
        {
            if (!method.IsStatic || method.Arity != 0 || method.Name != "Invariant" || method.ReturnType.SpecialType != SpecialType.System_String
                || !IsExactFrameworkType(method.ContainingType, "System.FormattableString", typeof(FormattableString).Assembly.GetName())
                || method.Parameters is not [{ RefKind: RefKind.None, Type: { } input }]
                || !SymbolEqualityComparer.Default.Equals(input, method.ContainingType)
                || node is not InvocationExpressionSyntax || model?.GetOperation(node) is not IInvocationOperation { Arguments: [var argument] }
                || !ArgumentBoundaryUsesOnlyBuiltInConversions(argument))
            {
                return false;
            }

            IOperation value = argument.Value;

            while (value is IParenthesizedOperation or IConversionOperation { OperatorMethod: null })
            {
                value = value is IParenthesizedOperation parenthesized ? parenthesized.Operand : ((IConversionOperation)value).Operand;
            }

            // Stored/custom FormattableString objects can override formatting. Only this
            // compiler-created inline scalar interpolation has no authored formatting target.
            return value is IInterpolatedStringOperation interpolation
                && interpolation.Parts.OfType<IInterpolationOperation>().All(static hole => IsClosedFrameworkFormattingValue(hole.Expression));
        }

        private static bool CarriesExactSystemClockDependency(AuthoredMember target) => target.Symbol.ContainingType.InstanceConstructors
            .Any(static constructor => constructor.Parameters.Any(static parameter => IsExactSystemClockType(parameter.Type)));

        private static bool IsExactSystemClockType(ITypeSymbol type) =>
            IsExactFrameworkType(type, "System.TimeProvider", typeof(TimeProvider).Assembly.GetName());

        private AuthoredMember BindExactDiSystemClockArguments(AuthoredMember constructor)
        {
            Dictionary<ISymbol, BoundValueSource> values = constructor.ValueBindings?.ToDictionary(
                static pair => pair.Key, static pair => pair.Value, SymbolEqualityComparer.Default) ?? new(SymbolEqualityComparer.Default);

            foreach (IParameterSymbol parameter in constructor.Symbol.Parameters.Where(static parameter => IsExactSystemClockType(parameter.Type)))
            {
                BoundContractIdentity key = new(parameter.Type.ContainingAssembly.Identity, TypeKey(parameter.Type));

                ResolvedRegistration[] registrations = (diActivationRegistrations.TryGetValue(key, out List<ResolvedRegistration>? indexed) ? indexed : [])
                    .DistinctBy(static registration => registration.Call, ReferenceEqualityComparer.Instance).ToArray();

                BoundValueSource? witness = null;

                bool complete = registrations.Length > 0;

                foreach (ResolvedRegistration registration in registrations)
                {
                    IMethodSymbol definition = registration.Method.ReducedFrom is { } reduced
                        ? reduced.Construct(registration.Method.TypeArguments.ToArray()) : registration.Method;

                    if (definition.Name is not ("AddSingleton" or "TryAddSingleton")
                        || !IsExactServiceCollectionFluentMethod(registration.Method)
                        || definition.Parameters is not [_, { Type: { } registeredType }]
                        || !IsExactSystemClockType(registeredType)
                        || registration.Call.ArgumentList.Arguments is not [ArgumentSyntax { Expression: { } expression }]
                        || registration.Model.GetSymbolInfo(StripTransparentExpression(expression)).Symbol is not IPropertySymbol
                        { Name: "System", IsStatic: true, SetMethod: null, GetMethod: { IsStatic: true, IsVirtual: false }, ContainingType: { } clockType }
                        || !IsExactSystemClockType(clockType)
                        || registration.Model.GetEnclosingSymbol(registration.Call.SpanStart) is not IMethodSymbol enclosing
                        || Resolve(enclosing, registration.Model.Compilation) is not { } caller)
                    {
                        complete = false;
                        break;
                    }

                    witness ??= new(caller, expression);
                }

                if (complete && invocations.Any(candidate =>
                    !registrations.Any(registration => ReferenceEquals(registration.Call, candidate.Call))
                    && candidate.Model.GetSymbolInfo(candidate.Call).Symbol is IMethodSymbol method
                    && IsApplicableServiceRegistrationMutation(candidate.Call, candidate.Model, method,
                        static (type, _) => IsExactSystemClockType(type)))) complete = false;

                if (complete && witness is not null) values[parameter] = witness;
            }

            return constructor with { ValueBindings = values };
        }

        private bool HasClockReceiverReplacement(AuthoredMember owner, SemanticModel model, ISymbol symbol)
        {
            if (HasCollectionValueReplacement(owner with { Model = model }, symbol)) return true;

            if (symbol is not IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } constructor }) return false;

            // A captured primary-constructor parameter is in scope in every partial declaration.
            // Only those exact declaring parts can write it; an unavailable part is not a proof.
            foreach (SyntaxReference part in constructor.ContainingType.DeclaringSyntaxReferences)
            {
                if (!semanticModels.TryGetValue(part.SyntaxTree, out SemanticModel? partModel)
                    || HasCollectionValueReplacement(owner with { Model = partModel }, symbol)) return true;
            }

            return false;
        }

        private bool IsReviewedSystemClockElapsed(IMethodSymbol method, SyntaxNode? node, SemanticModel? model, AuthoredMember? context)
        {
            if (method.IsStatic || method.IsVirtual || method.IsAbstract || method.Arity != 0 || method.Name != "GetElapsedTime"
                || !HasExactParameterTypes(method, "System.Int64", "System.Int64")
                || !IsExactFrameworkType(method.ContainingType, "System.TimeProvider", typeof(TimeProvider).Assembly.GetName())
                || !IsExactFrameworkType(method.ReturnType, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName())
                || context is null || node is not InvocationExpressionSyntax
                || model?.GetOperation(node) is not IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver })
            {
                return false;
            }

            int remaining = 64;

            bool Origin(ExpressionSyntax value, SemanticModel sourceModel, HashSet<ISymbol> path, AuthoredMember? owner)
            {
                if (owner is null || --remaining < 0 || sourceModel.GetOperation(value) is not { } source
                    || source.DescendantsAndSelf().OfType<IConversionOperation>().Any(static conversion => conversion.OperatorMethod is not null)) return false;

                value = StripTransparentExpression(value);

                if (sourceModel.GetSymbolInfo(value).Symbol is ILocalSymbol or IParameterSymbol
                    && HasClockReceiverReplacement(owner, sourceModel, sourceModel.GetSymbolInfo(value).Symbol!)) return false;

                if (sourceModel.GetSymbolInfo(value).Symbol is IPropertySymbol { Name: "System", IsStatic: true, SetMethod: null, GetMethod: { IsStatic: true, IsVirtual: false } } property
                    && IsExactFrameworkType(property.ContainingType, "System.TimeProvider", typeof(TimeProvider).Assembly.GetName())) return true;

                return HasOnlyReviewedValueSources(value, sourceModel, path, owner, Origin);
            }

            // GetElapsedTime reads virtual TimestampFrequency. Its pinned System receiver
            // selects the framework implementation; an unknown or authored clock is refused.
            return Origin(receiver, model, new(SymbolEqualityComparer.Default), context);
        }
    }
}
