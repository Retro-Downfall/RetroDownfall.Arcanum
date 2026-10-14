using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Support;

// Virtual dispatch behind the reviewed database members. The reviewed support members and the database sites name
// framework members by identity, but most of them are virtual or abstract, or are non-virtual wrappers around a
// virtual slot (DbConnection.CreateCommand calls CreateDbCommand, DbCommand.ExecuteReader calls
// ExecuteDbDataReader), of a type authored code can extend. A grant by name says what the framework body does; it
// says nothing about an override an authored subclass of that type put behind it. The grant therefore holds only
// where the receiver is closed, and everything else is refused.
internal static partial class HostedGrimoireProducerInventory
{
    // The ADO.NET provider base classes and the Sqlite types authored code can derive from (their constructors are
    // public or protected). Each member of these types a reviewed grant names is a wrapper over, or is itself, a
    // slot an authored subclass can override, so a grant is valid only for a receiver whose runtime type is a
    // trusted framework type.
    private static readonly IReadOnlySet<string> ProviderWrapperTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Data.Common.DbConnection",
        "System.Data.Common.DbCommand",
        "System.Data.Common.DbTransaction",
        "System.Data.Common.DbDataReader",
        "System.Data.Common.DbParameter",
        "System.Data.Common.DbParameterCollection",
        "System.Data.Common.DbConnectionStringBuilder",
        "Microsoft.Data.Sqlite.SqliteConnection",
        "Microsoft.Data.Sqlite.SqliteCommand",
        "Microsoft.Data.Sqlite.SqliteParameter",
        "Microsoft.Data.Sqlite.SqliteConnectionStringBuilder",
    };

    // The Entity Framework types authored code can extend, whose reviewed members are themselves virtual. The compiler
    // binds a call to the most-derived override of the static type it sees, so a sealed static type (or an exact
    // runtime type that does not override the member) is closed; an unsealed one can hold an override.
    private static readonly IReadOnlySet<string> ProviderVirtualTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.EntityFrameworkCore.DbContext",
        "Microsoft.EntityFrameworkCore.DbSet`1",
        "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker",
        "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry",
        "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`1",
        "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade",
        "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction",
    };

    // Sqlite types no authored code can derive from (their constructors are internal or private). Their members run only
    // framework bodies, but the virtual members of an argument they are handed (a parameter, a connection) run whatever
    // the argument's type is.
    private static readonly IReadOnlySet<string> ProviderNonExtensibleTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.Data.Sqlite.SqliteDataReader",
        "Microsoft.Data.Sqlite.SqliteTransaction",
        "Microsoft.Data.Sqlite.SqliteParameterCollection",
    };

    // The assemblies whose provider types the dispatch closure trusts, by exact identity.
    private static IReadOnlyList<AssemblyName> TrustedProviderAssemblies() =>
    [
        typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName(),
        typeof(System.Data.Common.DbConnection).Assembly.GetName(),
        typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName(),
        typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly.GetName(),
        typeof(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions).Assembly.GetName(),
    ];

    private static bool IsTrustedProviderAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && TrustedProviderAssemblies().Any(trusted => ProviderIdentityMatches(assembly.Identity, trusted));

    // Name, version, culture and public key token, exactly: a simple name proves nothing.
    private static bool ProviderIdentityMatches(AssemblyIdentity actual, AssemblyName trusted) =>
        actual.Name == trusted.Name
        && actual.Version == trusted.Version
        && string.Equals(actual.CultureName ?? string.Empty, trusted.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && actual.PublicKeyToken.SequenceEqual(trusted.GetPublicKeyToken() ?? []);

    private sealed partial class ProducerGraph
    {
        // True for a framework class no authored code can derive from: it is declared outside every analysed source,
        // in a trusted provider assembly, and has no constructor a derived class could chain to (every instance
        // constructor is internal or private). Its runtime type is itself, whatever expression holds it.
        private static bool IsNonExtensibleProviderType(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Class } named
            && !named.Locations.Any(static location => location.IsInSource)
            && IsTrustedProviderAssembly(named.ContainingAssembly)
            && (named.IsSealed
                || named.InstanceConstructors.All(static constructor => constructor.DeclaredAccessibility
                    is Accessibility.Internal or Accessibility.Private or Accessibility.ProtectedAndInternal));

        private static bool IsAuthoredSymbol(ISymbol symbol) => symbol.Locations.Any(static location => location.IsInSource);

        // The audited surface: a member of a provider wrapper type or of an extensible Entity Framework type, called on
        // an instance. Statics and constructors have no receiver to dispatch on. A member of a type no authored code can
        // derive from is audited for its arguments alone.
        private static bool IsProviderDispatchMember(ISymbol definition, out bool wrapper, out bool argumentsOnly)
        {
            wrapper = false;

            argumentsOnly = false;

            if (definition.ContainingType is not { } declaring
                || definition.IsStatic
                || definition is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
            {
                return false;
            }

            string key = TypeKey(declaring);

            if (ProviderWrapperTypes.Contains(key))
            {
                wrapper = true;

                return true;
            }

            if (ProviderNonExtensibleTypes.Contains(key))
            {
                wrapper = true;

                argumentsOnly = true;

                return true;
            }

            return ProviderVirtualTypes.Contains(key);
        }

        /// <summary>
        /// A reviewed database member (support member or database site) is closed when no authored override can run
        /// behind it: the DbProviderTypesClosedByUse premise holds, or the receiver is a closed one (see
        /// <see cref="IsClosedProviderReceiver"/>) and no argument is an authored subclass of a provider type whose
        /// virtual members the member would call. Anything else is refused.
        /// </summary>
        private bool ReviewedProviderMemberIsClosed(ISymbol member, SyntaxNode? node, SemanticModel? model, AuthoredMember? context)
        {
            ISymbol definition = member is IMethodSymbol method ? method.ReducedFrom ?? method : member;

            if (!IsProviderDispatchMember(definition, out bool wrapper, out bool argumentsOnly))
            {
                return true;
            }

            // A disposal reached through cleanup resolution names the exact candidate type it proved, so it is closed;
            // a disposal written as a call is judged like any other member.
            if (definition.Name is "Dispose" or "DisposeAsync" && (node is not InvocationExpressionSyntax || context is null))
            {
                return true;
            }

            if (DbProviderTypesClosedByUse)
            {
                return true;
            }

            if (node is null || model is null || context is null)
            {
                return false;
            }

            IOperation? operation = model.GetOperation(node);

            ExpressionSyntax? receiver;

            switch (operation)
            {
                case IInvocationOperation invocation:
                    if (invocation.Arguments.Any(static argument => UnconvertedType(argument.Value) is { } type && IsAuthoredProviderSubclass(type)))
                    {
                        return false;
                    }

                    receiver = ProviderOperationReceiver(invocation.Instance, node);

                    break;

                case IPropertyReferenceOperation property:
                    if (property.Parent is ISimpleAssignmentOperation { Value: { } assignedValue } && UnconvertedType(assignedValue) is { } assigned && IsAuthoredProviderSubclass(assigned))
                    {
                        return false;
                    }

                    receiver = ProviderOperationReceiver(property.Instance, node);

                    break;

                default:
                    return false;
            }

            if (argumentsOnly)
            {
                return true;
            }

            // An implied `this` is the containing type, which is closed only where nothing can derive from it.
            if (receiver is null)
            {
                return !wrapper
                    && context.Symbol.ContainingType is { IsSealed: true } sealedOwner
                    && IsExactEfDbContext(sealedOwner);
            }

            return IsClosedProviderReceiver(context, receiver, definition, wrapper, 0);
        }

        // The type a value has before the implicit conversions an argument or assignment applies to it.
        private static ITypeSymbol? UnconvertedType(IOperation value)
        {
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            return value.Type;
        }

        // The receiver expression of an instance operation, looking through the instance of a conditional access.
        private static ExpressionSyntax? ProviderOperationReceiver(IOperation? instance, SyntaxNode node)
        {
            if (instance is null || instance.IsImplicit && instance is IInstanceReferenceOperation)
            {
                return null;
            }

            if (instance is IConditionalAccessInstanceOperation)
            {
                return ConditionalAccessReceiver(node);
            }

            return instance.Syntax as ExpressionSyntax;
        }

        private static bool IsAuthoredProviderSubclass(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } named || !IsAuthoredSymbol(named))
            {
                return false;
            }

            for (INamedTypeSymbol? parent = named.BaseType; parent is not null; parent = parent.BaseType)
            {
                string key = TypeKey(parent);

                if (ProviderWrapperTypes.Contains(key) || ProviderVirtualTypes.Contains(key))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A receiver is closed when what runs behind the member is exactly the framework body. For the ADO.NET and
        /// Sqlite wrappers that is a receiver whose static type no authored code can extend, or whose provenance
        /// proves an exact framework runtime type (an allocation, or the closed final provider's factory chain). For the
        /// virtual Entity Framework types the compiler already bound the most-derived override, so a sealed static type
        /// is closed, an exact runtime type is closed when it does not override the member, and the context, the
        /// change tracker, the database facade and the sets a closed context hands out are framework objects.
        /// </summary>
        private bool IsClosedProviderReceiver(AuthoredMember context, ExpressionSyntax receiver, ISymbol definition, bool wrapper, int depth)
        {
            receiver = StripTransparentExpression(receiver);

            if (receiver is BaseExpressionSyntax)
            {
                return true;
            }

            ITypeSymbol? staticType = context.Model.GetTypeInfo(receiver).Type;

            if (staticType is null || staticType.TypeKind is TypeKind.Error or TypeKind.Dynamic)
            {
                return false;
            }

            if (wrapper)
            {
                // SqliteSealedByUse: no subclass of SqliteConnection, SqliteCommand or SqliteTransaction exists, so a
                // static type that is exactly one of them is its own runtime type.
                if (IsNonExtensibleProviderType(staticType) || SqliteSealedByUse && IsSqliteSealedFamilyClass(staticType))
                {
                    return true;
                }
            }
            else if (staticType is INamedTypeSymbol { TypeKind: TypeKind.Struct }
                || staticType is INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true }
                    && (IsNonExtensibleProviderType(staticType) || IsExactEfDbContext((INamedTypeSymbol)staticType)))
            {
                return true;
            }

            CleanupProvenance provenance = CleanupProvenanceOf(
                context,
                receiver,
                new CleanupProvenanceContext { ClosedSqliteProvider = true });

            if (provenance.Complete && provenance.Candidates.Count != 0
                && provenance.Candidates.All(candidate => IsClosedProviderCandidate(candidate.Type, definition, wrapper)))
            {
                return true;
            }

            if (depth < 4
                && receiver is IdentifierNameSyntax
                && context.Model.GetSymbolInfo(receiver).Symbol is ILocalSymbol local)
            {
                // A local holds what was assigned to it within the member that declares it.
                List<ExpressionSyntax> held = LocalQueryValues(context, local);

                return held.Count != 0 && held.All(value => IsClosedProviderReceiver(context, value, definition, wrapper, depth + 1));
            }

            return depth < 4
                && (wrapper
                    ? IsFrameworkParametersOfClosedCommand(context, receiver, depth)
                    : IsFrameworkProviderObjectOfClosedContext(context, receiver, depth));
        }

        // The parameter collection of a closed command is the command's own: a non-extensible framework object.
        private bool IsFrameworkParametersOfClosedCommand(AuthoredMember context, ExpressionSyntax receiver, int depth) =>
            receiver is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Parameters" } access
            && context.Model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "Parameters", IsStatic: false } property
            && TypeKey(property.ContainingType) is "System.Data.Common.DbCommand" or "Microsoft.Data.Sqlite.SqliteCommand"
            && IsTrustedProviderAssembly(property.ContainingAssembly)
            && IsClosedProviderReceiver(context, access.Expression, property, wrapper: true, depth + 1);

        private static bool IsClosedProviderCandidate(ITypeSymbol candidate, ISymbol definition, bool wrapper)
        {
            if (candidate is not INamedTypeSymbol { TypeKind: TypeKind.Class } named)
            {
                return false;
            }

            if (!IsAuthoredSymbol(named))
            {
                return IsTrustedProviderAssembly(named.ContainingAssembly);
            }

            // An exact authored runtime type behind a wrapper hides the overrides of its virtual slots. Behind a member
            // that is itself virtual, it is closed only if it overrides nothing the member could reach.
            return !wrapper && IsExactEfDbContext(named) && !OverridesMember(named, definition);
        }

        private static bool OverridesMember(INamedTypeSymbol type, ISymbol definition)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                foreach (ISymbol candidate in current.GetMembers(definition.Name))
                {
                    if (!IsAuthoredSymbol(candidate) || !candidate.IsOverride)
                    {
                        continue;
                    }

                    ISymbol? overridden = candidate switch
                    {
                        IMethodSymbol method => method.OverriddenMethod,
                        IPropertySymbol property => property.OverriddenProperty,
                        _ => null,
                    };

                    for (; overridden is not null; overridden = overridden switch
                    {
                        IMethodSymbol method => method.OverriddenMethod,
                        IPropertySymbol property => property.OverriddenProperty,
                        _ => null,
                    })
                    {
                        if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, definition.OriginalDefinition))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The objects a closed context hands out are the framework's own: the Database facade, the change tracker and
        /// the sets (<c>Set&lt;T&gt;()</c>, and a set property whose body is exactly that call or that nothing authored
        /// ever assigns). They are created by Entity Framework, whatever the static type says.
        /// </summary>
        private bool IsFrameworkProviderObjectOfClosedContext(AuthoredMember context, ExpressionSyntax receiver, int depth)
        {
            ISymbol? symbol = context.Model.GetSymbolInfo(receiver).Symbol;

            ExpressionSyntax? owner = receiver switch
            {
                MemberAccessExpressionSyntax access => access.Expression,
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Expression,
                _ => null,
            };

            switch (symbol)
            {
                case IPropertySymbol { Name: "ChangeTracker" or "Database" } property
                    when TypeKey(property.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext"
                        && IsTrustedProviderAssembly(property.ContainingAssembly):
                case IMethodSymbol { Name: "Set", IsGenericMethod: true } set
                    when TypeKey(set.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext"
                        && IsTrustedProviderAssembly(set.ContainingAssembly):
                    return IsClosedContextOwner(context, owner, symbol, depth);

                case IPropertySymbol { Type: INamedTypeSymbol setType } setProperty
                    when TypeKey(setType) == "Microsoft.EntityFrameworkCore.DbSet`1"
                        && IsAuthoredSymbol(setProperty)
                        && IsFrameworkSetProperty(setProperty):
                    return IsClosedContextOwner(context, owner, symbol, depth);

                default:
                    return false;
            }
        }

        private bool IsClosedContextOwner(AuthoredMember context, ExpressionSyntax? owner, ISymbol used, int depth)
        {
            if (owner is null)
            {
                return context.Symbol.ContainingType is { IsSealed: true } sealedOwner && IsExactEfDbContext(sealedOwner);
            }

            return IsClosedProviderReceiver(context, owner, used, wrapper: false, depth + 1);
        }

        // A set property of an authored context that Entity Framework fills: an expression-bodied property returning
        // Set<T>() or an automatic property nothing authored writes. The declaring context must be sealed, so no
        // derived context can replace the property.
        private bool IsFrameworkSetProperty(IPropertySymbol property)
        {
            if (property.ContainingType is not { IsSealed: true } owner || !IsExactEfDbContext(owner) || property.IsVirtual || property.IsOverride)
            {
                return false;
            }

            if (property.DeclaringSyntaxReferences is not [{ } reference] || reference.GetSyntax() is not PropertyDeclarationSyntax declaration
                || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model))
            {
                return false;
            }

            if (declaration.ExpressionBody?.Expression is { } body)
            {
                return StripTransparentExpression(body) is InvocationExpressionSyntax call
                    && model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Set", IsGenericMethod: true } set
                    && TypeKey(set.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext"
                    && IsTrustedProviderAssembly(set.ContainingAssembly)
                    && call.Expression is IdentifierNameSyntax or GenericNameSyntax;
            }

            return declaration.AccessorList is { } accessors
                && accessors.Accessors.All(static accessor => accessor.Body is null && accessor.ExpressionBody is null)
                && declaration.Initializer is null
                && !HasAuthoredStorageWrite(property);
        }
    }
}
