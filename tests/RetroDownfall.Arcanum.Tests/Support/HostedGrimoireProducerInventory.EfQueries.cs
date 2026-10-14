using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

// Entity Framework queries. Executing a query is a database access wherever it is written: enumerating a DbSet or an
// IQueryable built from one, a LINQ execution operator over it, an EF execution operator (ToListAsync, Load,
// ExecuteDelete, ...), Find, ExecuteSql, and the reviewed framework members that eagerly enumerate a sequence they are
// given. Each is a DatabaseAccess site, so it requires the Work frontier live at it, and it raises the interceptor
// callbacks of every context the query is built from.
//
// A query is followed from the expression that creates it (a DbSet read, Set<T>(), SqlQuery) through the operators that
// build on it, each of which returns a query value whatever its static type, to the one that executes it. The operators
// are classified by exact framework identity and by a reviewed name table that is checked against the assemblies
// (EfOperatorVerdict): an operator the table does not name is not guessed at, and neither is a lookalike of another
// assembly. A lazy operator runs nothing where it wraps the query, so the execution is judged where its result is consumed.
//
// A query value is executed where it is consumed, so a value that is consumed anywhere the analysis cannot follow to its
// execution (an argument to an authored method, a member that stores or defers it, a return, a field, a capture, a tuple)
// is refused where it is built. That is what keeps a sequence-typed value, which the authored-enumerables premise stops
// at, from hiding a database query.
internal static partial class HostedGrimoireProducerInventory
{
    // The LINQ operators whose result is a lazy sequence over their source: they run nothing where they are called.
    private static readonly IReadOnlySet<string> LinqLazyOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "AggregateBy",
        "Append",
        "AsEnumerable",
        "AsQueryable",
        "Cast",
        "Chunk",
        "Concat",
        "CountBy",
        "DefaultIfEmpty",
        "Distinct",
        "DistinctBy",
        "Except",
        "ExceptBy",
        "GroupBy",
        "GroupJoin",
        "Index",
        "Intersect",
        "IntersectBy",
        "Join",
        "LeftJoin",
        "OfType",
        "Order",
        "OrderBy",
        "OrderByDescending",
        "OrderDescending",
        "Prepend",
        "Reverse",
        "RightJoin",
        "Select",
        "SelectMany",
        "Shuffle",
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

