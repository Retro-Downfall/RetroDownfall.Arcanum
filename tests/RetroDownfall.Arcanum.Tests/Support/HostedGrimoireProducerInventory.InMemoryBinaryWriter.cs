using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<AuthoredMember, Dictionary<ISymbol, bool>> closedMemoryBinaryWriterUses = [];

        // BinaryWriter dispatches both its stream and encoding, including Flush during
        // leaveOpen disposal. Only an exact fresh writer with closed uses is in-memory.
        private bool IsReviewedInMemoryBinaryWriterMember(IMethodSymbol method, SyntaxNode? node,
            SemanticModel? model, AuthoredMember? context)
        {
            if (model is null || context is null
                || !IsExactFrameworkType(method.ContainingType, "System.IO.BinaryWriter", typeof(BinaryWriter).Assembly.GetName()))
                return false;

            if (method.MethodKind == MethodKind.Constructor && node is BaseObjectCreationExpressionSyntax creation)
                return HasClosedMemoryBinaryWriterOrigin(creation, model, context);

            if (!IsMemoryBinaryWriterOperation(method) || node is not ExpressionSyntax expression) return false;

            ExpressionSyntax receiver = node is InvocationExpressionSyntax call
                && model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax instance }
                    ? instance : expression;

            return HasMemoryBinaryWriterProvenance(receiver, model, new(SymbolEqualityComparer.Default), context);
        }

        private static bool IsMemoryBinaryWriterOperation(IMethodSymbol method) =>
            !method.IsStatic && method.Arity == 0
            && (method is { Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true }
                || method is { Name: "Write", ReturnsVoid: true, Parameters: [IParameterSymbol { RefKind: RefKind.None } value] }
                    && (value.Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                            or SpecialType.System_Boolean or SpecialType.System_String
                        || value.Type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte }));

        private bool HasMemoryBinaryWriterProvenance(ExpressionSyntax expression, SemanticModel model,
            HashSet<ISymbol> path, AuthoredMember? context)
        {
            if (expression.DescendantNodesAndSelf().OfType<CastExpressionSyntax>().Any()) return false;

            expression = StripTransparentExpression(expression);

            if (model.GetSymbolInfo(expression).Symbol is IFieldSymbol or IPropertySymbol) return false;

            if (expression is BaseObjectCreationExpressionSyntax creation && context is not null)
                return HasClosedMemoryBinaryWriterOrigin(creation, model, context);

            return HasOnlyReviewedValueSources(expression, model, path, context, HasMemoryBinaryWriterProvenance);
        }

        private bool HasClosedMemoryBinaryWriterOrigin(BaseObjectCreationExpressionSyntax creation,
            SemanticModel model, AuthoredMember context)
        {
            if (model.GetOperation(creation) is not IObjectCreationOperation { Constructor: { } constructor } operation
                || !IsExactFrameworkType(constructor.ContainingType, "System.IO.BinaryWriter", typeof(BinaryWriter).Assembly.GetName())
                || !HasExactParameterTypes(constructor, "System.IO.Stream", "System.Text.Encoding", "System.Boolean")
                || creation.Initializer is not null
                || operation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0)?.Value.Syntax is not ExpressionSyntax stream
                || !HasExactBinaryBufferProvenance(stream, model, new(SymbolEqualityComparer.Default), context)
                || operation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 1)?.Value is not IPropertyReferenceOperation encoding
                || encoding.Property is not { IsStatic: true, Name: "UTF8", IsIndexer: false }
                || !IsExactFrameworkType(encoding.Property.ContainingType, "System.Text.Encoding", typeof(System.Text.Encoding).Assembly.GetName())
                || operation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 2)
                    is not { Value.ConstantValue: { HasValue: true, Value: true } }
                || creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
                || model.GetDeclaredSymbol(variable) is not ILocalSymbol writer
                || variable.Parent is not VariableDeclarationSyntax declaration
                || declaration.Parent is not UsingStatementSyntax
                    && declaration.Parent is not LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
                return false;

            return MemoryBinaryWriterUsesAreClosed(writer, context, new(SymbolEqualityComparer.Default));
        }

        private bool HasExactBinaryBufferProvenance(ExpressionSyntax expression, SemanticModel model,
            HashSet<ISymbol> path, AuthoredMember? context)
        {
            // Reuse the existing no-reassignment/ref/implicit-in checks. Its optional
            // authored bounded-buffer support does not carry into this exact-type proof.
            if (!HasClosedJsonBufferProvenance(expression, model, new(SymbolEqualityComparer.Default), context)) return false;

            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax creation)
                return model.GetOperation(creation) is IObjectCreationOperation { Constructor: { Parameters.Length: 0 } constructor }
                    && IsExactFrameworkType(constructor.ContainingType, "System.IO.MemoryStream", typeof(MemoryStream).Assembly.GetName())
                    && creation.Initializer is null;

            return HasOnlyReviewedValueSources(expression, model, path, context, HasExactBinaryBufferProvenance);
        }

        private bool MemoryBinaryWriterUsesAreClosed(ISymbol writer, AuthoredMember owner, HashSet<ISymbol> path)
        {
            if (!path.Add(writer)) return false;

            try
            {
                if (closedMemoryBinaryWriterUses.TryGetValue(owner, out Dictionary<ISymbol, bool>? cached)
                    && cached.TryGetValue(writer, out bool proved)) return proved;

                bool complete = ProveUses();

                if (!closedMemoryBinaryWriterUses.TryGetValue(owner, out cached))
                    closedMemoryBinaryWriterUses[owner] = cached = new(SymbolEqualityComparer.Default);

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
                        && IsExactFrameworkType(method.ContainingType, "System.IO.BinaryWriter", typeof(BinaryWriter).Assembly.GetName())
                        && IsMemoryBinaryWriterOperation(method)) continue;

                    if (reference.Parent is not ArgumentSyntax { RefKindKeyword.RawKind: 0, Parent.Parent: InvocationExpressionSyntax forwarded } argument
                        || owner.Model.GetOperation(argument) is not IArgumentOperation { Parameter: { RefKind: RefKind.None } parameter }
                        || owner.Model.GetSymbolInfo(forwarded).Symbol is not IMethodSymbol invoked
                        || invoked.MethodKind == MethodKind.DelegateInvoke)
                        return false;

                    AuthoredMember? target = ResolveInvocationTarget(invoked, owner, forwarded);

                    if (target is null || target.Symbol.IsAsync || !target.Symbol.ReturnsVoid
                        || parameter.Ordinal >= target.Symbol.Parameters.Length
                        || target.Symbol.Parameters[parameter.Ordinal].RefKind != RefKind.None) return false;

                    AuthoredMember bound = BindInvocationContext(owner, forwarded, target);

                    if (!MemoryBinaryWriterUsesAreClosed(target.Symbol.Parameters[parameter.Ordinal], bound, path)) return false;
                }

                return true;
            }
        }
    }
}
