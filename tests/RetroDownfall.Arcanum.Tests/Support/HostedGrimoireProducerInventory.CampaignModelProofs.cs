using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private bool IsReviewedCampaignModelIndexer(IPropertySymbol property, bool mutation,
            SyntaxNode? node, SemanticModel? model, AuthoredMember? context)
        {
            if (mutation || !property.IsIndexer || property.SetMethod is not null
                || property.Parameters is not [IParameterSymbol { Type.SpecialType: SpecialType.System_Int32 }]
                || !IsExactFrameworkType(property.ContainingType, "System.Collections.Generic.IReadOnlyList`1",
                    typeof(IReadOnlyList<>).Assembly.GetName())
                || property.ContainingType.TypeArguments is not [ITypeSymbol entry]
                || !IsCampaignConfigurationType(entry, "ModelEntry")
                || node is not ElementAccessExpressionSyntax access || model is null || context is null)
            {
                return false;
            }

            return HasCampaignModelValue(context, access.Expression, CampaignModelValueKind.Models,
                new CleanupProvenanceContext { CollectionIndexerProof = true }, new());
        }

        private enum CampaignModelValueKind
        {
            Models,
            Provider,
            Providers,
            Settings,
        }

        private bool HasCampaignModelValue(AuthoredMember member, ExpressionSyntax expression, CampaignModelValueKind kind,
            CleanupProvenanceContext provenance, HashSet<(SyntaxTree Tree, int Start, int Length, CampaignModelValueKind Kind)> path)
        {
            if (!TryStripCampaignEncodingValue(expression, member.Model, out expression))
            {
                return false;
            }

            if (!path.Add((expression.SyntaxTree, expression.SpanStart, expression.Span.Length, kind)))
            {
                return false;
            }

            try
            {
                if (expression is BinaryExpressionSyntax coalesce && coalesce.IsKind(SyntaxKind.CoalesceExpression))
                {
                    return HasCampaignModelValue(member, coalesce.Left, kind, provenance, path)
                        && HasCampaignModelValue(member, coalesce.Right, kind, provenance, path);
                }

                if (kind == CampaignModelValueKind.Models
                    && IsReviewedFrameworkCollectionTerminal(member, expression, provenance,
                        "System.Collections.Generic.IReadOnlyList`1"))
                {
                    return true;
                }

                if (kind == CampaignModelValueKind.Providers
                    && expression is CollectionExpressionSyntax { Elements.Count: 0 }
                    && member.Model.GetTypeInfo(expression).ConvertedType is IArrayTypeSymbol { ElementType: { } emptyProviderType }
                    && IsCampaignConfigurationType(emptyProviderType, "ProviderSettings"))
                {
                    return true;
                }

                if (expression is MemberAccessExpressionSyntax propertyAccess
                    && member.Model.GetSymbolInfo(propertyAccess).Symbol is IPropertySymbol selectedProperty
                    && selectedProperty.GetMethod is { IsVirtual: false } && !selectedProperty.IsStatic)
                {
                    if (kind == CampaignModelValueKind.Models && selectedProperty.Name == "Models"
                        && IsCampaignConfigurationType(selectedProperty.ContainingType, "ProviderSettings"))
                    {
                        return HasCampaignModelValue(member, propertyAccess.Expression, CampaignModelValueKind.Provider, provenance, path);
                    }

                    if (kind == CampaignModelValueKind.Providers && selectedProperty.Name == "Providers"
                        && IsCampaignConfigurationType(selectedProperty.ContainingType, "ArcanumSettings"))
                    {
                        return HasCampaignModelValue(member, propertyAccess.Expression, CampaignModelValueKind.Settings, provenance, path);
                    }
                }

                if (kind == CampaignModelValueKind.Provider && expression is ElementAccessExpressionSyntax selectedProvider
                    && member.Model.GetTypeInfo(selectedProvider.Expression).Type is IArrayTypeSymbol { ElementType: { } providerType }
                    && IsCampaignConfigurationType(providerType, "ProviderSettings"))
                {
                    return HasCampaignModelValue(member, selectedProvider.Expression, CampaignModelValueKind.Providers, provenance, path);
                }

                if (kind is CampaignModelValueKind.Provider or CampaignModelValueKind.Settings
                    && IsExactCampaignJsonMaterialization(member, expression,
                        kind == CampaignModelValueKind.Provider ? "ProviderSettings" : "ArcanumSettings"))
                {
                    return true;
                }

                ISymbol? storage = member.Model.GetSymbolInfo(expression).Symbol;

                if (storage is IParameterSymbol && member.ValueBindings?.TryGetValue(storage, out BoundValueSource? bound) == true
                    && bound is not null)
                {
                    return HasCampaignModelValue(bound.Caller, bound.Expression, kind, provenance, path);
                }

                if (storage is ILocalSymbol local)
                {
                    return HasStableCampaignModelOwnership(member, local, new HashSet<(SyntaxTree, int, int)>())
                        && HasOnlyReviewedValueSources(expression, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member,
                            (value, _, _, caller) => caller is not null && HasCampaignModelValue(caller, value, kind, provenance, path));
                }

                if (kind == CampaignModelValueKind.Settings && expression is MemberAccessExpressionSyntax recordAccess
                    && member.Model.GetSymbolInfo(recordAccess).Symbol is IPropertySymbol recordProperty
                    && recordProperty.ContainingType is { IsSealed: true, IsRecord: true }
                    && recordProperty.SetMethod is { IsInitOnly: true }
                    && recordProperty.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is ParameterSyntax))
                {
                    if (!HasStableCampaignRecordReceiver(member, recordAccess.Expression, new()))
                    {
                        return false;
                    }

                    CleanupValueFlow receivers = CleanupValueFlowOf(member, recordAccess.Expression, provenance);

                    return receivers.Complete && receivers.Values.Count != 0 && receivers.Values.All(receiver =>
                        receiver.Caller.Model.GetOperation(StripTransparentExpression(receiver.Expression)) is IObjectCreationOperation creation
                        && SymbolEqualityComparer.Default.Equals(creation.Type, recordProperty.ContainingType)
                        && IsClosedCampaignRecordConstruction(creation)
                        && creation.Arguments.SingleOrDefault(argument => argument.Parameter?.Name == recordProperty.Name) is { } argument
                        && argument.Value.Syntax is ExpressionSyntax value
                        && HasCampaignModelValue(receiver.Caller, value, kind, provenance, path));
                }

                CleanupValueFlow flow = CleanupValueFlowOf(member, expression, provenance);

                return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                    HasCampaignModelValue(value.Caller, value.Expression, kind, provenance, path));
            }
            finally
            {
                path.Remove((expression.SyntaxTree, expression.SpanStart, expression.Span.Length, kind));
            }
        }

        private bool HasStableCampaignRecordReceiver(AuthoredMember member, ExpressionSyntax expression,
            HashSet<(SyntaxTree Tree, int Start, int Length)> path)
        {
            if (!TryStripCampaignEncodingValue(expression, member.Model, out expression))
            {
                return false;
            }

            var identity = (expression.SyntaxTree, expression.SpanStart, expression.Span.Length);

            if (path.Count >= ReceiverCleanupForwardingMaximumDepth || !path.Add(identity))
            {
                return false;
            }

            try
            {
                ISymbol? storage = member.Model.GetSymbolInfo(expression).Symbol;

                if (storage is ILocalSymbol or IParameterSymbol
                    && !HasStableCampaignModelOwnership(member, storage, new()))
                {
                    return false;
                }

                if (storage is IParameterSymbol && member.ValueBindings?.TryGetValue(storage, out BoundValueSource? bound) == true
                    && bound is not null)
                {
                    return HasStableCampaignRecordReceiver(bound.Caller, bound.Expression, path);
                }

                if (storage is ILocalSymbol)
                {
                    return HasOnlyReviewedValueSources(expression, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member,
                        (value, _, _, caller) => caller is not null && HasStableCampaignRecordReceiver(caller, value, path));
                }

                if (member.Model.GetOperation(expression) is IObjectCreationOperation creation)
                {
                    return IsClosedCampaignRecordConstruction(creation);
                }

                CleanupValueFlow flow = CleanupValueFlowOf(member, expression, new CleanupProvenanceContext { CollectionIndexerProof = true });

                return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                    HasStableCampaignRecordReceiver(value.Caller, value.Expression, path));
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private bool HasStableCampaignModelOwnership(AuthoredMember member, ISymbol owner,
            HashSet<(SyntaxTree Tree, int Member, int Parameter)> active)
        {
            int parameterIndex = owner is IParameterSymbol parameter ? parameter.Ordinal : owner.Locations.FirstOrDefault()?.SourceSpan.Start ?? -1;

            var identity = (member.Syntax.SyntaxTree, member.Syntax.SpanStart, parameterIndex);

            if (active.Count >= ReceiverCleanupForwardingMaximumDepth || !active.Add(identity))
            {
                return false;
            }

            try
            {
                HashSet<ISymbol> aliases = new(SymbolEqualityComparer.Default) { owner };

                HashSet<(SyntaxTree Tree, int Start)> ownedOutputArguments = [];

                bool ReferencesAlias(SyntaxNode node) => node.DescendantNodesAndSelf().OfType<ExpressionSyntax>()
                    .Any(expression => member.Model.GetSymbolInfo(expression).Symbol is { } symbol && aliases.Contains(symbol));

                bool changed;

                do
                {
                    changed = false;

                    foreach (VariableDeclaratorSyntax declaration in member.Syntax.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
                    {
                        if (declaration.Initializer is { Value: { } value } && ReferencesAlias(value)
                            && member.Model.GetDeclaredSymbol(declaration) is ILocalSymbol local
                            && IsCampaignModelReferenceType(local.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                        {
                            changed |= aliases.Add(local);
                        }
                    }

                    foreach (AssignmentExpressionSyntax assignment in member.Syntax.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
                    {
                        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                            || !ReferencesAlias(assignment.Right)
                            || member.Model.GetTypeInfo(assignment.Right).Type is not { } assignedType
                            || !IsCampaignModelReferenceType(assignedType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                        {
                            continue;
                        }

                        // A separate assignment can publish the same mutable configuration owner.
                        // Follow local aliases; storing it elsewhere loses the exclusive receiver proof.
                        if (member.Model.GetSymbolInfo(assignment.Left).Symbol is not ILocalSymbol local
                            || assignment.Left is not IdentifierNameSyntax)
                        {
                            if (IsTerminalCampaignModelOutPublication(member, assignment))
                            {
                                continue;
                            }

                            return false;
                        }

                        changed |= aliases.Add(local);
                    }

                    // A selector can return an alias of the input owner. Track that output
                    // across the whole member: loops and deferred reads can revisit it later.
                    foreach (InvocationExpressionSyntax call in member.Syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                    {
                        if (!call.ArgumentList.Arguments.Any(argument => argument.RefKindKeyword.IsKind(SyntaxKind.None)
                                && ReferencesAlias(argument.Expression)
                                && member.Model.GetTypeInfo(argument.Expression).Type is { } inputType
                                && IsCampaignModelReferenceType(inputType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                            || member.Model.GetOperation(call) is not IInvocationOperation invocation
                            || ResolveInvocationTarget(invocation.TargetMethod, member, call) is not { Symbol.IsExtern: false, Symbol.IsAbstract: false } target)
                        {
                            continue;
                        }

                        foreach (ArgumentSyntax output in call.ArgumentList.Arguments.Where(static argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                        {
                            ISymbol? outputStorage = output.Expression is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax designation }
                                ? member.Model.GetDeclaredSymbol(designation)
                                : member.Model.GetSymbolInfo(output.Expression).Symbol;

                            ITypeSymbol? outputType = outputStorage switch
                            {
                                ILocalSymbol local => local.Type,
                                IParameterSymbol outputParameter => outputParameter.Type,
                                _ => member.Model.GetTypeInfo(output.Expression).Type,
                            };

                            if (outputType is null || !IsCampaignModelReferenceType(outputType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                            {
                                continue;
                            }

                            if (outputStorage is not ILocalSymbol
                                && (outputStorage is not IParameterSymbol { RefKind: RefKind.Out } forwarded
                                    || !SymbolEqualityComparer.Default.Equals(forwarded.ContainingSymbol, member.Symbol)
                                    || !HasClosedCampaignModelSelectorScope(member)
                                    || !HasClosedCampaignModelSelectorScope(target)))
                            {
                                return false;
                            }

                            changed |= aliases.Add(outputStorage!);

                            ownedOutputArguments.Add((output.SyntaxTree, output.SpanStart));
                        }
                    }
                } while (changed);

                if (member.Syntax.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.Left is not IdentifierNameSyntax
                        && ReferencesAlias(assignment.Left))
                    || member.Syntax.DescendantNodesAndSelf().OfType<RefExpressionSyntax>()
                        .Any(reference => ReferencesAlias(reference.Expression)))
                {
                    return false;
                }

                foreach (ArgumentSyntax argument in member.Syntax.DescendantNodesAndSelf().OfType<ArgumentSyntax>())
                {
                    if (!ReferencesAlias(argument.Expression)
                        || member.Model.GetTypeInfo(argument.Expression).Type is not { } argumentType
                        || !IsCampaignModelReferenceType(argumentType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                    {
                        continue;
                    }

                    if (argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword)
                    {
                        if (argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                            && ownedOutputArguments.Contains((argument.SyntaxTree, argument.SpanStart)))
                        {
                            continue;
                        }

                        return false;
                    }

                    if (HasIndependentCampaignModelCloneRebinding(member, owner, argument.Expression))
                    {
                        continue;
                    }

                    if (argument.Parent?.Parent is BaseObjectCreationExpressionSyntax construction
                        && member.Model.GetOperation(construction) is IObjectCreationOperation record
                        && IsClosedCampaignRecordConstruction(record))
                    {
                        continue;
                    }

                    if (argument.Parent?.Parent is not InvocationExpressionSyntax call
                        || member.Model.GetOperation(call) is not IInvocationOperation invocation)
                    {
                        return false;
                    }

                    if (invocation.TargetMethod is { Name: "SerializeToUtf8Bytes", IsStatic: true, Arity: 1 } serializer
                        && IsExactFrameworkType(serializer.ContainingType, "System.Text.Json.JsonSerializer", typeof(JsonSerializer).Assembly.GetName())
                        && invocation.Arguments.Length == 2
                        && invocation.Arguments[1].Value.Syntax is MemberAccessExpressionSyntax metadata
                        && HasExactCampaignJsonMetadata(member, metadata, argumentType.Name))
                    {
                        continue;
                    }

                    if (ResolveInvocationTarget(invocation.TargetMethod, member, call) is not { } target
                        || target.Symbol.IsExtern || target.Symbol.IsAbstract
                        || member.Model.GetOperation(argument) is not IArgumentOperation { Parameter: { } input }
                        || input.Ordinal >= target.Symbol.Parameters.Length
                        || !HasStableCampaignModelOwnership(target, target.Symbol.Parameters[input.Ordinal], active))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                active.Remove(identity);
            }
        }

        private static bool IsTerminalCampaignModelOutPublication(AuthoredMember member, AssignmentExpressionSyntax assignment)
        {
            if (!HasClosedCampaignModelSelectorScope(member)
                || member.Model.GetSymbolInfo(assignment.Left).Symbol is not IParameterSymbol { RefKind: RefKind.Out } output
                || !SymbolEqualityComparer.Default.Equals(output.ContainingSymbol, member.Symbol)
                || !IsCampaignModelReferenceType(output.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))
                || assignment.Parent is not ExpressionStatementSyntax statement
                || statement.Parent is not BlockSyntax block)
            {
                return false;
            }

            int index = block.Statements.IndexOf(statement);

            for (int next = index + 1; next < block.Statements.Count; next++)
            {
                if (block.Statements[next] is ReturnStatementSyntax { Expression: LiteralExpressionSyntax literal }
                    && literal.IsKind(SyntaxKind.TrueLiteralExpression) && next == block.Statements.Count - 1)
                {
                    return true;
                }

                if (block.Statements[next] is not ExpressionStatementSyntax
                    { Expression: AssignmentExpressionSyntax scalar } || !scalar.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || member.Model.GetSymbolInfo(scalar.Left).Symbol is not IParameterSymbol { RefKind: RefKind.Out, Type.SpecialType: SpecialType.System_String } scalarOutput
                    || !SymbolEqualityComparer.Default.Equals(scalarOutput.ContainingSymbol, member.Symbol)
                    || scalar.Right is not IdentifierNameSyntax identifier
                    || member.Model.GetTypeInfo(identifier).Type?.SpecialType != SpecialType.System_String
                    || member.Model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol and not IParameterSymbol)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool HasClosedCampaignModelSelectorScope(AuthoredMember member) =>
            member.Symbol.IsStatic && !member.Symbol.IsAsync
            && member.Symbol.ReturnType.SpecialType == SpecialType.System_Boolean
            && !member.Syntax.DescendantNodesAndSelf().Any(static node => node is TryStatementSyntax
                or UsingStatementSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                || node is LocalDeclarationStatementSyntax declaration && !declaration.UsingKeyword.IsKind(SyntaxKind.None));

        private static bool HasIndependentCampaignModelCloneRebinding(AuthoredMember member, ISymbol owner, ExpressionSyntax expression)
        {
            if (!TryStripCampaignEncodingValue(expression, member.Model, out expression)
                || expression is not IdentifierNameSyntax
                || member.Model.GetSymbolInfo(expression).Symbol is not ILocalSymbol local
                || SymbolEqualityComparer.Default.Equals(local, owner)
                || !IsCampaignConfigurationType(local.Type, "ProviderSettings")
                    && !IsCampaignConfigurationType(local.Type, "ArcanumSettings")
                || expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault() is not { Parent: BlockSyntax block } statement
                || member.Syntax.DescendantNodesAndSelf().Any(static node => node is GotoStatementSyntax
                    or TryStatementSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                return false;
            }

            bool NamesLocal(ExpressionSyntax value) => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(value).Symbol, local);

            bool WritesLocal(SyntaxNode node) => node.DescendantNodesAndSelf().Any(candidate =>
                candidate is AssignmentExpressionSyntax assignment && NamesLocal(assignment.Left)
                || candidate is VariableDeclaratorSyntax declaration && SymbolEqualityComparer.Default.Equals(member.Model.GetDeclaredSymbol(declaration), local)
                || candidate is ArgumentSyntax argument && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword && NamesLocal(argument.Expression));

            if (WritesLocal(statement))
            {
                return false;
            }

            for (int index = block.Statements.IndexOf(statement) - 1; index >= 0; index--)
            {
                StatementSyntax previous = block.Statements[index];

                if (!WritesLocal(previous))
                {
                    continue;
                }

                ExpressionSyntax? value = previous switch
                {
                    ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                        when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left is IdentifierNameSyntax && NamesLocal(assignment.Left) => assignment.Right,
                    LocalDeclarationStatementSyntax { Declaration.Variables: [VariableDeclaratorSyntax declaration] }
                        when SymbolEqualityComparer.Default.Equals(member.Model.GetDeclaredSymbol(declaration), local) => declaration.Initializer?.Value,
                    _ => null,
                };

                return value is not null && TryStripCampaignEncodingValue(value, member.Model, out value)
                    && IsExactCampaignJsonMaterialization(member, value, local.Type.Name);
            }

            return false;
        }

        private static bool IsCampaignModelReferenceType(ITypeSymbol type, HashSet<ITypeSymbol> path)
        {
            if (!path.Add(type))
            {
                return false;
            }

            return IsCampaignConfigurationType(type, "ProviderSettings") || IsCampaignConfigurationType(type, "ArcanumSettings")
                || type is IArrayTypeSymbol array && IsCampaignModelReferenceType(array.ElementType, path)
                || type is INamedTypeSymbol { IsSealed: true, IsRecord: true } record
                    && record.GetMembers().OfType<IPropertySymbol>().Any(property => !property.IsStatic
                        && property.SetMethod is { IsInitOnly: true } && IsCampaignModelReferenceType(property.Type, path));
        }

        private static bool IsClosedCampaignRecordConstruction(IObjectCreationOperation creation) =>
            creation.Type is INamedTypeSymbol { IsSealed: true, IsRecord: true } record
            && creation.Initializer is null
            && record.DeclaringSyntaxReferences is [SyntaxReference declarationReference]
            && declarationReference.GetSyntax() is RecordDeclarationSyntax { ParameterList: not null, Members.Count: 0, BaseList: null } declaration
            && creation.Constructor is { DeclaringSyntaxReferences: [SyntaxReference constructorReference] }
            && constructorReference.SyntaxTree == declaration.SyntaxTree && constructorReference.Span == declaration.Span
            && record.GetMembers().OfType<IPropertySymbol>().All(static property => property.IsStatic
                || property.SetMethod is null or { IsInitOnly: true });

        private static bool IsCampaignConfigurationType(ITypeSymbol type, string name) =>
            TypeKey(type) == "RetroDownfall.Arcanum.Core.Configuration." + name
            && AssemblyIdentityMatches(type.ContainingAssembly.Identity,
                typeof(RetroDownfall.Arcanum.Core.Configuration.ProviderSettings).Assembly.GetName())
            && type is INamedTypeSymbol { IsSealed: true };

        private static bool IsExactCampaignJsonMaterialization(AuthoredMember member, ExpressionSyntax expression, string name)
        {
            if (expression is not InvocationExpressionSyntax call
                || member.Model.GetOperation(call) is not IInvocationOperation invocation
                || invocation.TargetMethod is not { Name: "Deserialize", IsStatic: true, Arity: 1 } method
                || !IsExactFrameworkType(method.ContainingType, "System.Text.Json.JsonSerializer", typeof(JsonSerializer).Assembly.GetName())
                || method.TypeArguments is not [ITypeSymbol destination] || !IsCampaignConfigurationType(destination, name)
                || method.Parameters.Length != 2
                || invocation.Arguments.SingleOrDefault(static argument =>
                    TypeKey(argument.Parameter!.Type) == "System.Text.Json.Serialization.Metadata.JsonTypeInfo`1")
                    ?.Value.Syntax is not MemberAccessExpressionSyntax metadata
                || !HasExactCampaignJsonMetadata(member, metadata, name))
            {
                return false;
            }

            return true;
        }

        private static bool HasExactCampaignJsonMetadata(AuthoredMember member, MemberAccessExpressionSyntax metadata, string name)
        {
            if (metadata.Name.Identifier.ValueText != name
                || metadata.Expression is not MemberAccessExpressionSyntax defaultContext
                || member.Model.GetSymbolInfo(metadata).Symbol is not IPropertySymbol metadataProperty
                || member.Model.GetSymbolInfo(defaultContext).Symbol is not IPropertySymbol { Name: "Default", IsStatic: true } contextProperty
                || !SymbolEqualityComparer.Default.Equals(metadataProperty.ContainingType, contextProperty.ContainingType))
            {
                return false;
            }

            string contextType = TypeKey(metadataProperty.ContainingType);

            return contextType == "RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext"
                    && AssemblyIdentityMatches(metadataProperty.ContainingAssembly.Identity,
                        typeof(RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext).Assembly.GetName())
                || contextType == "RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext"
                    && AssemblyIdentityMatches(metadataProperty.ContainingAssembly.Identity,
                        typeof(RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext).Assembly.GetName());
        }
    }
}
