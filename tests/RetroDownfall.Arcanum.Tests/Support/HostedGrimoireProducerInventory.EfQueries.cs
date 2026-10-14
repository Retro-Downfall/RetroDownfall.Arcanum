using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

// Entity Framework queries. Executing a query is a database access wherever it is written: enumerating a DbSet or an
// IQueryable built from one, a LINQ execution operator over it, an EF execution operator (ToListAsync, Load,
// ExecuteDelete, ...), Find, ExecuteSql, and any framework member that enumerates a sequence it was given. Each is a
// DatabaseAccess site, so it requires the Work frontier live at it, and it raises the interceptor callbacks of its
// context. An operand is an EF query only when it is provably built from a DbSet by exact framework operators; a lookalike
// from another assembly, an in-memory sequence and an unfollowable value are not, and keep the strict judgement they had.
//
// A query value is executed where it is consumed, so a value that is consumed anywhere the analysis cannot follow to its
// execution (an argument to an authored method, a return, a field, a capture, a tuple) is refused where it is built. That is
// what keeps a sequence-typed parameter, which the authored-enumerables premise stops at, from hiding a database query.
internal static partial class HostedGrimoireProducerInventory
{
    private static readonly IReadOnlySet<string> LinqQueryBuilders = new HashSet<string>(StringComparer.Ordinal)
    {
        "AsEnumerable",
        "AsQueryable",
        "Append",
        "Cast",
        "Chunk",
        "Concat",
        "DefaultIfEmpty",
        "Distinct",
        "DistinctBy",
        "Except",
        "ExceptBy",
        "GroupBy",
        "GroupJoin",
        "Intersect",
        "IntersectBy",
        "Join",
        "OfType",
        "Order",
        "OrderBy",
        "OrderByDescending",
        "OrderDescending",
        "Prepend",
        "Reverse",
        "Select",
        "SelectMany",
        "Skip",
        "SkipLast",
        "SkipWhile",
        "Take",
        "TakeLast",
        "TakeWhile",
        "ThenBy",
        "ThenByDescending",
        "Union",
        "UnionBy",
        "Where",
        "Zip",
    };

    private static readonly IReadOnlySet<string> EfQueryBuilders = new HashSet<string>(StringComparer.Ordinal)
    {
        "AsAsyncEnumerable",
        "AsNoTracking",
        "AsNoTrackingWithIdentityResolution",
        "AsSingleQuery",
        "AsSplitQuery",
        "AsTracking",
        "IgnoreAutoIncludes",
        "IgnoreQueryFilters",
        "Include",
        "TagWith",
        "TagWithCallSite",
        "ThenInclude",
    };

