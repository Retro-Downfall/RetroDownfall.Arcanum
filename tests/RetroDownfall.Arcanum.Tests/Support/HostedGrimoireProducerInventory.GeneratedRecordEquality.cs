using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private bool TryGeneratedRecordEqualityTargets(AuthoredMember caller, IMethodSymbol selected,
            IOperation[] operands, out List<AuthoredMember> targets, out bool canonical, bool lifted = false)
        {
            targets = [];

            canonical = false;

            if (operands is not [IOperation { Syntax: ExpressionSyntax left }, IOperation { Syntax: ExpressionSyntax right }]
                || !TryAuthenticateRecordEqualityOperator(caller, selected,
                    out INamedTypeSymbol record, out Compilation compilation, out IMethodSymbol typedEquals)) return false;

            CleanupProvenanceContext proof = new();

            // A lifted comparison of nullable operands calls the same operator only on two present
            // values, so the closed-value field walk covers it exactly; exact-construction
            // certification of nullable operands is not attempted.
            if (lifted)
            {
                if (!TryClosedValueEqualityTargets(caller, record, compilation, typedEquals, proof, out List<AuthoredMember> liftedTargets)) return false;

                targets = liftedTargets;

                canonical = true;

                return true;
            }

            HashSet<BoundTypeIdentity> path = [];

            List<AuthoredMember> discovered = [];

            bool complete = Visit(record, compilation, typedEquals, new(caller, left), new(caller, right));

            if (!complete
                && TryClosedValueEqualityTargets(caller, record, compilation, typedEquals, proof, out List<AuthoredMember> closed))
            {
                targets = closed;

                canonical = true;

                return true;
            }

            targets = discovered;

            return complete;

            bool Visit(INamedTypeSymbol type, Compilation owner, IMethodSymbol equality,
                BoundValueSource? receiver, BoundValueSource? argument)
            {
                BoundTypeIdentity identity = TypeIdentity(type, owner);

                if (proof.RemainingWork-- <= 0 || !path.Add(identity)) return false;

                try
                {
                    if (!equality.IsImplicitlyDeclared)
                    {
                        if (Resolve(equality, owner) is not { } authored) return false;

                        BoundValueSource exactLeft = null!;

                        BoundValueSource exactRight = null!;

                        bool leftClosed = receiver is { } selectedReceiver
                            && TryExactRecordEqualityConstruction(selectedReceiver, type, owner, proof, out exactLeft);

                        bool rightClosed = argument is { } selectedArgument
                            && TryExactRecordEqualityConstruction(selectedArgument, type, owner, proof, out exactRight);

                        if (receiver is not { } actualReceiver || argument is not { } actualArgument
                            || !leftClosed && !TryExactRecordEqualityConstruction(actualReceiver, type, owner, proof, out exactLeft,
                                allowAuthoredConversionReturn: true)
                            || !rightClosed && !TryExactRecordEqualityConstruction(actualArgument, type, owner, proof, out exactRight,
                                allowAuthoredConversionReturn: true))
                        {
                            // The known sealed source body remains execution evidence even
                            // when no operand construction can be certified. No receiver fields
                            // or inherited authority are inferred for this unbound target.
                            discovered.Add(authored);

                            return false;
                        }

                        var arguments = new[] { (exactRight.Expression, (IParameterSymbol?)equality.Parameters[0], RefKind.None) };

                        AuthoredMember bound = AddCleanupValueBindings(exactRight.Caller, arguments, authored,
                            BindArguments(exactRight.Caller, arguments, authored));

                        AuthoredMember? constructed = ResolveConstructedReceiverContext(exactLeft.Caller,
                            exactLeft.Expression, authored.Symbol.ContainingType,
                            new HashSet<(GraphMemberIdentity Member, int Start, int Length)>());

                        if (constructed is null)
                        {
                            discovered.Add(authored);

                            return false;
                        }

                        bound = MergeValueContext(bound, constructed);

                        bound = BindExactConstructedReceiverPropertyContext(bound, authored, exactLeft, proof);

                        discovered.Add(bound);

                        return leftClosed && rightClosed;
                    }

                    // Synthesized Equals compares every instance field, including authored
                    // storage. No property getter or merely named Equals earns this certificate.
                    if (equality.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not RecordDeclarationSyntax)) return false;

                    foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic))
                    {
                        if (proof.RemainingWork-- <= 0) return false;

                        if (IsClosedRecordEqualityLeaf(field.Type, owner)) continue;

                        if (field.Type is not INamedTypeSymbol fieldType
                            || !TryRecordEqualitySource(fieldType, owner, out INamedTypeSymbol nested, out Compilation nestedOwner)
                            || ExactRecordEquatableImplementation(nested, nestedOwner) is not { } effective) return false;

                        BoundValueSource? nestedReceiver = RecordEqualityFieldValue(receiver, field, proof);

                        BoundValueSource? nestedArgument = RecordEqualityFieldValue(argument, field, proof);

                        if (!Visit(nested, nestedOwner, effective, nestedReceiver, nestedArgument)) return false;
                    }

                    return true;
                }
                finally
                {
                    path.Remove(identity);
                }
            }
        }

        // The compiler-generated operator pair of one authenticated source record, and the single typed
        // Equals slot it forwards to. Nothing here depends on the operands.
        private bool TryAuthenticateRecordEqualityOperator(AuthoredMember caller, IMethodSymbol selected,
            out INamedTypeSymbol record, out Compilation compilation, out IMethodSymbol typedEquals)
        {
            record = null!;

            compilation = null!;

            typedEquals = null!;

            if (selected.Name is not ("op_Equality" or "op_Inequality")
                || !TryRecordEqualitySource(selected.ContainingType, caller.Model.Compilation, out record, out compilation)) return false;

            INamedTypeSymbol authenticated = record;

            IMethodSymbol[] operators = authenticated.GetMembers().OfType<IMethodSymbol>()
                .Where(static method => method.Name is "op_Equality" or "op_Inequality").ToArray();

            if (operators.Length != 2 || operators.Any(method => !method.IsImplicitlyDeclared
                    || !method.IsStatic || method.MethodKind != MethodKind.UserDefinedOperator
                    || method.ReturnType.SpecialType != SpecialType.System_Boolean
                    || method.Parameters.Length != 2
                    || method.Parameters.Any(parameter => parameter.RefKind != RefKind.None
                        || !SymbolEqualityComparer.Default.Equals(parameter.Type, authenticated))
                    || method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not RecordDeclarationSyntax))
                || operators.SingleOrDefault(method => method.Name == selected.Name) is not { } sourceOperator
                || MethodKey(sourceOperator) != MethodKey(selected)) return false;

            IMethodSymbol[] equals = authenticated.GetMembers("Equals").OfType<IMethodSymbol>()
                .Where(method => IsRecordEqualitySlot(method, authenticated)).ToArray();

            if (equals is not [IMethodSymbol slot]) return false;

            typedEquals = slot;

            return true;
        }

        // Whether an operand's provenance can matter for this operator. It cannot when the record is
        // closed-value and no authored typed Equals it reaches can contain a producer site: the canonical
        // traversal then reads no operand and loses no precision. An authored Equals that can reach a site
        // keeps the demand, so a conditional effect that exact construction proves dead stays dead.
        private bool IsProvenanceFreeClosedValueRecordEquality(AuthoredMember caller, IMethodSymbol selected) =>
            TryAuthenticateRecordEqualityOperator(caller, selected, out INamedTypeSymbol record, out Compilation compilation, out IMethodSymbol typedEquals)
            && TryClosedValueEqualityTargets(caller, record, compilation, typedEquals, new CleanupProvenanceContext(), out List<AuthoredMember> authored)
            && !authored.Any(target => ContainsPotentialProducerSite(target));

        // Closed-Value Equality. A record whose every instance field is a handle-incapable
        // leaf (or a nested closed-value record) cannot carry a lease, a group or any other
        // admission handle through its comparison, whatever expression produced the operands.
        // The operand expressions are still traversed where they are written; only the
        // operator's own body is certified. Every authored typed Equals reached through the
        // field walk is returned for Canonical Equals Traversal: the caller binds it with no
        // arguments, so the argument-binding components of the traversal state are blank while
        // the recovery context, the frontiers and every other component stay the caller's.
        private bool TryClosedValueEqualityTargets(AuthoredMember caller, INamedTypeSymbol record, Compilation compilation,
            IMethodSymbol typedEquals, CleanupProvenanceContext proof, out List<AuthoredMember> targets, bool collect = true)
        {
            targets = [];

            HashSet<BoundTypeIdentity> path = [];

            List<AuthoredMember> discovered = [];

            if (!ClosedRecord(record, compilation, typedEquals, collect)) return false;

            targets = discovered;

            return true;

            bool ClosedRecord(INamedTypeSymbol type, Compilation owner, IMethodSymbol equality, bool collect)
            {
                BoundTypeIdentity identity = TypeIdentity(type, owner);

                if (proof.RemainingWork-- <= 0 || !path.Add(identity)) return false;

                try
                {
                    bool authored = !equality.IsImplicitlyDeclared;

                    if (authored)
                    {
                        // An authored typed Equals is traversed, never skipped. Under an authored
                        // parent the nested comparison is whatever that body does, and that body
                        // is itself traversed, so only a synthesized parent collects nested targets.
                        if (collect)
                        {
                            if (Resolve(equality, owner) is not { } member) return false;

                            discovered.Add(BindArguments(caller, [], member));
                        }
                    }
                    else if (equality.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not RecordDeclarationSyntax))
                    {
                        return false;
                    }

                    foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic))
                    {
                        if (proof.RemainingWork-- <= 0 || !ClosedType(field.Type, owner, collect: !authored && collect)) return false;
                    }

                    return true;
                }
                finally
                {
                    path.Remove(identity);
                }
            }

            bool ClosedType(ITypeSymbol type, Compilation owner, bool collect)
            {
                if (proof.RemainingWork-- <= 0) return false;

                if (IsClosedValueLeaf(type, owner, proof)) return true;

                if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [ITypeSymbol inner] } nullable
                    && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    && IsExactFrameworkType(nullable, "System.Nullable`1", typeof(object).Assembly.GetName()))
                {
                    return ClosedType(inner, owner, collect);
                }

                return type is INamedTypeSymbol named
                    && TryRecordEqualitySource(named, owner, out INamedTypeSymbol nested, out Compilation nestedOwner)
                    && ExactRecordEquatableImplementation(nested, nestedOwner) is { } effective
                    && ClosedRecord(nested, nestedOwner, effective, collect);
            }
        }

        // Exact CoreLib identities only. A source-authored lookalike, an interface, object, a
        // delegate, a type parameter, a non-record class or an unreviewed struct is not a leaf.
        private bool IsClosedValueLeaf(ITypeSymbol type, Compilation compilation, CleanupProvenanceContext proof)
        {
            if (proof.RemainingWork-- <= 0) return false;

            AssemblyName core = typeof(object).Assembly.GetName();

            string? special = type.SpecialType switch
            {
                SpecialType.System_Boolean => "System.Boolean",
                SpecialType.System_Char => "System.Char",
                SpecialType.System_SByte => "System.SByte",
                SpecialType.System_Byte => "System.Byte",
                SpecialType.System_Int16 => "System.Int16",
                SpecialType.System_UInt16 => "System.UInt16",
                SpecialType.System_Int32 => "System.Int32",
                SpecialType.System_UInt32 => "System.UInt32",
                SpecialType.System_Int64 => "System.Int64",
                SpecialType.System_UInt64 => "System.UInt64",
                SpecialType.System_Decimal => "System.Decimal",
                SpecialType.System_Single => "System.Single",
                SpecialType.System_Double => "System.Double",
                SpecialType.System_String => "System.String",
                SpecialType.System_DateTime => "System.DateTime",
                _ => null,
            };

            if (special is not null)
            {
                return SymbolEqualityComparer.Default.Equals(type, compilation.GetSpecialType(type.SpecialType))
                    && IsExactFrameworkType(type, special, core);
            }

            // An enumeration is a value-only type whatever assembly declares it.
            if (IsClosedFiniteEnum(type)) return true;

            if (type is IArrayTypeSymbol { Rank: 1, ElementType: { } element })
            {
                return IsClosedValueLeaf(element, compilation, proof);
            }

            if (type is not INamedTypeSymbol named) return false;

            if (named.IsTupleType)
            {
                return named.TupleElements.Length is >= 2 and <= 7
                    && IsExactFrameworkType(named.OriginalDefinition, "System.ValueTuple`" + named.TupleElements.Length, core)
                    && named.TupleElements.All(tuple => IsClosedValueLeaf(tuple.Type, compilation, proof));
            }

            if (named.IsGenericType)
            {
                return named.TypeArguments is [ITypeSymbol item]
                    && IsExactFrameworkType(named, "System.Collections.Immutable.ImmutableArray`1",
                        typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.GetName())
                    && IsClosedValueLeaf(item, compilation, proof);
            }

            return named.TypeKind == TypeKind.Struct
                && (IsExactFrameworkType(named, "System.Guid", core)
                    || IsExactFrameworkType(named, "System.DateTimeOffset", core)
                    || IsExactFrameworkType(named, "System.TimeSpan", core)
                    || IsExactFrameworkType(named, "System.Int128", core)
                    || IsExactFrameworkType(named, "System.UInt128", core)
                    || IsExactFrameworkType(named, "System.Index", core)
                    || IsExactFrameworkType(named, "System.Range", core));
        }

        private bool TryRecordEqualitySource(INamedTypeSymbol type, Compilation consuming,
            out INamedTypeSymbol record, out Compilation owner)
        {
            record = null!;

            owner = null!;

            if (type.IsGenericType || type.IsUnboundGenericType
                || AuthoredCompilationIdentity(type, consuming) is not { } identity) return false;

            Compilation[] owners = compilationIdentities.Where(pair => pair.Value == identity).Select(static pair => pair.Key).ToArray();

            if (owners is not [Compilation source]) return false;

            string metadataName = type.MetadataName;

            for (INamedTypeSymbol? parent = type.ContainingType; parent is not null; parent = parent.ContainingType)
                metadataName = parent.MetadataName + "+" + metadataName;

            if (!type.ContainingNamespace.IsGlobalNamespace)
                metadataName = type.ContainingNamespace.ToDisplayString() + "." + metadataName;

            if (source.GetTypeByMetadataName(metadataName) is not { IsRecord: true, IsAbstract: false } authored
                || !SameBoundType(type, consuming, authored, source)
                || authored.TypeKind != TypeKind.Struct && !(authored.TypeKind == TypeKind.Class && authored.IsSealed)
                || !IsExactFrameworkType(authored.BaseType,
                    authored.TypeKind == TypeKind.Struct ? "System.ValueType" : "System.Object", typeof(object).Assembly.GetName())
                || authored.DeclaringSyntaxReferences.IsEmpty) return false;

            foreach (SyntaxReference reference in authored.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not RecordDeclarationSyntax declaration
                    || !semanticModels.TryGetValue(reference.SyntaxTree, out SemanticModel? model)
                    || !SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declaration), authored)
                    || declaration.Members.Any(static member => member is ConstructorDeclarationSyntax constructor
                        && constructor.Modifiers.Any(SyntaxKind.StaticKeyword)
                        || member is PropertyDeclarationSyntax { Initializer: not null } property
                            && property.Modifiers.Any(SyntaxKind.StaticKeyword)
                        || member is BaseFieldDeclarationSyntax field
                            && field.Modifiers.Any(SyntaxKind.StaticKeyword)
                            && !field.Modifiers.Any(SyntaxKind.ConstKeyword)
                            && field.Declaration.Variables.Any(static variable => variable.Initializer is not null))) return false;
            }

            if (authored.TypeKind == TypeKind.Class
                && authored.GetMembers("EqualityContract").OfType<IPropertySymbol>().ToArray() is not
                    [IPropertySymbol { IsImplicitlyDeclared: true, GetMethod.IsImplicitlyDeclared: true }]) return false;

            record = authored;

            owner = source;

            return true;
        }

        private static bool IsRecordEqualitySlot(IMethodSymbol method, INamedTypeSymbol type) =>
            !method.IsStatic && method.Arity == 0
            && method.ReturnType.SpecialType == SpecialType.System_Boolean
            && method.Parameters is [IParameterSymbol { RefKind: RefKind.None } parameter]
            && SymbolEqualityComparer.Default.Equals(parameter.Type, type);

        private static IMethodSymbol? ExactRecordEquatableImplementation(INamedTypeSymbol record, Compilation compilation)
        {
            INamedTypeSymbol? definition = compilation.GetTypeByMetadataName("System.IEquatable`1");

            if (definition is null || definition.TypeKind != TypeKind.Interface
                || !IsExactFrameworkType(definition, "System.IEquatable`1", typeof(IEquatable<>).Assembly.GetName())
                || !compilation.References.OfType<PortableExecutableReference>().Any(reference =>
                    SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(reference), definition.ContainingAssembly))) return null;

            INamedTypeSymbol[] contracts = record.AllInterfaces.Where(contract =>
                SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, definition)
                && contract.TypeArguments is [ITypeSymbol argument]
                && SymbolEqualityComparer.Default.Equals(argument, record)).ToArray();

            if (contracts is not [INamedTypeSymbol exact]
                || exact.GetMembers("Equals").OfType<IMethodSymbol>().ToArray() is not [IMethodSymbol slot]
                || !IsRecordEqualitySlot(slot, record)
                || record.FindImplementationForInterfaceMember(slot) is not IMethodSymbol implementation
                || !IsRecordEqualitySlot(implementation, record)
                || !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, record)) return null;

            return implementation;
        }

        private static bool IsClosedRecordEqualityLeaf(ITypeSymbol type, Compilation compilation)
        {
            // Initial reviewed EqualityComparer<T>.Default closure: exact bool, int,
            // string metadata definitions. Other primitives and framework values need
            // their own controls before expanding this explicit list.
            string? name = type.SpecialType switch
            {
                SpecialType.System_Boolean => "System.Boolean",
                SpecialType.System_Int32 => "System.Int32",
                SpecialType.System_String => "System.String",
                _ => null,
            };

            return name is not null
                && SymbolEqualityComparer.Default.Equals(type, compilation.GetSpecialType(type.SpecialType))
                && IsExactFrameworkType(type, name, typeof(object).Assembly.GetName());
        }

        private bool TryExactRecordEqualityConstruction(BoundValueSource source, INamedTypeSymbol expected,
            Compilation owner, CleanupProvenanceContext proof, out BoundValueSource exact,
            bool allowAuthoredConversionReturn = false)
        {
            exact = null!;

            HashSet<(GraphMemberIdentity Member, int Start, int Length)> path = [];

            while (proof.RemainingWork-- > 0)
            {
                ExpressionSyntax expression = source.Expression;

                if (!path.Add((MemberIdentity(source.Caller), expression.SpanStart, expression.Span.Length))) return false;

                // Check the original effective conversion before any shared helper can
                // erase its cast syntax. This initial source grammar permits identity
                // copies only; opaque values do not earn this construction certificate.
                IOperation? operation = source.Caller.Model.GetOperation(expression);

                if (source.Caller.Model.GetTypeInfo(expression).Type is not { } actualType
                    || !SameBoundType(actualType, source.Caller.Model.Compilation, expected, owner)) return false;

                // Target-typed new has its own compiler conversion. Accept it only
                // around this exact allocation of the already authenticated record type.
                for (IOperation? contextual = operation?.Parent;
                    contextual is IConversionOperation or IParenthesizedOperation; contextual = contextual.Parent)
                {
                    if (proof.RemainingWork-- <= 0
                        || contextual is IConversionOperation converted
                            && (converted.OperatorMethod is not null || converted.Conversion.MethodSymbol is not null
                                || !converted.Conversion.IsIdentity
                                    && !(expression is ImplicitObjectCreationExpressionSyntax
                                        && operation is IObjectCreationOperation targetTypedCreation
                                        && converted.IsImplicit && converted.Conversion.Exists
                                        && ReferenceEquals(converted.Operand, targetTypedCreation)
                                        && converted.GetConversion().IsObjectCreation
                                        && SymbolEqualityComparer.Default.Equals(targetTypedCreation.Type, actualType)
                                        && SymbolEqualityComparer.Default.Equals(converted.Type, actualType)))) return false;
                }

                if (allowAuthoredConversionReturn && operation is IConversionOperation { OperatorMethod: { } selectedConversion } authoredConversion
                    && selectedConversion is { IsStatic: true, MethodKind: MethodKind.Conversion,
                        Parameters: [IParameterSymbol { RefKind: RefKind.None } conversionParameter] }
                    && authoredConversion.Operand.Syntax is ExpressionSyntax conversionOperand
                    && Resolve(selectedConversion, source.Caller.Model.Compilation) is { } conversionTarget
                    && SameBoundType(selectedConversion.ReturnType, source.Caller.Model.Compilation, expected, owner))
                {
                    var conversionArguments = new[] { (conversionOperand, (IParameterSymbol?)conversionParameter, RefKind.None) };

                    AuthoredMember conversionContext = AddCleanupValueBindings(source.Caller, conversionArguments, conversionTarget,
                        BindArguments(source.Caller, conversionArguments, conversionTarget));

                    if (ReturnedExpressions(conversionContext.Syntax) is not [BaseObjectCreationExpressionSyntax returnedCreation]) return false;

                    // Follow this compiler-selected authored return for execution only.
                    // The independent original operand certificate still refuses its UDC.
                    source = new(conversionContext, returnedCreation);

                    continue;
                }

                if (operation is IConversionOperation conversion)
                {
                    if (conversion.OperatorMethod is not null || conversion.Conversion.MethodSymbol is not null
                        || !conversion.Conversion.IsIdentity || conversion.Operand.Syntax is not ExpressionSyntax operand) return false;

                    source = new(source.Caller, operand);

                    continue;
                }

                if (expression is ParenthesizedExpressionSyntax parenthesized)
                {
                    source = new(source.Caller, parenthesized.Expression);

                    continue;
                }

                if (expression is PostfixUnaryExpressionSyntax suppression
                    && suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                {
                    source = new(source.Caller, suppression.Operand);

                    continue;
                }

                if (TryResolveStableBoundValueSource(source.Caller, expression, out BoundValueSource? bound) && bound is not null)
                {
                    source = bound;

                    continue;
                }

                if (TryResolveStableLocalInitializerValueSource(source.Caller, expression, out BoundValueSource? initialized) && initialized is not null)
                {
                    source = initialized;

                    continue;
                }

                if (expression is not BaseObjectCreationExpressionSyntax creation
                    || operation is not IObjectCreationOperation { Constructor: { } constructor }
                    || !SameBoundType(constructor.ContainingType, source.Caller.Model.Compilation, expected, owner)
                    || !constructor.IsImplicitlyDeclared
                        && !constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is RecordDeclarationSyntax
                            { ParameterList: not null } declaration
                            && semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? declarationModel)
                            && declarationModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol declared
                            && SymbolEqualityComparer.Default.Equals(declared, constructor.ContainingType))) return false;

                foreach (IOperation value in operation.DescendantsAndSelf())
                {
                    if (proof.RemainingWork-- <= 0
                        || value is IInvocationOperation or IDynamicInvocationOperation or IDynamicObjectCreationOperation
                            or ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation
                            or IConditionalOperation or ISwitchExpressionOperation
                        || value is IConversionOperation converted
                            && (converted.OperatorMethod is not null || converted.Conversion.MethodSymbol is not null)
                        || value is IUnaryOperation { OperatorMethod: not null }
                        || value is IBinaryOperation { OperatorMethod: not null }
                        || value is IPropertyReferenceOperation property
                            && (property.Parent is not ISimpleAssignmentOperation assignment || assignment.Target != property
                                || !HasExactAutoInitAssignmentSemantics(property.Property))) return false;
                }

                exact = source;

                return true;
            }

            return false;
        }

        private BoundValueSource? RecordEqualityFieldValue(BoundValueSource? receiver, IFieldSymbol field, CleanupProvenanceContext proof)
        {
            if (receiver is not { } actual || !field.IsImplicitlyDeclared
                || field.AssociatedSymbol is not IPropertySymbol property
                || !HasExactAutoInitAssignmentSemantics(property)
                || !TryExactRecordEqualityConstruction(actual, field.ContainingType, actual.Caller.Model.Compilation,
                    proof, out BoundValueSource exact)
                || exact.Expression is not BaseObjectCreationExpressionSyntax creation) return null;

            // Preserve the actual expression assigned or passed to this backing field.
            // PropertyValueFromExactConstruction would enter the shared cast-stripping
            // value flow before the next nested record has authenticated its source.
            if (creation.Initializer is { } initializer)
            {
                AssignmentExpressionSyntax[] assignments = initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => exact.Caller.Model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol assigned
                        && SameBoundPropertyDefinition(assigned, exact.Caller.Model.Compilation, property,
                            exact.Caller.Model.Compilation)).ToArray();

                return assignments is [AssignmentExpressionSyntax selected]
                    ? new(exact.Caller, selected.Right) : null;
            }

            if (PropertyConstructorParameter(property) is not { } parameter) return null;

            var arguments = ConstructorArguments(exact.Caller, creation)
                .Where(argument => argument.Parameter?.Ordinal == parameter.Ordinal).ToArray();

            return arguments is [var argument] && argument.RefKind == RefKind.None
                ? new(exact.Caller, argument.Expression) : null;
        }
    }
}