    // The LINQ operators that enumerate their source before they return.
    private static readonly IReadOnlySet<string> LinqEagerOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "Aggregate",
        "All",
        "Any",
        "Average",
        "Contains",
        "Count",
        "ElementAt",
        "ElementAtOrDefault",
        "First",
        "FirstOrDefault",
        "Last",
        "LastOrDefault",
        "LongCount",
        "Max",
        "MaxBy",
        "Min",
        "MinBy",
        "SequenceEqual",
        "Single",
        "SingleOrDefault",
        "Sum",
        "ToArray",
        "ToDictionary",
        "ToHashSet",
        "ToList",
        "ToLookup",
    };

    // Generators and probes: they take no source sequence, or they inspect one without running it. A query handed to one
    // of them is not followed.
    private static readonly IReadOnlySet<string> EnumerableRefusedOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "Empty",
        "InfiniteSequence",
        "Range",
        "Repeat",
        "Sequence",
        "TryGetNonEnumeratedCount",
    };

    private static readonly IReadOnlySet<string> AsyncLinqEagerOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "AggregateAsync",
        "AllAsync",
        "AnyAsync",
        "AverageAsync",
        "ContainsAsync",
        "CountAsync",
        "ElementAtAsync",
        "ElementAtOrDefaultAsync",
        "FirstAsync",
        "FirstOrDefaultAsync",
        "LastAsync",
        "LastOrDefaultAsync",
        "LongCountAsync",
        "MaxAsync",
        "MaxByAsync",
        "MinAsync",
        "MinByAsync",
        "SequenceEqualAsync",
        "SingleAsync",
        "SingleOrDefaultAsync",
        "SumAsync",
        "ToArrayAsync",
        "ToDictionaryAsync",
        "ToHashSetAsync",
        "ToListAsync",
        "ToLookupAsync",
    };

    private static readonly IReadOnlySet<string> AsyncLinqRefusedOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "Empty",
        "InfiniteSequence",
        "Range",
        "Repeat",
        "Sequence",
    };

    private static readonly IReadOnlySet<string> EfQueryBuilders = new HashSet<string>(StringComparer.Ordinal)
    {
        "AsAsyncEnumerable",
        "AsNoTracking",
        "AsNoTrackingWithIdentityResolution",
        "AsTracking",
        "IgnoreAutoIncludes",
        "IgnoreQueryFilters",
        "Include",
        "TagWith",
        "TagWithCallSite",
        "ThenInclude",
    };

    // The reviewed verdict of one operator of the query classes, by the metadata key of its class. "Unknown" is a name the
    // table has never reviewed: the census fixture reads every public operator of the classes from the assemblies and
    // fails on one, so a member a later framework version adds cannot be silently classed as none of these.
    internal static string EfOperatorVerdict(string typeKey, string name)
    {
        switch (typeKey)
        {
            case "System.Linq.Enumerable":
                return LinqLazyOperators.Contains(name)
                    ? "Builder"
                    : LinqEagerOperators.Contains(name)
                        ? "Terminal"
                        : EnumerableRefusedOperators.Contains(name) ? "Refused" : "Unknown";

            case "System.Linq.Queryable":
                return LinqLazyOperators.Contains(name) ? "Builder" : LinqEagerOperators.Contains(name) ? "Terminal" : "Unknown";

            case "System.Linq.AsyncEnumerable":
                return LinqLazyOperators.Contains(name) || name == "ToAsyncEnumerable"
                    ? "Builder"
                    : AsyncLinqEagerOperators.Contains(name)
                        ? "Terminal"
                        : AsyncLinqRefusedOperators.Contains(name) ? "Refused" : "Unknown";

            case "System.Threading.Tasks.TaskAsyncEnumerableExtensions":
                return name is "ConfigureAwait" or "WithCancellation" ? "Builder" : name == "ToBlockingEnumerable" ? "Refused" : "Unknown";

            case "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions":
                return EfQueryBuilders.Contains(name)
                    ? "Builder"
                    : name == "ToQueryString"
                        ? "Refused"
                        : name.EndsWith("Async", StringComparison.Ordinal) || name is "ExecuteDelete" or "ExecuteUpdate" or "Load" ? "Terminal" : "Unknown";

            case "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions":
                return name is "AsSingleQuery" or "AsSplitQuery" or "FromSql" or "FromSqlInterpolated" or "FromSqlRaw"
                    ? "Builder"
                    : name == "CreateDbCommand" ? "Refused" : "Unknown";

            default:
                return "Unknown";
        }
    }

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

    // The parameter types in which a framework member receives a sequence it will enumerate or build on. A query handed to
    // any other parameter (an element, an object, a type parameter) is stored or handed on, not enumerated by the member.
    private static readonly IReadOnlySet<string> SequenceSlotTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Collections.IEnumerable",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IAsyncEnumerable`1",
        "System.Linq.IQueryable",
        "System.Linq.IQueryable`1",
        "System.Linq.IOrderedQueryable`1",
        "System.Linq.IOrderedEnumerable`1",
        "System.Linq.IOrderedAsyncEnumerable`1",
        "Microsoft.EntityFrameworkCore.Query.IIncludableQueryable`2",
        "Microsoft.EntityFrameworkCore.DbSet`1",
    };

    // Framework members that enumerate a sequence they are given before they return, reviewed by exact type and member.
    private static readonly (Type Runtime, string Key, string[] Members)[] EagerEnumeratingMembers =
    [
        (typeof(List<>), "System.Collections.Generic.List`1", [".ctor", "AddRange", "InsertRange"]),
        (typeof(HashSet<>), "System.Collections.Generic.HashSet`1", [".ctor", "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith"]),
        (typeof(SortedSet<>), "System.Collections.Generic.SortedSet`1", [".ctor", "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith"]),
        (typeof(Queue<>), "System.Collections.Generic.Queue`1", [".ctor"]),
        (typeof(Stack<>), "System.Collections.Generic.Stack`1", [".ctor"]),
        (typeof(LinkedList<>), "System.Collections.Generic.LinkedList`1", [".ctor"]),
        (typeof(string), "System.String", ["Join", "Concat"]),
    ];

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

        // True when the type is exactly the framework operator class the key names, by assembly identity.
        private static bool IsExactEfOperatorClass(INamedTypeSymbol type, string key) => key switch
        {
            "System.Linq.Enumerable" => IsExactFrameworkType(type, key, typeof(Enumerable).Assembly.GetName()),
            "System.Linq.Queryable" => IsExactFrameworkType(type, key, typeof(Queryable).Assembly.GetName()),
            "System.Linq.AsyncEnumerable" => IsExactFrameworkType(type, key, typeof(System.Linq.AsyncEnumerable).Assembly.GetName()),
            "System.Threading.Tasks.TaskAsyncEnumerableExtensions" => IsExactFrameworkType(type, key, typeof(System.Threading.Tasks.TaskAsyncEnumerableExtensions).Assembly.GetName()),
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions" => IsExactFrameworkType(type, key, typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions).Assembly.GetName()),
            "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions" => IsExactFrameworkType(type, key, typeof(Microsoft.EntityFrameworkCore.RelationalQueryableExtensions).Assembly.GetName()),
            _ => false,
        };

        private static bool IsSequenceSlot(IParameterSymbol? parameter) =>
            parameter?.OriginalDefinition.Type is INamedTypeSymbol named
            && SequenceSlotTypes.Contains(TypeKey(named));

        // A reviewed framework member that enumerates the sequence it is given before it returns.
        private static bool IsEagerEnumeratingFrameworkMember(IMethodSymbol definition)
        {
            if (definition.ContainingType is not { } declaring)
            {
                return false;
            }

            string key = TypeKey(declaring);

            foreach ((Type runtime, string expected, string[] members) in EagerEnumeratingMembers)
            {
                if (key == expected
                    && members.Contains(definition.Name, StringComparer.Ordinal)
                    && IsExactFrameworkType(declaring, expected, runtime.Assembly.GetName()))
                {
                    return true;
                }
            }

            return false;
        }

        // ---- What a call does with an EF query operand.

        /// <summary>
        /// Classifies a call that is given an EF query operand (or that executes SQL by its own identity). The identity of
        /// the method is judged by exact framework identity: a lookalike of another assembly is nothing. Only an operator
        /// the reviewed tables name executes or builds on the query: anything else that is handed one (a member that
        /// stores it, defers it, takes it as an element, or hands back its enumerator) is none of these, so the query
        /// is not consumed and is refused where it was built.
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

            // An authored member is followed into, never classified here: a query handed to one is an escape.
            if (IsAuthoredSymbol(definition) || member.Model.GetOperation(node) is not { } operation)
            {
                return EfQueryCall.None;
            }

            List<(ExpressionSyntax Expression, IParameterSymbol? Parameter)> found = [];

            switch (operation)
            {
                case IInvocationOperation invocation:
                    if (invocation.Instance?.Syntax is ExpressionSyntax instance
                        && !ReferenceEquals(instance, node)
                        && IsEfQueryOperand(member, instance))
                    {
                        found.Add((instance, null));
                    }

                    foreach (IArgumentOperation argument in invocation.Arguments)
                    {
                        if (argument.Value.Syntax is ExpressionSyntax value
                            && !ReferenceEquals(value, node)
                            && IsEfQueryOperand(member, value))
                        {
                            found.Add((value, argument.Parameter));
                        }
                    }

                    break;

                case IObjectCreationOperation creation:
                    foreach (IArgumentOperation argument in creation.Arguments)
                    {
                        if (argument.Value.Syntax is ExpressionSyntax value
                            && !ReferenceEquals(value, node)
                            && IsEfQueryOperand(member, value))
                        {
                            found.Add((value, argument.Parameter));
                        }
                    }

                    break;

                default:
                    return EfQueryCall.None;
            }

            if (found.Count == 0)
            {
                return EfQueryCall.None;
            }

            operands = [.. found.Select(static operand => operand.Expression)];

            // A query handed to a parameter that is not a sequence is stored or passed on, never enumerated by the member.
            if (found.Any(static operand => operand.Parameter is not null && !IsSequenceSlot(operand.Parameter)))
            {
                return EfQueryCall.None;
            }

            if (operation is IObjectCreationOperation)
            {
                // A reviewed framework constructor given a sequence enumerates it.
                return IsEagerEnumeratingFrameworkMember(definition) ? EfQueryCall.Terminal : EfQueryCall.None;
            }

            if (found.Any(static operand => operand.Parameter is null))
            {
                // The query is the receiver of an instance member: only a DbSet is reviewed as a receiver.
                if (key != "Microsoft.EntityFrameworkCore.DbSet`1" || !IsEfAssembly(definition.ContainingAssembly))
                {
                    return EfQueryCall.None;
                }

                return DbSetNeutralMembers.Contains(definition.Name)
                    ? EfQueryCall.Neutral
                    : definition.Name is "Find" or "FindAsync"
                        ? EfQueryCall.Terminal
                        : definition.Name is "AsAsyncEnumerable" or "AsQueryable" ? EfQueryCall.Builder : EfQueryCall.None;
            }

            if (IsExactEfOperatorClass(declaring, key))
            {
                return EfOperatorVerdict(key, definition.Name) switch
                {
                    "Builder" => EfQueryCall.Builder,
                    "Terminal" => EfQueryCall.Terminal,
                    _ => EfQueryCall.None,
                };
            }

            return IsEagerEnumeratingFrameworkMember(definition) ? EfQueryCall.Terminal : EfQueryCall.None;
        }

        private bool IsReviewedEfQueryOperator(IMethodSymbol method, SyntaxNode? node, SemanticModel? model, AuthoredMember? context) =>
            node is not null
            && context is not null
            && model is not null
            && ClassifyEfQueryCall(context, node, method, out _) == EfQueryCall.Builder;

        // The locals whose values are being judged: a local that is its own builder's operand is a query if its other values
        // are, so a cycle through it assumes it is one.
        private readonly HashSet<ILocalSymbol> efQueryLocalsInProgress = new(SymbolEqualityComparer.Default);

        private int efQueryDepth;

        // Deep enough for any chain a person writes. Beyond it the operand is not provably a query, which the escape
        // refusal at the query's root covers.
        private const int MaximumEfQueryDepth = 48;

        /// <summary>
        /// True when the expression is provably an EF query built in this member: a read of a DbSet the framework creates,
        /// a call to <c>DbContext.Set&lt;T&gt;()</c> on a closed context, a builder operator (LINQ or EF, by exact framework
        /// identity) over an EF query, whatever type it returns, or a local every value of which is one. A parameter, a
        /// field of another type and any unfollowable value are not provably an EF query, which is why a query that escapes
        /// the member that built it is refused where it was built.
        /// </summary>
        private bool IsEfQueryOperand(AuthoredMember member, ExpressionSyntax expression)
        {
            expression = StripTransparentExpression(expression);

            if (efQueryDepth >= MaximumEfQueryDepth)
            {
                return false;
            }

            efQueryDepth++;

            try
            {
                return IsEfQueryOperandCore(member, expression);
            }
            finally
            {
                efQueryDepth--;
            }
        }

        private bool IsEfQueryOperandCore(AuthoredMember member, ExpressionSyntax expression)
        {
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
                    if (!LocalMayHoldEfQuery(local.Type))
                    {
                        return false;
                    }

                    if (efQueryLocalsInProgress.Contains(local))
                    {
                        return true;
                    }

                    List<ExpressionSyntax> values = LocalQueryValues(member, local);

                    if (values.Count == 0)
                    {
                        return false;
                    }

                    _ = efQueryLocalsInProgress.Add(local);

                    try
                    {
                        return values.All(value => IsEfQueryOperand(member, value));
                    }
                    finally
                    {
                        _ = efQueryLocalsInProgress.Remove(local);
                    }

                default:
                    return false;
            }
        }

        // A local can hold a query only when its type is one a query value converts to: object, a type parameter or dynamic, a
        // sequence or queryable interface, a DbSet, the struct the async configuration operators return, or the list-source
        // interface a DbSet implements. A local of any other type (a context, a string, a collection that was materialized)
        // holds none, and a query that flows into one is not consumed by anything the analysis follows, so its root is refused.
        private static bool LocalMayHoldEfQuery(ITypeSymbol? type) => type switch
        {
            null => false,
            ITypeParameterSymbol => true,
            { TypeKind: TypeKind.Dynamic } => true,
            INamedTypeSymbol named => named.SpecialType == SpecialType.System_Object
                || SequenceSlotTypes.Contains(TypeKey(named))
                || TypeKey(named) is "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1" or "System.ComponentModel.IListSource" or "System.ValueType",
            _ => false,
        };

        // A DbSet property the framework creates: a property of EF itself, or an authored set property of a sealed context
        // that returns Set<T>() or that nothing authored assigns, or any DbSet when the provider types are closed.
        private bool IsFrameworkDbSetProperty(IPropertySymbol property) =>
            !IsAuthoredSymbol(property)
                ? IsEfAssembly(property.ContainingAssembly)
                : DbProviderTypesClosedByUse || IsFrameworkSetProperty(property);

        // How one reference to a local is used.
        private enum LocalUse : byte
        {
            // A read of the value, which does not change what the local holds.
            Read = 0,

            // A plain assignment `local = value`, whose right-hand side is the value.
            Assignment = 1,

            // Any other way the local can be written or aliased: a compound assignment, a deconstruction target, a ref, out
            // or in argument, a ref alias, an increment, an address-of. What it then holds cannot be named.
            Opaque = 2,
        }

        private static LocalUse ClassifyLocalUse(IdentifierNameSyntax name, out ExpressionSyntax? assigned)
        {
            assigned = null;

            switch (name.Parent)
            {
                case AssignmentExpressionSyntax assignment when ReferenceEquals(assignment.Left, name):
                    if (assignment.RawKind != (int)SyntaxKind.SimpleAssignmentExpression)
                    {
                        return LocalUse.Opaque;
                    }

                    assigned = assignment.Right;

                    return LocalUse.Assignment;

                case ArgumentSyntax argument:
                    if (argument.RefKindKeyword.RawKind != 0)
                    {
                        return LocalUse.Opaque;
                    }

                    // An element of a tuple that is the target of a deconstruction.
                    return argument.Parent is TupleExpressionSyntax tuple && IsDeconstructionTarget(tuple) ? LocalUse.Opaque : LocalUse.Read;

                case RefExpressionSyntax:
                    return LocalUse.Opaque;

                case PrefixUnaryExpressionSyntax prefix when prefix.RawKind is (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression or (int)SyntaxKind.AddressOfExpression:
                    return LocalUse.Opaque;

                case PostfixUnaryExpressionSyntax postfix when postfix.RawKind is (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression:
                    return LocalUse.Opaque;

                default:
                    return LocalUse.Read;
            }
        }

        private static bool IsDeconstructionTarget(TupleExpressionSyntax tuple)
        {
            while (tuple.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax outer })
            {
                tuple = outer;
            }

            return tuple.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, tuple);
        }

        // Every value a local holds: its initializer and the right-hand side of each assignment to it, within the function
        // that declares it (the member, or the lambda or local function it is declared in). A local that is written in any
        // other way (a compound assignment, a deconstruction, a ref, out or in argument, a ref alias), or is mentioned by a
        // function nested in that scope, is not a plain holder, and holds nothing nameable.
        private List<ExpressionSyntax> LocalQueryValues(AuthoredMember member, ILocalSymbol local)
        {
            List<ExpressionSyntax> values = [];

            if (local.IsRef
                || local.DeclaringSyntaxReferences is not [{ } reference]
                || reference.GetSyntax() is not VariableDeclaratorSyntax declarator
                || declarator.Initializer?.Value is RefExpressionSyntax
                || declarator.Ancestors().FirstOrDefault(static ancestor => ancestor is MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) is not { } scope)
            {
                return values;
            }

            if (declarator.Initializer?.Value is { } initializer)
            {
                values.Add(initializer);
            }

            foreach (IdentifierNameSyntax name in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.ValueText != local.Name
                    || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, local))
                {
                    continue;
                }

                if (name.Ancestors().TakeWhile(ancestor => ancestor != scope).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    return [];
                }

                switch (ClassifyLocalUse(name, out ExpressionSyntax? assigned))
                {
                    case LocalUse.Opaque:
                        return [];

                    case LocalUse.Assignment:
                        values.Add(assigned!);

                        break;
                }
            }

            return values;
        }

        // ---- The contexts a query is built from.

        // The static type is a DbContext, or a type parameter bounded by one.
        private bool IsContextTyped(AuthoredMember member, ExpressionSyntax owner)
        {
            if (ContextBound(member.Model.GetTypeInfo(owner).Type) is not { TypeKind: TypeKind.Class } bound)
            {
                return false;
            }

            return IsExactEfDbContext(bound)
                || TypeKey(bound) == "Microsoft.EntityFrameworkCore.DbContext" && IsEfAssembly(bound.ContainingAssembly);
        }

        /// <summary>
        /// Collects every context expression a query is built from: the owner of each DbSet read or <c>Set&lt;T&gt;()</c> call
        /// at the root of its builder chain, for every operand of every builder and every value of every local. False when any
        /// root cannot be followed to a context (a set read through a holder that is no context, a facade nobody can name),
        /// so the callbacks it would raise are unknown.
        /// </summary>
        private bool EfQueryRoots(AuthoredMember member, ExpressionSyntax operand, List<(AuthoredMember Caller, ExpressionSyntax Context)> roots)
        {
            operand = StripTransparentExpression(operand);

            if (efQueryDepth >= MaximumEfQueryDepth)
            {
                return false;
            }

            efQueryDepth++;

            try
            {
                return EfQueryRootsCore(member, operand, roots);
            }
            finally
            {
                efQueryDepth--;
            }
        }

        private bool EfQueryRootsCore(AuthoredMember member, ExpressionSyntax operand, List<(AuthoredMember Caller, ExpressionSyntax Context)> roots)
        {
            ISymbol? symbol = member.Model.GetSymbolInfo(operand).Symbol;

            switch (symbol)
            {
                case IPropertySymbol { IsIndexer: false } or IFieldSymbol:
                    switch (operand)
                    {
                        case MemberAccessExpressionSyntax access when IsContextTyped(member, access.Expression):
                            roots.Add((member, access.Expression));

                            return true;

                        case IdentifierNameSyntax when !member.Symbol.IsStatic && IsExactEfDbContext(member.Symbol.ContainingType):
                            roots.Add((member, SyntaxFactory.ThisExpression()));

                            return true;

                        default:
                            return false;
                    }

                case IMethodSymbol method when operand is InvocationExpressionSyntax call:
                    IMethodSymbol definition = method.ReducedFrom ?? method;

                    if (member.Model.GetOperation(call) is not IInvocationOperation invocation)
                    {
                        return false;
                    }

                    if (definition.Name == "Set" && TypeKey(definition.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext")
                    {
                        if (invocation.Instance is { IsImplicit: false, Syntax: ExpressionSyntax owner })
                        {
                            if (!IsContextTyped(member, owner))
                            {
                                return false;
                            }

                            roots.Add((member, owner));

                            return true;
                        }

                        // `Set<T>()` inside a context is the context's own.
                        if (invocation.Instance is null or { IsImplicit: true }
                            && !member.Symbol.IsStatic
                            && IsExactEfDbContext(member.Symbol.ContainingType))
                        {
                            roots.Add((member, SyntaxFactory.ThisExpression()));

                            return true;
                        }

                        return false;
                    }

                    if (definition.Name is "SqlQuery" or "SqlQueryRaw" or "SqlQueryInterpolated"
                        && ExactInvocationReceiverOrFirstArgument(invocation) is { } facade)
                    {
                        List<(AuthoredMember Caller, ExpressionSyntax Context)> facadeContexts = [];

                        if (!FacadeContexts(member, facade, facadeContexts, 0) || facadeContexts.Count == 0)
                        {
                            return false;
                        }

                        roots.AddRange(facadeContexts);

                        return true;
                    }

                    // A builder: every query operand it was given contributes its roots.
                    if (ClassifyEfQueryCall(member, call, method, out List<ExpressionSyntax> builderOperands) != EfQueryCall.Builder || builderOperands.Count == 0)
                    {
                        return false;
                    }

                    foreach (ExpressionSyntax builderOperand in builderOperands)
                    {
                        if (!EfQueryRoots(member, builderOperand, roots))
                        {
                            return false;
                        }
                    }

                    return true;

                case ILocalSymbol local when operand is IdentifierNameSyntax:
                    if (efQueryLocalsInProgress.Contains(local))
                    {
                        return true;
                    }

                    List<ExpressionSyntax> held = LocalQueryValues(member, local);

                    if (held.Count == 0)
                    {
                        return false;
                    }

                    _ = efQueryLocalsInProgress.Add(local);

                    try
                    {
                        foreach (ExpressionSyntax value in held)
                        {
                            if (!EfQueryRoots(member, value, roots))
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                    finally
                    {
                        _ = efQueryLocalsInProgress.Remove(local);
                    }

                default:
                    return false;
            }
        }

        // ---- A query consumed where it is built.

        /// <summary>
        /// True when the value of a query-producing expression is consumed at a place the analysis can follow to its
        /// execution: a builder, a terminal or a non-executing DbSet member; a loop or spread over a query; a discard; or a
        /// local whose every use is such a consumption. The one return it allows is the context's own
        /// <c>DbSet X =&gt; Set&lt;T&gt;()</c> property, whose value reaches the read that consumes it.
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

                // The loop enumerates it, and the enumeration is a database site only where the operand is provably a query.
                case CommonForEachStatementSyntax loop when ReferenceEquals(loop.Expression, current):
                    return IsEfQueryOperand(member, loop.Expression);

                case SpreadElementSyntax spread when ReferenceEquals(spread.Expression, current):
                    return IsEfQueryOperand(member, spread.Expression);

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
                    return IsFrameworkSetGetter(member, property, current);

                case ReturnStatementSyntax { Parent: BlockSyntax { Parent: AccessorDeclarationSyntax { Parent: AccessorListSyntax { Parent: PropertyDeclarationSyntax getterProperty } } } }:
                    return IsFrameworkSetGetter(member, getterProperty, current);

                default:
                    return false;
            }
        }

        // The context's own set property: `public DbSet<T> Name => Set<T>();` (or a getter that returns exactly that) of an
        // authored EF context, calling Set on the context itself. The set reaches the read of the property, which is judged
        // there; a getter that returns the set of any other context would be judged by an owner that is not the one it reads.
        private bool IsFrameworkSetGetter(AuthoredMember member, PropertyDeclarationSyntax property, ExpressionSyntax returned)
        {
            if (member.Model.GetDeclaredSymbol(property) is not IPropertySymbol symbol
                || !IsEfDbSetType(symbol.Type)
                || symbol.ContainingType is not { } owner
                || !IsExactEfDbContext(owner)
                || !IsAuthoredSymbol(owner)
                || symbol.IsStatic
                || StripTransparentExpression(returned) is not InvocationExpressionSyntax call
                || member.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol { Name: "Set", IsGenericMethod: true } set
                || TypeKey(set.ContainingType) != "Microsoft.EntityFrameworkCore.DbContext"
                || !IsEfAssembly(set.ContainingAssembly))
            {
                return false;
            }

            // The call's receiver is the context itself: implied, or spelled `this`.
            return call.Expression is GenericNameSyntax or IdentifierNameSyntax
                || call.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
        }

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
                    || ClassifyLocalUse(reference, out _) == LocalUse.Opaque)
                {
                    return false;
                }

                // The declaration and the assignments of a query are the producing side.
                if (reference.Parent is VariableDeclaratorSyntax || reference.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, reference))
                {
                    if (reference.Parent is AssignmentExpressionSyntax written
                        && !(StripTransparentExpression(written.Right) is { } assigned && IsEfQueryOperand(member, assigned)))
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
        /// Called for a framework call that executes an EF query: the roots of every operand supply the interceptors it
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
            List<(AuthoredMember Caller, ExpressionSyntax Context)>? contexts = [];

            if (operands.Count == 0)
            {
                // ExecuteSql and DbContext.Find name the context by their own receiver.
                contexts = node is InvocationExpressionSyntax call && definition is not null ? EfOperationContexts(member, call, definition) : null;
            }
            else
            {
                foreach (ExpressionSyntax operand in operands)
                {
                    if (!EfQueryRoots(member, operand, contexts))
                    {
                        contexts = null;

                        break;
                    }
                }
            }

            return TraverseInterceptorsForOperation(
                member,
                node,
                contexts is { Count: > 0 } ? contexts : null,
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
