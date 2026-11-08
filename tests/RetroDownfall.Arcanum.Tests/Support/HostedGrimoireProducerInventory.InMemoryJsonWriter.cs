using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<AuthoredMember, Dictionary<ISymbol, bool>> _closedMemoryJsonWriterUses = [];

        private static bool IsReviewedInMemoryJsonProperty(IPropertySymbol property, bool mutation) =>
            mutation && property.Name == "Encoder"
                && IsExactFrameworkType(property.ContainingType, "System.Text.Json.JsonWriterOptions",
                    typeof(System.Text.Json.JsonWriterOptions).Assembly.GetName())
            || !mutation && property is { IsStatic: true, Name: "UnsafeRelaxedJsonEscaping" }
                && IsExactFrameworkType(property.ContainingType, "System.Text.Encodings.Web.JavaScriptEncoder",
                    typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.GetName());

        // Utf8JsonWriter invokes its stream and encoder. Its framework identity alone
        // therefore proves nothing about I/O: both backends and every writer use close.
        private bool IsReviewedInMemoryJsonMember(IMethodSymbol method, SyntaxNode? node,
            SemanticModel? model, AuthoredMember? context)
        {
            if (IsExactFrameworkType(method.ContainingType, "System.Text.Encodings.Web.JavaScriptEncoder",
                    typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.GetName()))
                return method is { IsStatic: true, Name: "get_UnsafeRelaxedJsonEscaping", Parameters.Length: 0 };

            if (IsExactFrameworkType(method.ContainingType, "System.Text.Json.JsonWriterOptions",
                    typeof(System.Text.Json.JsonWriterOptions).Assembly.GetName()))
                return method.Name is ".ctor" or "set_Encoder";

            if (model is null || context is null) return false;

            if (IsExactFrameworkType(method.ContainingType, "System.Text.Json.Utf8JsonWriter",
                    typeof(System.Text.Json.Utf8JsonWriter).Assembly.GetName()))
            {
                if (method.MethodKind == MethodKind.Constructor && node is BaseObjectCreationExpressionSyntax creation)
                    return HasClosedMemoryJsonWriterOrigin(creation, model, context);

                if (!IsMemoryJsonWriterOperation(method) || node is not ExpressionSyntax expression) return false;

                ExpressionSyntax? receiver = node is InvocationExpressionSyntax call
                    && model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax instance }
                    ? instance : expression;

                return HasMemoryJsonWriterProvenance(receiver, model, new(SymbolEqualityComparer.Default), context);
            }

            return method.Name is "Dispose" or "DisposeAsync"
                && IsExactFrameworkType(method.ContainingType, "System.IO.Stream", typeof(Stream).Assembly.GetName())
                && node is ExpressionSyntax resource
                && HasClosedJsonBufferProvenance(resource, model, new(SymbolEqualityComparer.Default), context);
        }

        private static bool IsMemoryJsonWriterOperation(IMethodSymbol method) =>
            method.Name is "WriteStartObject" or "WriteEndObject" or "WriteStartArray" or "WriteEndArray"
                or "WriteString" or "WriteStringValue" or "WriteNumber" or "WriteNumberValue"
                or "WritePropertyName" or "WriteBoolean" or "WriteBooleanValue" or "WriteNull" or "WriteNullValue"
                or "WriteRawValue" or "Flush" or "Dispose"
            && !method.Parameters.Any(static parameter => parameter.Type.TypeKind == TypeKind.Delegate
                || parameter.RefKind is RefKind.Ref or RefKind.Out);

        private bool HasMemoryJsonWriterProvenance(ExpressionSyntax expression, SemanticModel model,
            HashSet<ISymbol> path, AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax creation && context is not null)
                return HasClosedMemoryJsonWriterOrigin(creation, model, context);

            return HasOnlyReviewedValueSources(expression, model, path, context, HasMemoryJsonWriterProvenance);
        }

        private bool HasClosedMemoryJsonWriterOrigin(BaseObjectCreationExpressionSyntax creation,
            SemanticModel model, AuthoredMember context)
        {
            if (model.GetOperation(creation) is not IObjectCreationOperation { Constructor: { } constructor } operation
                || !IsExactFrameworkType(constructor.ContainingType, "System.Text.Json.Utf8JsonWriter",
                    typeof(System.Text.Json.Utf8JsonWriter).Assembly.GetName())
                || operation.Arguments.Length != 2
                || operation.Arguments[0].Value.Syntax is not ExpressionSyntax stream
                || !HasClosedJsonBufferProvenance(stream, model, new(SymbolEqualityComparer.Default), context)
                || operation.Arguments[1].Value.Syntax is not ExpressionSyntax options
                || !HasClosedJsonWriterOptions(options, model)
                || creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
                || model.GetDeclaredSymbol(variable) is not ILocalSymbol writer
                || variable.Parent?.Parent is not LocalDeclarationStatementSyntax declaration
                || declaration.UsingKeyword.RawKind == 0)
                return false;

            return MemoryJsonWriterUsesAreClosed(writer, context, new(SymbolEqualityComparer.Default));
        }

        private static bool HasClosedJsonWriterOptions(ExpressionSyntax expression, SemanticModel model)
        {
            if (expression.DescendantNodesAndSelf().OfType<CastExpressionSyntax>().Any()) return false;

            expression = StripTransparentExpression(expression);

            if (expression is DefaultExpressionSyntax || expression.IsKind(SyntaxKind.DefaultLiteralExpression))
                return IsExactFrameworkType(model.GetTypeInfo(expression).ConvertedType!, "System.Text.Json.JsonWriterOptions",
                    typeof(System.Text.Json.JsonWriterOptions).Assembly.GetName());

            if (expression is not BaseObjectCreationExpressionSyntax creation
                || model.GetTypeInfo(creation).Type is not { } type
                || !IsExactFrameworkType(type, "System.Text.Json.JsonWriterOptions", typeof(System.Text.Json.JsonWriterOptions).Assembly.GetName()))
                return false;

            return creation.Initializer is null || creation.Initializer.Expressions.All(value =>
                value is AssignmentExpressionSyntax assignment
                && model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol property
                && SymbolEqualityComparer.Default.Equals(property.ContainingType, type)
                && (property.Name == "Encoder"
                    ? model.GetSymbolInfo(assignment.Right).Symbol is IPropertySymbol { Name: "UnsafeRelaxedJsonEscaping", IsStatic: true } encoder
                        && IsExactFrameworkType(encoder.ContainingType, "System.Text.Encodings.Web.JavaScriptEncoder",
                            typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.GetName())
                    : property.Name is "Indented" or "SkipValidation" or "MaxDepth"
                        && model.GetConstantValue(assignment.Right).HasValue));
        }

        private bool MemoryJsonWriterUsesAreClosed(ISymbol writer, AuthoredMember owner, HashSet<ISymbol> path)
        {
            if (!path.Add(writer)) return false;

            try
            {
                if (_closedMemoryJsonWriterUses.TryGetValue(owner, out Dictionary<ISymbol, bool>? cached)
                    && cached.TryGetValue(writer, out bool proved)) return proved;

                bool complete = ProveUses();

                if (!_closedMemoryJsonWriterUses.TryGetValue(owner, out cached))
                    _closedMemoryJsonWriterUses[owner] = cached = new(SymbolEqualityComparer.Default);

                cached[writer] = complete;

                return complete;
            }
            finally
            {
                path.Remove(writer);
            }

            bool ProveUses()
            {
                foreach (IdentifierNameSyntax reference in owner.Syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Where(reference => SymbolEqualityComparer.Default.Equals(owner.Model.GetSymbolInfo(reference).Symbol, writer)))
                {
                    if (reference.Ancestors().TakeWhile(node => node != owner.Syntax)
                        .Any(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) return false;

                    if (reference.Parent is MemberAccessExpressionSyntax { Expression: { } receiver, Parent: InvocationExpressionSyntax call }
                        && receiver == reference && owner.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                        && IsExactFrameworkType(method.ContainingType, "System.Text.Json.Utf8JsonWriter", typeof(System.Text.Json.Utf8JsonWriter).Assembly.GetName())
                        && IsMemoryJsonWriterOperation(method)) continue;

                    if (reference.Parent is not ArgumentSyntax { RefKindKeyword.RawKind: 0, Parent.Parent: InvocationExpressionSyntax forwarded } argument
                        || owner.Model.GetOperation(argument) is not IArgumentOperation { Parameter: { } parameter }
                        || owner.Model.GetSymbolInfo(forwarded).Symbol is not IMethodSymbol invoked)
                        return false;

                    AuthoredMember? target = invoked.MethodKind == MethodKind.DelegateInvoke
                        ? ResolveCallable(forwarded.Expression, owner.Model, owner.CallableBindings, owner.AbsentCallables)
                        : ResolveInvocationTarget(invoked, owner, forwarded);

                    if (target is null || target.Symbol.IsAsync || !target.Symbol.ReturnsVoid
                        || parameter.Ordinal >= target.Symbol.Parameters.Length) return false;

                    AuthoredMember bound = invoked.MethodKind == MethodKind.DelegateInvoke
                        ? BindResolvedCallableInvocationContext(owner, forwarded, target)
                        : BindInvocationContext(owner, forwarded, target);

                    if (!MemoryJsonWriterUsesAreClosed(target.Symbol.Parameters[parameter.Ordinal], bound, path)) return false;
                }

                return true;
            }
        }

        private bool HasClosedJsonBufferProvenance(ExpressionSyntax expression, SemanticModel model,
            HashSet<ISymbol> path, AuthoredMember? context)
        {
            if (expression.DescendantNodesAndSelf().OfType<CastExpressionSyntax>().Any()) return false;

            expression = StripTransparentExpression(expression);

            // Readonly storage may still be rebound through ref/out in its constructor.
            ISymbol? source = model.GetSymbolInfo(expression).Symbol;

            if (source is IFieldSymbol or IPropertySymbol) return false;

            if (source is ILocalSymbol or IParameterSymbol)
            {
                SyntaxNode scope = model.SyntaxTree.GetRoot();

                if (source is ILocalSymbol { RefKind: not RefKind.None } or IParameterSymbol { RefKind: not RefKind.None }
                    || scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                        SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(StripTransparentExpression(assignment.Left)).Symbol, source)
                        || DeconstructsInto(assignment, model, source))
                    || scope.DescendantNodes().OfType<RefExpressionSyntax>().Any(reference =>
                        SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(StripTransparentExpression(reference.Expression)).Symbol, source))
                    || scope.DescendantNodes().OfType<ArgumentSyntax>().Any(argument =>
                        (argument.RefKindKeyword.RawKind != 0
                            || model.GetOperation(argument) is IArgumentOperation { Parameter.RefKind: not RefKind.None })
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(StripTransparentExpression(argument.Expression)).Symbol, source))) return false;
            }

            if (expression is BaseObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation).Type is INamedTypeSymbol type)
                return IsExactFrameworkType(type, "System.IO.MemoryStream", typeof(MemoryStream).Assembly.GetName())
                    || IsClosedBoundedJsonBuffer(type);

            return HasOnlyReviewedValueSources(expression, model, path, context, HasClosedJsonBufferProvenance);
        }

        private bool IsClosedBoundedJsonBuffer(INamedTypeSymbol type)
        {
            if (!type.IsSealed || type.BaseType is not { } parent
                || !IsExactFrameworkType(parent, "System.IO.MemoryStream", typeof(MemoryStream).Assembly.GetName())) return false;

            ISymbol[] authored = type.GetMembers().Where(static member => !member.IsImplicitlyDeclared).ToArray();

            return authored.Length == 2 && authored.All(member => member is IMethodSymbol
                { IsOverride: true, Name: "Write", ReturnsVoid: true } method
                && method.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is MethodDeclarationSyntax declaration
                && semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model)
                && model.GetOperation(declaration.Body!) is { } body && JsonBufferBodyIsMechanical(body));
        }

        private static bool JsonBufferBodyIsMechanical(IOperation operation)
        {
            foreach (IOperation value in operation.DescendantsAndSelf())
            {
                bool accepted = value switch
                {
                    IBlockOperation or IExpressionStatementOperation or IConditionalOperation or IThrowOperation
                        or IArgumentOperation or IParameterReferenceOperation or ILiteralOperation or IInstanceReferenceOperation => true,
                    IBinaryOperation binary => binary.OperatorMethod is null,
                    IConversionOperation conversion => conversion.OperatorMethod is null,
                    IFieldReferenceOperation { Field.IsConst: true, ConstantValue.HasValue: true } => true,
                    IPropertyReferenceOperation property => property.Property.Name == "Position"
                        && IsExactFrameworkType(property.Property.ContainingType, "System.IO.MemoryStream", typeof(MemoryStream).Assembly.GetName())
                        || property.Property.Name == "Length" && IsExactFrameworkType(property.Property.ContainingType,
                            "System.ReadOnlySpan`1", typeof(ReadOnlySpan<>).Assembly.GetName()),
                    IInvocationOperation invocation => invocation.TargetMethod.Name == "Write"
                        && invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } }
                        && IsExactFrameworkType(invocation.TargetMethod.ContainingType, "System.IO.MemoryStream", typeof(MemoryStream).Assembly.GetName())
                        || invocation.TargetMethod.Name == "ToArray"
                            && (IsExactFrameworkType(invocation.TargetMethod.ContainingType,
                                    "System.MemoryExtensions", typeof(MemoryExtensions).Assembly.GetName())
                                || IsExactFrameworkType(invocation.TargetMethod.ContainingType,
                                    "System.ReadOnlySpan`1", typeof(ReadOnlySpan<>).Assembly.GetName())),
                    IObjectCreationOperation creation => creation.Arguments.Length == 0
                        && creation.Type is INamedTypeSymbol { IsSealed: true, BaseType: { } exception } thrown
                        && IsExactFrameworkType(exception, "System.Exception", typeof(Exception).Assembly.GetName())
                        && thrown.GetMembers().All(static member => member.IsImplicitlyDeclared),
                    _ => false,
                };

                if (!accepted) return false;
            }

            return true;
        }
    }
}