    // DbSet members that record state in the change tracker and run no query.
    private static readonly IReadOnlySet<string> DbSetNeutralMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Add",
        "AddAsync",
        "AddRange",
        "AddRangeAsync",
        "Attach",
        "AttachRange",
        "Remove",
        "RemoveRange",
        "Update",
        "UpdateRange",
    };

    private enum EfQueryCall : byte
    {
        None = 0,

        // Returns a query that still runs nothing: the operand is consumed by whatever consumes the result.
        Builder = 1,

        // Records state and runs no query.
        Neutral = 2,

        // Executes the query: a database access.
        Terminal = 3,
    }

    private sealed partial class ProducerGraph
    {
        private static bool IsEfDbSetType(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Class } named
            && IsExactFrameworkType(named, "Microsoft.EntityFrameworkCore.DbSet`1", typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName());

        private static bool IsEfAssembly(IAssemblySymbol? assembly) =>
            assembly is not null
            && (AssemblyIdentityMatches(assembly.Identity, typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName())
                || AssemblyIdentityMatches(assembly.Identity, typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly.GetName()));

        private static bool IsExactLinq(INamedTypeSymbol type, out bool queryable)
        {
            queryable = false;

            if (IsExactFrameworkType(type, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName()))
            {
                return true;
            }

            queryable = IsExactFrameworkType(type, "System.Linq.Queryable", typeof(Queryable).Assembly.GetName());

            return queryable;
        }

        // ---- What a call does with an EF query operand.

        /// <summary>
        /// Classifies a call that is given an EF query operand (or that executes SQL by its own identity). The identity
        /// of the method is judged by exact framework identity: a lookalike of another assembly is nothing. A framework
        /// member that is neither a known builder nor a known non-executing member and is given a query enumerates it,
        /// so it executes the query.
        /// </summary>
        private EfQueryCall ClassifyEfQueryCall(AuthoredMember member, SyntaxNode node, IMethodSymbol method, out List<ExpressionSyntax> operands)
        {
            operands = [];

            IMethodSymbol definition = method.ReducedFrom ?? method;

            if (definition.ContainingType is not { } declaring)
            {
                return EfQueryCall.None;
            }

            string key = TypeKey(declaring);

            // Executes SQL by its own identity: there is no query operand to follow.
            if (key == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"
                && IsEfAssembly(definition.ContainingAssembly)
                && definition.Name.StartsWith("ExecuteSql", StringComparison.Ordinal))
            {
                return EfQueryCall.Terminal;
            }

            if (key == "Microsoft.EntityFrameworkCore.DbContext"
                && IsEfAssembly(definition.ContainingAssembly)
                && definition.Name is "Find" or "FindAsync")
            {
                return EfQueryCall.Terminal;
            }

            if (member.Model.GetOperation(node) is not { } operation)
            {
                return EfQueryCall.None;
            }

            IEnumerable<IOperation> candidates = operation switch
            {
                IInvocationOperation invocation => (invocation.Instance is null ? Enumerable.Empty<IOperation>() : [invocation.Instance])
                    .Concat(invocation.Arguments.Select(static argument => argument.Value)),
                IObjectCreationOperation creation => creation.Arguments.Select(static argument => argument.Value),
                _ => [],
            };

            foreach (IOperation candidate in candidates)
            {
                if (candidate.Syntax is ExpressionSyntax expression
                    && !ReferenceEquals(expression, node)
                    && IsEfQueryOperand(member, expression, 0))
                {
                    operands.Add(expression);
                }
            }

            if (operands.Count == 0)
            {
                return EfQueryCall.None;
            }

            if (operation is IObjectCreationOperation)
            {
                // A framework constructor given a query enumerates it.
                return Resolve(definition, member.Model.Compilation) is null ? EfQueryCall.Terminal : EfQueryCall.None;
            }

            if (Resolve(definition, member.Model.Compilation) is not null)
            {
                // An authored method given a query is an escape, refused where the query was built.
                return EfQueryCall.None;
            }

            if (declaring.ContainingAssembly is { } assembly && IsExactLinq(declaring, out bool _))
            {
                return LinqQueryBuilders.Contains(definition.Name) ? EfQueryCall.Builder : EfQueryCall.Terminal;
            }

            if (IsEfAssembly(definition.ContainingAssembly))
            {
                if (key == "Microsoft.EntityFrameworkCore.DbSet`1")
                {
                    return DbSetNeutralMembers.Contains(definition.Name)
                        ? EfQueryCall.Neutral
                        : definition.Name is "AsAsyncEnumerable" or "AsQueryable" ? EfQueryCall.Builder : EfQueryCall.Terminal;
                }

                if (key == "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions")
                {
                    return EfQueryBuilders.Contains(definition.Name) ? EfQueryCall.Builder : EfQueryCall.Terminal;
                }

                if (key == "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions")
                {
                    return definition.Name.StartsWith("FromSql", StringComparison.Ordinal) || definition.Name is "AsSplitQuery" or "AsSingleQuery"
                        ? EfQueryCall.Builder
                        : EfQueryCall.Terminal;
                }
            }

            // Any other framework member given a query enumerates the sequence it was given.
            return EfQueryCall.Terminal;
        }

        private bool IsReviewedEfQueryOperator(IMethodSymbol method, SyntaxNode? node, SemanticModel? model, AuthoredMember? context) =>
            node is not null
            && context is not null
            && model is not null
            && ClassifyEfQueryCall(context, node, method, out _) == EfQueryCall.Builder;

        /// <summary>
        /// True when the expression is provably an EF query built in this member: a read of a DbSet the framework creates,
        /// a call to <c>DbContext.Set&lt;T&gt;()</c> on a closed context, a builder operator (LINQ or EF, by exact framework
        /// identity) over an EF query, or a local every value of which is one. A parameter, a field of another type and any
        /// unfollowable value are not provably an EF query, which is why a query that escapes the member that built it is
        /// refused where it was built.
        /// </summary>
        private bool IsEfQueryOperand(AuthoredMember member, ExpressionSyntax expression, int depth)
        {
            expression = StripTransparentExpression(expression);

            if (depth > 4 || !MayHoldEfQuery(member.Model.GetTypeInfo(expression).Type))
            {
                return false;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            switch (symbol)
            {
                case IPropertySymbol { IsIndexer: false } property when IsEfDbSetType(property.Type):
                    return IsFrameworkDbSetProperty(property);

                case IFieldSymbol field when IsEfDbSetType(field.Type):
                    return DbProviderTypesClosedByUse;

                case IMethodSymbol method when expression is InvocationExpressionSyntax call:
                    IMethodSymbol definition = method.ReducedFrom ?? method;

                    if (definition.Name == "Set"
                        && definition.IsGenericMethod
                        && TypeKey(definition.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext"
                        && IsEfAssembly(definition.ContainingAssembly))
                    {
                        return ReviewedProviderMemberIsClosed(definition, call, member.Model, member);
                    }

                    if (definition.Name is "SqlQuery" or "SqlQueryRaw" or "SqlQueryInterpolated"
                        && TypeKey(definition.ContainingType) == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"
                        && IsEfAssembly(definition.ContainingAssembly))
                    {
                        return true;
                    }

                    return Resolve(definition, member.Model.Compilation) is null
                        && ClassifyEfQueryCall(member, call, method, out List<ExpressionSyntax> operands) == EfQueryCall.Builder
                        && operands.Count != 0;

                case ILocalSymbol local when expression is IdentifierNameSyntax:
                    List<ExpressionSyntax> values = LocalQueryValues(member, local);

                    return values.Count != 0 && values.All(value => IsEfQueryOperand(member, value, depth + 1));

                default:
                    return false;
            }
        }

        // The static types a value can have and still be an EF query: a DbSet, or a sequence or queryable interface.
        private static bool MayHoldEfQuery(ITypeSymbol? type)
        {
            if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
            {
                return false;
            }

            if (IsEfDbSetType(type))
            {
                return true;
            }

            return type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named
                && !named.Locations.Any(static location => location.IsInSource)
                && TypeKey(named) is "System.Linq.IQueryable" or "System.Linq.IQueryable`1" or "System.Linq.IOrderedQueryable"
                    or "System.Linq.IOrderedQueryable`1" or "System.Collections.IEnumerable" or "System.Collections.Generic.IEnumerable`1"
                    or "System.Collections.Generic.IAsyncEnumerable`1";
        }

        // A DbSet property the framework creates: a property of EF itself, or an authored set property of a sealed context
        // that returns Set<T>() or that nothing authored assigns, or any DbSet when the provider types are closed.
        private bool IsFrameworkDbSetProperty(IPropertySymbol property) =>
            !IsAuthoredSymbol(property)
                ? IsEfAssembly(property.ContainingAssembly)
                : DbProviderTypesClosedByUse || IsFrameworkSetProperty(property);

        // Every value a local holds: its initializer and the right-hand side of each assignment to it, within the function
        // that declares it (the member, or the lambda or local function it is declared in). A local that is passed by
        // reference, deconstructed into or captured by a function nested in that scope is not a plain holder.
        private List<ExpressionSyntax> LocalQueryValues(AuthoredMember member, ILocalSymbol local)
        {
            List<ExpressionSyntax> values = [];

            if (local.DeclaringSyntaxReferences is not [{ } reference]
                || reference.GetSyntax() is not VariableDeclaratorSyntax declarator
                || declarator.Initializer?.Value is not { } initializer
                || declarator.Ancestors().FirstOrDefault(static ancestor => ancestor is MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) is not { } scope)
            {
                return values;
            }

            values.Add(initializer);

            foreach (IdentifierNameSyntax name in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.ValueText != local.Name
                    || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, local))
                {
                    continue;
                }

                if (name.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, name))
                {
                    if (assignment.RawKind != (int)SyntaxKind.SimpleAssignmentExpression)
                    {
                        return [];
                    }

                    values.Add(assignment.Right);
                }
                else if (name.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }
                    || name.Ancestors().TakeWhile(ancestor => ancestor != scope).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    return [];
                }
            }

            return values;
        }

        // The context a query is built from: the owner of the DbSet read or Set<T>() call at the root of its builder chain.
        private (AuthoredMember Caller, ExpressionSyntax Context)? EfQueryRootContext(AuthoredMember member, ExpressionSyntax operand, int depth)
        {
            operand = StripTransparentExpression(operand);

            if (depth > 4)
            {
                return null;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(operand).Symbol;

            switch (symbol)
            {
                case IPropertySymbol or IFieldSymbol:
                    return operand switch
                    {
                        MemberAccessExpressionSyntax access => (member, access.Expression),
                        IdentifierNameSyntax when symbol.ContainingType is { } owner && IsExactEfDbContext(owner) => (member, SyntaxFactory.ThisExpression()),
                        _ => null,
                    };

                case IMethodSymbol method when operand is InvocationExpressionSyntax call:
                    IMethodSymbol definition = method.ReducedFrom ?? method;

                    if (member.Model.GetOperation(call) is not IInvocationOperation invocation)
                    {
                        return null;
                    }

                    if (definition.Name == "Set" && TypeKey(definition.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext")
                    {
                        return invocation.Instance?.Syntax is ExpressionSyntax owner
                            ? (member, owner)
                            : member.Symbol.ContainingType is { } implied && IsExactEfDbContext(implied) ? (member, SyntaxFactory.ThisExpression()) : null;
                    }

                    if (definition.Name is "SqlQuery" or "SqlQueryRaw" or "SqlQueryInterpolated"
                        && ExactInvocationReceiverOrFirstArgument(invocation) is { } facade)
                    {
                        List<(AuthoredMember Caller, ExpressionSyntax Context)> contexts = [];

                        return FacadeContexts(member, facade, contexts, 0) && contexts.Count == 1 ? contexts[0] : null;
                    }

                    foreach (IOperation candidate in (invocation.Instance is null ? Enumerable.Empty<IOperation>() : [invocation.Instance]).Concat(invocation.Arguments.Select(static argument => argument.Value)))
                    {
                        if (candidate.Syntax is ExpressionSyntax source
                            && !ReferenceEquals(source, operand)
                            && IsEfQueryOperand(member, source, 0))
                        {
                            return EfQueryRootContext(member, source, depth + 1);
                        }
                    }

                    return null;

                case ILocalSymbol local when operand is IdentifierNameSyntax:
                    List<ExpressionSyntax> held = LocalQueryValues(member, local);

                    return held.Count == 0 ? null : EfQueryRootContext(member, held[0], depth + 1);

                default:
                    return null;
            }
        }

        // ---- A query consumed where it is built.

        /// <summary>
        /// True when the value of a query-producing expression is consumed at a place the analysis can follow to its
        /// execution: a builder, a terminal or a non-executing DbSet member; a loop or spread; a discard; or a local whose
        /// every use is such a consumption. The one return it allows is the context's own `DbSet X =&gt; Set&lt;T&gt;()`
        /// property, whose value reaches the read that consumes it.
        /// </summary>
        private bool EfQueryValueIsConsumed(AuthoredMember member, ExpressionSyntax node, int depth)
        {
            if (depth > 4)
            {
                return false;
            }

            SyntaxNode? parent = node.Parent;

            ExpressionSyntax current = node;

            while (parent is ParenthesizedExpressionSyntax or CastExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression }
                || parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression })
            {
                current = (ExpressionSyntax)parent;

                parent = current.Parent;
            }

            switch (parent)
            {
                case MemberAccessExpressionSyntax access when ReferenceEquals(access.Expression, current):
                    return access.Parent is InvocationExpressionSyntax receiverCall
                        && ConsumingEfQueryCall(member, receiverCall, current, depth);

                case ConditionalAccessExpressionSyntax conditional when ReferenceEquals(conditional.Expression, current):
                    return conditional.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } bound
                        && ConsumingEfQueryCall(member, bound, current, depth);

                case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax } list } argument
                    when ReferenceEquals(argument.Expression, current):
                    return list.Parent is InvocationExpressionSyntax argumentCall
                        ? ConsumingEfQueryCall(member, argumentCall, current, depth)
                        : list.Parent is BaseObjectCreationExpressionSyntax creation && ConsumingEfQueryCall(member, creation, current, depth);

                case CommonForEachStatementSyntax loop when ReferenceEquals(loop.Expression, current):
                    return true;

                case SpreadElementSyntax spread when ReferenceEquals(spread.Expression, current):
                    return true;

                case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment when ReferenceEquals(assignment.Right, current):
                    return assignment.Left switch
                    {
                        IdentifierNameSyntax { Identifier.ValueText: "_" } => true,
                        IdentifierNameSyntax local when member.Model.GetSymbolInfo(local).Symbol is ILocalSymbol localSymbol => EfQueryLocalIsConsumed(member, localSymbol, depth),
                        _ => false,
                    };

                case ExpressionStatementSyntax:
                    return true;

                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } when member.Model.GetDeclaredSymbol(declarator) is ILocalSymbol declared:
                    return EfQueryLocalIsConsumed(member, declared, depth);

                case ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                    return IsFrameworkSetGetter(member, property);

                case ReturnStatementSyntax { Parent: BlockSyntax { Parent: AccessorDeclarationSyntax { Parent: AccessorListSyntax { Parent: PropertyDeclarationSyntax getterProperty } } } }:
                    return IsFrameworkSetGetter(member, getterProperty);

                default:
                    return false;
            }
        }

        // The context's own set property: `public DbSet<T> Name => Set<T>();` (or a getter that returns it) of an
        // authored EF context. The set reaches the read of the property, which is judged where it is consumed.
        private bool IsFrameworkSetGetter(AuthoredMember member, PropertyDeclarationSyntax property) =>
            member.Model.GetDeclaredSymbol(property) is IPropertySymbol symbol
            && IsEfDbSetType(symbol.Type)
            && symbol.ContainingType is { } owner
            && IsExactEfDbContext(owner)
            && IsAuthoredSymbol(owner);

        // The call that consumes a query value: a recognized builder, terminal or non-executing member. A builder's result
        // is a query value in its own right and is judged where it is consumed.
        private bool ConsumingEfQueryCall(AuthoredMember member, SyntaxNode call, ExpressionSyntax operand, int depth)
        {
            if (member.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol method)
            {
                return false;
            }

            EfQueryCall kind = ClassifyEfQueryCall(member, call, method, out List<ExpressionSyntax> operands);

            return kind != EfQueryCall.None && operands.Any(candidate => ReferenceEquals(StripTransparentExpression(candidate), StripTransparentExpression(operand)));
        }

        // A local is consumed when it is assigned once from the query and every other reference is itself a consumption,
        // never captured by a nested function and never passed by reference.
        private bool EfQueryLocalIsConsumed(AuthoredMember member, ILocalSymbol local, int depth)
        {
            if (local.DeclaringSyntaxReferences is not [{ } declaration]
                || declaration.GetSyntax().Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() is not { } scopeRoot)
            {
                return false;
            }

            foreach (IdentifierNameSyntax reference in scopeRoot.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (reference.Identifier.ValueText != local.Name
                    || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(reference).Symbol, local))
                {
                    continue;
                }

                if (reference.Ancestors().TakeWhile(ancestor => ancestor != scopeRoot).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                    || reference.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 })
                {
                    return false;
                }

                // The declaration and the assignments of a query are the producing side.
                if (reference.Parent is VariableDeclaratorSyntax || reference.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, reference))
                {
                    if (reference.Parent is AssignmentExpressionSyntax written
                        && !(StripTransparentExpression(written.Right) is { } assigned && IsEfQueryOperand(member, assigned, depth + 1)))
                    {
                        return false;
                    }

                    continue;
                }

                if (!EfQueryValueIsConsumed(member, reference, depth + 1))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Refuses a query value that is consumed where the analysis cannot follow it to its execution, naming the
        /// expression. Called at every expression that builds an EF query.
        /// </summary>
        private void RefuseEscapingEfQuery(AuthoredMember member, ExpressionSyntax node, string description)
        {
            if (EfQueryValueIsConsumed(member, node, 0))
            {
                return;
            }

            RefuseTraversalCompletion();

            diagnostics.Add(new(
                "HOSTED_SITE_UNCLASSIFIED",
                Location(node),
                description + "; an EF query value must be consumed where it is built, because its execution cannot be followed from anywhere else."));
        }

        // ---- The database site an executed query is.

        /// <summary>
        /// Called for a framework call that executes an EF query: the operands' root context supplies the interceptors it
        /// raises. A context nobody can follow is refused by the callback traversal.
        /// </summary>
        private TraversalEvidence TraverseEfQueryCallbacks(
            AuthoredMember member,
            SyntaxNode node,
            IReadOnlyList<ExpressionSyntax> operands,
            IMethodSymbol? definition,
            string callee,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? effectGroup,
            string? recoveryEffect)
        {
            List<(AuthoredMember Caller, ExpressionSyntax Context)> contexts = [];

            if (operands.Count == 0)
            {
                // ExecuteSql and DbContext.Find name the context by their own receiver.
                if (node is InvocationExpressionSyntax call && definition is not null && EfOperationContexts(member, call, definition) is { } named)
                {
                    contexts = named;
                }
            }
            else
            {
                foreach (ExpressionSyntax operand in operands)
                {
                    if (EfQueryRootContext(member, operand, 0) is { } root)
                    {
                        contexts.Add(root);
                    }
                }
            }

            return TraverseInterceptorsForOperation(
                member,
                node,
                contexts.Count == 0 ? null : contexts,
                EfInterception.Connection | EfInterception.ConnectionCreation | EfInterception.Command | EfInterception.Query,
                callee,
                rootType,
                operationId,
                active,
                lifecycle,
                workAdmitted,
                effectGroup,
                recoveryEffect);
        }
    }
}
