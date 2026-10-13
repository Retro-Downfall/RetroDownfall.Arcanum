using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed record ScopedLeaseInstanceSource(
        string Identity, string Origin, INamedTypeSymbol Type, Compilation Compilation,
        bool EntryPreservesAdmission, bool ContinuationProven = false);

    private sealed partial class ProducerGraph
    {
        private sealed record ScopedLeaseCompletedTarget(
            IMethodSymbol Selected, AuthoredMember Target,
            bool CollectionIndexerProof, bool SuppressOrdinaryStorageConditions);

        private readonly Dictionary<AuthoredMember, Dictionary<InvocationExpressionSyntax, ScopedLeaseCompletedTarget>>
            completedScopedLeaseTargets = new(ReferenceEqualityComparer.Instance);

        private static bool ScopedLeaseProofHasNoActiveQuery(CleanupProvenanceContext proof) =>
            proof.Active.Count == 0 && proof.CompleteActive.Count == 0
                && proof.ValueActive.Count == 0 && proof.CompleteValueActive.Count == 0
                && proof.CollectionElementActive.Count == 0;

        private AuthoredMember? TryScopedLeaseInvocationTarget(IMethodSymbol selected, AuthoredMember caller,
            InvocationExpressionSyntax call, CleanupProvenanceContext proof)
        {
            bool reusableContext = ScopedLeaseProofHasNoActiveQuery(proof);

            // Reuse only an independently completed positive fact for this exact
            // immutable caller and syntax. Never revive an exhausted or active proof.
            if (proof.RemainingWork > 0 && reusableContext
                && completedScopedLeaseTargets.TryGetValue(caller, out var completedCalls)
                && completedCalls.TryGetValue(call, out ScopedLeaseCompletedTarget? completed)
                && SymbolEqualityComparer.Default.Equals(completed.Selected, selected)
                && completed.CollectionIndexerProof == proof.CollectionIndexerProof
                && completed.SuppressOrdinaryStorageConditions == proof.SuppressOrdinaryStorageConditions)
                return completed.Target;

            if (caller.OrdinaryScopedHolder is not { } witness
                || selected.IsStatic || selected.Name != "ProveDrainedMaintenance"
                || selected.ContainingType.TypeKind != TypeKind.Interface
                || !IsExactAuthoredProtocolType(selected.ContainingType,
                    "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", caller.Model.Compilation)
                || ScopedAdmissionOf(caller) is not { Active: true } admission
                || AdmissionReceiver(caller, call) is not { } receiver
                || !ScopedAdmissionLeaseReceiver(caller, receiver, call.Span.End, admission, proof)
                || witness.Binding.ArgumentList.Arguments is not [ArgumentSyntax argument]
                || !TryScopedLeaseOutAlias(witness, argument.Expression, proof,
                    out ILocalSymbol local, out SingleVariableDesignationSyntax designation)
                || designation.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is not { } acquisition
                || Location(acquisition) != witness.AdmissionOrigin
                || AdmissionReceiver(witness.Origin, acquisition) is not { } gateReceiver
                || proof.RemainingWork-- <= 0
                || gateReceiver is not IdentifierNameSyntax
                || !TryResolveStableLocalInitializerValueSource(witness.Origin, gateReceiver, out BoundValueSource? gateValue)
                || gateValue is null || gateValue.Expression is not BaseObjectCreationExpressionSyntax gateCreation
                || !ReferenceEquals(gateValue.Caller.Model.Compilation, witness.Origin.Model.Compilation)
                || gateValue.Caller.Model.GetOperation(gateCreation) is not IObjectCreationOperation
                    { Type: INamedTypeSymbol { IsSealed: true, IsAbstract: false } gateType, Initializer: null }
                || witness.Origin.Model.GetSymbolInfo(gateReceiver).Symbol is not { } gateSlot) return null;

            Dictionary<ISymbol, ITypeSymbol> concrete = witness.Origin.ConcreteBindings?.ToDictionary(
                static pair => pair.Key, static pair => pair.Value, SymbolEqualityComparer.Default)
                ?? new(SymbolEqualityComparer.Default);

            concrete[gateSlot] = gateType;

            AuthoredMember root = witness.Origin with { ConcreteBindings = concrete };

            if (TryExactAuthoredOutLocalAssignments(root, argument.Expression, local, designation, proof) is not { } values)
                return null;

            List<IObjectCreationOperation> allocations = [];

            foreach (AssignmentExpressionSyntax assignment in PerUnitVisited(values.Assignments, proof))
            {
                if (assignment.Right.IsKind(SyntaxKind.NullLiteralExpression)) continue;

                if (assignment.Right is not BaseObjectCreationExpressionSyntax creation
                    || values.Bound.Model.GetOperation(creation) is not IObjectCreationOperation
                        { Constructor: not null, Type: INamedTypeSymbol { IsSealed: true, IsAbstract: false } } allocation)
                    return null;

                allocations.Add(allocation);
            }

            if (proof.RemainingWork <= 0 || allocations is not [IObjectCreationOperation made]
                || made.Type is not INamedTypeSymbol actual
                || !IsExactFrameworkType(actual.BaseType, "System.Object", typeof(object).Assembly.GetName())) return null;

            List<IMethodSymbol> implementations = [];

            foreach (INamedTypeSymbol contract in PerUnitVisited(actual.AllInterfaces, proof))
            {
                if (!SameBoundType(contract, values.Bound.Model.Compilation, selected.ContainingType,
                        caller.Model.Compilation)) continue;

                foreach (IMethodSymbol slot in PerUnitVisited(contract.GetMembers().OfType<IMethodSymbol>(), proof))
                {
                    if (MethodKey(slot) == MethodKey(selected)
                        && actual.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation)
                        implementations.Add(implementation);
                }
            }

            if (proof.RemainingWork <= 0 || implementations is not [IMethodSymbol effective]
                || !SymbolEqualityComparer.Default.Equals(effective.ContainingType, actual)
                || Resolve(effective, values.Bound.Model.Compilation) is not { } target) return null;

            // Discovery is complete. Unsupported construction/body authority below
            // never discards this known implementation or its physical effects.
            ScopedLeaseInstanceSource instance = new(
                witness.Identity + "|instance:" + Location(made.Syntax) + "|" + Fingerprint(made.Syntax),
                witness.AdmissionOrigin, actual, values.Bound.Model.Compilation,
                !actual.IsGenericType && made.Constructor is { IsImplicitlyDeclared: true, Parameters.Length: 0 }
                    && made.Initializer is null
                    && ScopedLeaseTypeInitializersArePassive(actual, values.Bound.Model.Compilation, proof, staticOnly: false));

            AuthoredMember tagged = target with { ScopedLeaseInstance = instance };

            bool continuation = !ScopedLeaseInstanceEndsBefore(tagged, int.MaxValue, proof)
                && proof.RemainingWork > 0;

            AuthoredMember result = tagged with
                { ScopedLeaseInstance = instance with { ContinuationProven = continuation } };

            if (instance.EntryPreservesAdmission && continuation && reusableContext && ScopedLeaseProofHasNoActiveQuery(proof))
            {
                if (!completedScopedLeaseTargets.TryGetValue(caller, out var completedForCaller))
                    completedScopedLeaseTargets.Add(caller, completedForCaller = new(ReferenceEqualityComparer.Instance));

                completedForCaller[call] = new(selected, result,
                    proof.CollectionIndexerProof, proof.SuppressOrdinaryStorageConditions);
            }

            return result;
        }

        private bool ScopedLeaseTypeInitializersArePassive(INamedTypeSymbol type, Compilation compilation,
            CleanupProvenanceContext proof, bool staticOnly)
        {
            if (proof.RemainingWork-- <= 0 || type.TypeKind != TypeKind.Class || type.IsGenericType
                || type.DeclaringSyntaxReferences.IsEmpty
                || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                || !IsExactFrameworkType(type.BaseType, "System.Object", typeof(object).Assembly.GetName())) return false;

            foreach (SyntaxReference reference in PerUnitVisited(type.DeclaringSyntaxReferences, proof))
            {
                if (reference.GetSyntax() is not ClassDeclarationSyntax { ParameterList: null } declaration
                    || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model)
                    || !ReferenceEquals(model.Compilation, compilation)
                    || !SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declaration), type)) return false;

                foreach (MemberDeclarationSyntax member in PerUnitVisited(declaration.Members, proof))
                {
                    // An authored static constructor executes outside the selected body.
                    if (member is ConstructorDeclarationSyntax constructor)
                    {
                        if (model.GetDeclaredSymbol(constructor) is not IMethodSymbol declaredConstructor
                            || !SymbolEqualityComparer.Default.Equals(declaredConstructor.ContainingType, type)
                            || declaredConstructor.IsStatic) return false;
                    }

                    if (member is BaseFieldDeclarationSyntax field)
                    {
                        foreach (VariableDeclaratorSyntax variable in PerUnitVisited(field.Declaration.Variables, proof))
                        {
                            ISymbol? storage = model.GetDeclaredSymbol(variable);

                            if (storage is not IFieldSymbol and not IEventSymbol
                                || !SymbolEqualityComparer.Default.Equals(storage.ContainingType, type)) return false;

                            // Visit even default storage. Only instance initialization is
                            // absent when reading another type's static backing storage.
                            if (storage is IFieldSymbol { IsConst: true } constant)
                            {
                                if (!constant.HasConstantValue) return false;

                                continue;
                            }

                            if (staticOnly && !storage.IsStatic) continue;

                            if (variable.Initializer is { } initializer
                                && !ScopedLeaseInitializerIsPassive(model, initializer.Value, proof,
                                    allowStaticStorageRead: !staticOnly)) return false;
                        }
                    }

                    if (member is PropertyDeclarationSyntax property)
                    {
                        if (model.GetDeclaredSymbol(property) is not IPropertySymbol storage
                            || !SymbolEqualityComparer.Default.Equals(storage.ContainingType, type)) return false;

                        if (staticOnly && !storage.IsStatic) continue;

                        if (property.Initializer is { } initializer
                            && (!ScopedLeaseAutoPropertyIsBodyless(model, property, storage, proof)
                                || !ScopedLeaseInitializerIsPassive(model, initializer.Value, proof,
                                    allowStaticStorageRead: !staticOnly))) return false;
                    }
                }
            }

            return proof.RemainingWork > 0;
        }

        private static bool ScopedLeaseAutoPropertyIsBodyless(SemanticModel model, PropertyDeclarationSyntax declaration,
            IPropertySymbol property, CleanupProvenanceContext proof)
        {
            if (proof.RemainingWork-- <= 0 || property.IsIndexer || property.ReturnsByRef || property.ReturnsByRefReadonly
                || property.GetMethod is null || declaration.ExpressionBody is not null
                || declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
                || declaration.AccessorList is not { Accessors.Count: > 0 } accessors
                || !SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declaration), property)) return false;

            foreach (AccessorDeclarationSyntax accessor in PerUnitVisited(accessors.Accessors, proof))
            {
                if (accessor.Body is not null || accessor.ExpressionBody is not null
                    || !accessor.SemicolonToken.IsKind(SyntaxKind.SemicolonToken)
                    || model.GetDeclaredSymbol(accessor) is not IMethodSymbol method
                    || method.IsExtern || method.IsAbstract
                    || !SymbolEqualityComparer.Default.Equals(method.AssociatedSymbol, property)) return false;
            }

            return proof.RemainingWork > 0;
        }

        private bool ScopedLeaseInitializerIsPassive(SemanticModel model, ExpressionSyntax expression,
            CleanupProvenanceContext proof, bool allowStaticStorageRead)
        {
            // The existing normalizer has its own bound. Charge all syntax here to
            // the same incoming proof before using it; no fresh proof earns entry.
            foreach (SyntaxNode node in PerUnitVisited(expression.DescendantNodesAndSelf(), proof))
            {
                if (node.ContainsDiagnostics) return false;
            }

            if (proof.RemainingWork <= 0
                || !TryNormalizeBuiltInCleanupValue(model, expression, out ExpressionSyntax value,
                    requireIdentityPreservingCasts: true)) return false;

            if ((value.IsKind(SyntaxKind.NullLiteralExpression)
                    || value.IsKind(SyntaxKind.DefaultLiteralExpression) || value is DefaultExpressionSyntax)
                && IsSemanticallyEmptyCleanupValue(model, value)) return proof.RemainingWork > 0;

            Conversion conversion = model.GetConversion(expression);

            if (!allowStaticStorageRead || !conversion.Exists || !(conversion.IsIdentity || conversion.IsReference)
                || model.GetOperation(value) is not IPropertyReferenceOperation
                    { Instance: null, Arguments.Length: 0, Property.IsStatic: true } read
                || read.Property.DeclaringSyntaxReferences is not [SyntaxReference reference]
                || reference.GetSyntax() is not PropertyDeclarationSyntax declaration
                || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? sourceModel)
                || !ReferenceEquals(sourceModel.Compilation, model.Compilation)
                || !ScopedLeaseAutoPropertyIsBodyless(sourceModel, declaration, read.Property, proof)) return false;

            // This certifies only execution of a backing-storage read. Its mutable
            // value stays unknown and gains no receiver identity or authority.
            // The owner's static initializers accept empty values only, so there
            // is no recursive getter/type-initialization interpreter or cycle.
            return ScopedLeaseTypeInitializersArePassive(read.Property.ContainingType, model.Compilation,
                proof, staticOnly: true);
        }

        private bool TryScopedLeaseOutAlias(OrdinaryScopedHolderWitness witness, ExpressionSyntax expression,
            CleanupProvenanceContext proof, out ILocalSymbol local, out SingleVariableDesignationSyntax designation)
        {
            local = null!;

            designation = null!;

            HashSet<ISymbol> aliases = new(SymbolEqualityComparer.Default);

            while (proof.RemainingWork > 0)
            {
                // Authenticate each original operand before any wrapper is removed.
                // Only same-member stable local initializers may lead to the out slot.
                if (proof.RemainingWork-- <= 0
                    || !TryNormalizeBuiltInCleanupValue(witness.Origin.Model, expression, out expression,
                        requireIdentityPreservingCasts: true)
                    || expression.SyntaxTree != witness.Origin.Syntax.SyntaxTree
                    || !witness.Origin.Syntax.Span.Contains(expression.Span)
                    || witness.Origin.Model.GetSymbolInfo(expression).Symbol is not ILocalSymbol candidate
                    || !SymbolEqualityComparer.Default.Equals(candidate.ContainingSymbol, witness.Origin.Symbol)
                    || !aliases.Add(candidate)
                    || candidate.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                    || !IsStableAcrossAdmissionCleanupBackEdges(witness.Origin, expression, candidate)) return false;

                if (declaration.GetSyntax() is SingleVariableDesignationSyntax acquired)
                {
                    local = candidate;

                    designation = acquired;

                    return proof.RemainingWork > 0;
                }

                if (!TryResolveStableLocalInitializerValueSource(witness.Origin, expression, out BoundValueSource? source)
                    || source is null || !ReferenceEquals(source.Caller.Model, witness.Origin.Model)
                    || !SymbolEqualityComparer.Default.Equals(source.Caller.Symbol, witness.Origin.Symbol)) return false;

                expression = source.Expression;
            }

            return false;
        }

        private bool IsScopedLeaseInstanceOperation(AuthoredMember member, IOperation? operation) =>
            member.ScopedLeaseInstance is { } source
            && operation is IInstanceReferenceOperation
                { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type: { } actual }
            && ReferenceEquals(member.Model.Compilation, source.Compilation)
            && SymbolEqualityComparer.Default.Equals(actual, source.Type);

        private bool ScopedLeaseInstanceEndsBefore(AuthoredMember member, int position, CleanupProvenanceContext proof)
        {
            if (member.ScopedLeaseInstance is not { EntryPreservesAdmission: true } instance) return true;

            AdmissionOriginQueryContext query = new(proof);

            bool Carries(ExpressionSyntax expression) =>
                AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default), query)
                    .Contains(instance.Origin) || !query.Complete;

            // Declaration headers cannot execute when this selected body is called.
            // Charge every executable body node under the supplied proof, including
            // declarations/captures inside that body and all conservative escape cuts.
            SyntaxNode? body = member.Syntax switch
            {
                MethodDeclarationSyntax method => (SyntaxNode?)method.ExpressionBody ?? method.Body,
                AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.ExpressionBody ?? accessor.Body,
                PropertyDeclarationSyntax property => property.ExpressionBody,
                _ => null,
            };

            if (body is null) return true;

            foreach (SyntaxNode node in PerUnitVisited(body.DescendantNodesAndSelf(node => node == body
                || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax), proof))
            {
                if (node.Span.End >= position) continue;

                if (node is LocalFunctionStatementSyntax) continue;

                if (node is AnonymousFunctionExpressionSyntax or BaseObjectCreationExpressionSyntax) return true;

                if (node is ExpressionSyntax expression)
                {
                    IOperation? operation = member.Model.GetOperation(expression);

                    // No second getter, conversion, dynamic or delegate interpreter.
                    if (operation is IPropertyReferenceOperation or IConversionOperation
                        or IDynamicInvocationOperation or IDynamicMemberReferenceOperation
                        || member.Model.GetTypeInfo(expression).ConvertedType?.TypeKind == TypeKind.Delegate) return true;

                    if (Carries(expression)
                        && !(expression.Parent is MemberAccessExpressionSyntax access && access.Expression == expression)
                        || !query.Complete) return true;
                }

                if (node is InvocationExpressionSyntax call
                    && member.Model.GetOperation(call) is IInvocationOperation invocation)
                {
                    if (invocation.TargetMethod.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke
                        || IsScopedLeaseInstanceOperation(member, invocation.Instance)
                            && !PreservesAdmissionReceiver(invocation.TargetMethod)) return true;

                    foreach (ArgumentSyntax argument in PerUnitVisited(call.ArgumentList.Arguments, proof))
                    {
                        if (argument.Expression is not DeclarationExpressionSyntax && Carries(argument.Expression)) return true;
                    }
                }

                if (node is ReturnStatementSyntax { Expression: { } returned }
                    && !returned.IsKind(SyntaxKind.NullLiteralExpression)
                    || node is ArrowExpressionClauseSyntax arrow
                        && !arrow.Expression.IsKind(SyntaxKind.NullLiteralExpression)) return true;
            }

            return proof.RemainingWork <= 0 || !query.Complete;
        }
    }
}
