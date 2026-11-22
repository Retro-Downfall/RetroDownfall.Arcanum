using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private const string GuildNamespace = "RetroDownfall.Arcanum.Infrastructure.Guild.";
    private const string GuildWorkspaceLease = GuildNamespace + "GuildWorkspaceMutationFence.Lease";
    private const string GuildWorkspaceInvocation = GuildNamespace + "GuildWorkspaceMutationFence.Invocation";
    private const string GuildToolAdmission = GuildNamespace + "GuildChildToolAdmission";
    private const string GuildRevalidationOwner = GuildNamespace + "GuildQuestStore";
    private const string GuildRevalidationMethod = "RevalidateWorkspaceMutationToolAsync";
    private const string GuildAuthorizationMethod = "AuthorizeLocalToolAsync";
    private const string GuildAuthorizationOperation = GuildRevalidationOwner + "." + GuildAuthorizationMethod;
    private const string GuildAuthorizationSource = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.ToolEffects.cs";

    private static HostedProducerOperationEntry GuildRevalidationRoot(string sourcePath, IReadOnlyList<HostedProducerSite> sites) => new(
        GuildAuthorizationOperation, sourcePath, GuildRevalidationOwner, GuildAuthorizationMethod,
        HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.GuildStorage, null, sites);

    internal static bool IsGuildRevalidationChildRoot(string rootType, string operation) =>
        rootType == GuildRevalidationOwner && operation == GuildAuthorizationOperation;

    internal static bool IsGuildRevalidationChildSite(HostedProducerSite site) =>
        IsGuildRevalidationChildRoot(site.RootType, site.AuthorityOperationId);

    internal static bool IsGuildRevalidationJoinedEdge(HostedProducerSite site) =>
        site.Kind == HostedProducerSiteKind.JoinedSelfAdmission
        && site.EnclosingType == GuildRevalidationOwner && site.Member == GuildRevalidationMethod
        && site.Callee == GuildAuthorizationOperation && !IsGuildRevalidationChildSite(site)
        && site.WorkFrontierCapsuleId is null && site.RegisteredRootWorkFrontierCapsuleId is null
        && site.EffectFrontierCapsuleId is null;

    private static void ValidateManifestJoinedSelfAdmissions(
        IReadOnlyList<HostedProducerSite> sites, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        HostedProducerSite[] children = [.. sites.Where(IsGuildRevalidationChildSite)];

        HostedProducerSite[] links = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.JoinedSelfAdmission)];

        if ((children.Length > 0) != (links.Length > 0))
        {
            diagnostics.Add(new("HOSTED_MANIFEST_JOINED_ADMISSION_UNBOUND", GuildAuthorizationOperation,
                "The ordinary Work suboperation and its exact joined caller edges must both be present."));
        }

        foreach (HostedProducerSite edge in links.Where(static site => !IsGuildRevalidationJoinedEdge(site)))
        {
            diagnostics.Add(new("HOSTED_MANIFEST_JOINED_ADMISSION_EDGE_INVALID", StableSiteIdentity(edge),
                "The joined edge must name the bound Guild workspace lease call and its independent Work owner."));
        }

        HashSet<string?> guildWork = children
            .Where(static site => site.Kind == HostedProducerSiteKind.EffectFrontier
                && IsWorkFrontierCallee(site.Callee) && site.AdmittedWorkKind == GrimoireWorkKind.GuildStorage)
            .Select(static site => site.WorkFrontierCapsuleId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (HostedProducerSite child in children.Where(static site => site.Kind != HostedProducerSiteKind.EffectFrontier))
        {
            if (child.WorkFrontierCapsuleId is null
                || child.RegisteredRootWorkFrontierCapsuleId is not { } root || !guildWork.Contains(root)
                || RequiresExternalEffect(child.Kind, child.Callee) && child.EffectFrontierCapsuleId is null)
            {
                diagnostics.Add(new("HOSTED_MANIFEST_JOINED_ADMISSION_WORK_UNBOUND", StableSiteIdentity(child),
                    "Every protected child operation must identify its own GuildStorage Work and any required external effect."));
            }
        }
    }

    private sealed partial class ProducerGraph
    {
        private bool TryTraverseJoinedGuildWorkspaceRevalidation(
            AuthoredMember caller, InvocationExpressionSyntax call, AuthoredMember child, string rootType,
            string operationId, HashSet<TraversalStateIdentity> active, out TraversalEvidence evidence)
        {
            evidence = TraversalEvidence.NoEvidence;

            if (TypeKey(caller.Symbol.ContainingType) != GuildRevalidationOwner
                || caller.Symbol.Name != GuildRevalidationMethod
                || Normalize(child.Symbol) != GuildAuthorizationOperation
                || caller.RecoveryContext is not null || child.RecoveryContext is not null
                || caller.Model.GetOperation(call) is not IInvocationOperation
                {
                    IsVirtual: false, Instance: IInstanceReferenceOperation,
                } dispatch
                || !SymbolEqualityComparer.Default.Equals(dispatch.TargetMethod.OriginalDefinition, child.Symbol.OriginalDefinition)
                || !IsExactAuthoredProtocolType(child.Symbol.ContainingType, GuildRevalidationOwner, child.Model.Compilation)
                || !child.Symbol.ContainingType.IsSealed
                || !PreservesGuildWorkspaceRevalidationChecks(caller, call)
                || CallerInvocationForRevalidation(caller) is not { } writer
                || !OwnsJoinedGuildRevalidationCall(writer.Caller, writer.Call, caller.Symbol)
                || TryRegisterEvaluationEnvironmentToken(child) is null)
            {
                return false;
            }

            // The caller retains both ownership checks and their host-lock reads. The joined
            // authorization traverses the independently admitted database transaction with
            // the exact bound argument environment and cancellation from the original call.
            provenSelfAdmittedRoots[GuildAuthorizationOperation] = GuildRevalidationRoot(child.Syntax.SyntaxTree.FilePath, []);

            RootTraversalAnalysisCounter analysis = RootTraversalCounter(GuildRevalidationOwner, GuildAuthorizationOperation);

            analysis.SelfAdmittedWorkKind = GrimoireWorkKind.GuildStorage;

            long started = Stopwatch.GetTimestamp();

            TraversalEvidence childEvidence;

            try
            {
                childEvidence = Traverse(child, GuildRevalidationOwner, GuildAuthorizationOperation, active,
                    lifecycle: false, completionOwned: true);
            }
            finally
            {
                analysis.TraversalCalls++;

                analysis.Elapsed += Stopwatch.GetElapsedTime(started);
            }

            evidence = MergeEvidence(childEvidence,
                AddSite(HostedProducerSiteKind.JoinedSelfAdmission, GuildAuthorizationOperation, caller, rootType, operationId, call));

            return true;
        }

        private static bool RequiresGuildWorkspaceRevalidationCaller(AuthoredMember target, IParameterSymbol parameter) =>
            parameter.Ordinal == 0 && target.Symbol.Name == GuildRevalidationMethod
            && TypeKey(target.Symbol.ContainingType) == GuildRevalidationOwner
            && TypeKey(parameter.Type) == GuildToolAdmission;

        private static (AuthoredMember Caller, InvocationExpressionSyntax Call)? CallerInvocationForRevalidation(AuthoredMember member)
        {
            if (member.Symbol.Parameters is not [IParameterSymbol admitted, _]
                || member.ValueBindings?.TryGetValue(admitted, out BoundValueSource? source) != true
                || source?.Expression.Parent is not ArgumentSyntax
                {
                    Parent.Parent: InvocationExpressionSyntax invocation,
                })
            {
                return null;
            }

            return (source.Caller, invocation);
        }

        private static bool OwnsJoinedGuildRevalidationCall(
            AuthoredMember writer, InvocationExpressionSyntax call, IMethodSymbol revalidate) =>
            TypeKey(writer.Symbol.ContainingType) == GuildWorkspaceLease && writer.Symbol.Name == "ValidateAsync"
            && writer.Model.GetOperation(call) is IInvocationOperation
            {
                IsVirtual: false, Instance: { } instance,
                Arguments: [{ Value: ILocalReferenceOperation admitted }, { Value: IParameterReferenceOperation cancellation }],
            } dispatch
            && SymbolEqualityComparer.Default.Equals(dispatch.TargetMethod.OriginalDefinition, revalidate.OriginalDefinition)
            && SymbolEqualityComparer.Default.Equals(cancellation.Parameter.ContainingSymbol, writer.Symbol)
            && IsExactCancellationToken(cancellation.Parameter.Type)
            && AwaitRefusal(writer, call) is null
            && RecoveryTupleSourceIsStable(writer, admitted.Local, call, allowSimpleLocalAssignments: false)
            && RecoveryTupleSourceIsStable(writer, cancellation.Parameter, call, allowSimpleLocalAssignments: false)
            && ReadsOriginalGuildToolAdmission(writer, call, admitted.Local)
            && OriginalGuildToolStoreSource(instance) is ILocalReferenceOperation receiverAdmission
            && SymbolEqualityComparer.Default.Equals(receiverAdmission.Local, admitted.Local);

        private static IOperation? OriginalGuildToolStoreSource(IOperation instance)
        {
            IOperation? parent = ReadClosedProperty(instance, GuildNamespace + "GuildLocalInferenceExecution", "Store");
            IOperation? registry = ReadClosedProperty(parent, GuildNamespace + "GuildLocalToolRegistry", "Parent");

            return ReadClosedProperty(registry, GuildToolAdmission, "Registry");
        }

        private static IOperation? ReadClosedProperty(IOperation? value, string owner, string name) =>
            value is IPropertyReferenceOperation
            {
                Property: { IsStatic: false, SetMethod: null, ContainingType.IsSealed: true } property,
                Instance: { } instance,
            }
            && property.Name == name && TypeKey(property.ContainingType) == owner
            && property.DeclaringSyntaxReferences is [SyntaxReference authored]
            && authored.GetSyntax() is PropertyDeclarationSyntax
            {
                AccessorList.Accessors: [AccessorDeclarationSyntax { Body: null, ExpressionBody: null } access],
            }
            && access.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? instance
                : null;

        private static bool ReadsOriginalGuildToolAdmission(AuthoredMember caller, InvocationExpressionSyntax call, ILocalSymbol local)
        {
            if (TypeKey(local.Type) != GuildToolAdmission
                || local.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                || declaration.GetSyntax() is not SingleVariableDesignationSyntax
                {
                    Parent: RecursivePatternSyntax
                    {
                        Type: null, PositionalPatternClause: null, PropertyPatternClause.Subpatterns.Count: 0,
                        Parent: IsPatternExpressionSyntax pattern,
                    },
                }
                || ReadClosedProperty(caller.Model.GetOperation(pattern.Expression), GuildWorkspaceInvocation, "ToolAdmission") is null)
            {
                return false;
            }

            return call.Ancestors().OfType<IfStatementSyntax>().Any(branch =>
                branch.Statement.Span.Contains(call.Span)
                && (branch.Condition == pattern || branch.Condition is BinaryExpressionSyntax condition
                    && condition.IsKind(SyntaxKind.LogicalAndExpression) && condition.Right == pattern));
        }

        private static bool PreservesGuildWorkspaceRevalidationChecks(AuthoredMember child, InvocationExpressionSyntax authorization)
        {
            if (child.Symbol.Parameters is not [IParameterSymbol admission, IParameterSymbol cancellation]
                || TypeKey(admission.Type) != GuildToolAdmission || !IsExactCancellationToken(cancellation.Type)
                || child.Syntax is not MethodDeclarationSyntax
                {
                    Body.Statements:
                    [
                        IfStatementSyntax
                        {
                            Else: null, Condition: PrefixUnaryExpressionSyntax before,
                            Statement: ReturnStatementSyntax { Expression: { } invalid },
                        },
                        LocalDeclarationStatementSyntax
                        {
                            Declaration.Variables: [VariableDeclaratorSyntax { Initializer.Value: AwaitExpressionSyntax awaited } allowed],
                        },
                        ReturnStatementSyntax
                        {
                            Expression: ConditionalExpressionSyntax
                            {
                                Condition: { } failed, WhenTrue: { } error, WhenFalse: ConditionalExpressionSyntax after,
                            },
                        },
                    ],
                }
                || !before.IsKind(SyntaxKind.LogicalNotExpression)
                || !TestsOriginalGuildCallOwner(child, before.Operand, admission)
                || !TestsOriginalGuildCallOwner(child, after.Condition, admission)
                || !SymbolEqualityComparer.Default.Equals(child.Model.GetSymbolInfo(before.Operand).Symbol, child.Model.GetSymbolInfo(after.Condition).Symbol)
                || child.Model.GetSymbolInfo(invalid).Symbol is not IPropertySymbol invalidReceipt
                || !SymbolEqualityComparer.Default.Equals(invalidReceipt, child.Model.GetSymbolInfo(after.WhenFalse).Symbol)
                || child.Model.GetOperation(after.WhenTrue) is not IInvocationOperation { TargetMethod: { IsStatic: true, Name: "Success" } success }
                || !SymbolEqualityComparer.Default.Equals(success.ContainingType, child.Symbol.ReturnType is INamedTypeSymbol task ? task.TypeArguments.SingleOrDefault() : null)
                || child.Model.GetDeclaredSymbol(allowed) is not ILocalSymbol authorized
                || !ReadsGuildAuthorizationResult(child, failed, authorized, "IsFailure")
                || !ReadsGuildAuthorizationResult(child, error, authorized, "Error"))
            {
                return false;
            }

            return awaited.Span.Contains(authorization.Span)
                && AwaitRefusal(child, authorization) is null
                && child.Model.GetOperation(authorization) is IInvocationOperation
                {
                    IsVirtual: false, Instance: IInstanceReferenceOperation,
                    Arguments: [var registry, var tool, var eligibility, { Value: IParameterReferenceOperation token }],
                }
                && ReferencesGuildAdmissionProperty(registry.Value, admission, "Registry")
                && ReferencesGuildAdmissionTool(child, tool.Value, admission)
                && ReferencesGuildAdmissionProperty(eligibility.Value, admission, "Eligibility")
                && SymbolEqualityComparer.Default.Equals(token.Parameter, cancellation);
        }

        private static bool TestsOriginalGuildCallOwner(AuthoredMember child, ExpressionSyntax expression, IParameterSymbol admission) =>
            child.Model.GetOperation(expression) is IInvocationOperation
            {
                IsVirtual: false, Instance: IInstanceReferenceOperation,
                TargetMethod: { DeclaredAccessibility: Accessibility.Private, Name: "OwnsWorkspaceMutationCall" } method,
                Arguments: [{ Value: IParameterReferenceOperation argument }],
            }
            && SymbolEqualityComparer.Default.Equals(method.ContainingType, child.Symbol.ContainingType)
            && SymbolEqualityComparer.Default.Equals(argument.Parameter, admission);

        private static bool ReadsGuildAuthorizationResult(AuthoredMember child, ExpressionSyntax value, ILocalSymbol local, string propertyName) =>
            child.Model.GetOperation(value) is IPropertyReferenceOperation
            {
                Property: { } property, Instance: ILocalReferenceOperation read,
            }
            && property.Name == propertyName && SymbolEqualityComparer.Default.Equals(read.Local, local);

        private static bool ReferencesGuildAdmissionTool(AuthoredMember child, IOperation value, IParameterSymbol admission)
        {
            if (value is not IPropertyReferenceOperation
                {
                    Instance: IParameterReferenceOperation source,
                    Property: { Name: "Tool", IsStatic: false, SetMethod: null, ContainingType.IsSealed: true } property,
                }
                || !SymbolEqualityComparer.Default.Equals(source.Parameter, admission)
                || !SymbolEqualityComparer.Default.Equals(admission.Type, property.ContainingType)
                || property.DeclaringSyntaxReferences is not [SyntaxReference reference]
                || reference.GetSyntax() is not PropertyDeclarationSyntax { ExpressionBody.Expression: { } forwarding }
                || !child.Model.Compilation.ContainsSyntaxTree(forwarding.SyntaxTree))
            {
                return false;
            }

            SemanticModel model = child.Model.Compilation.GetSemanticModel(forwarding.SyntaxTree);

            IOperation? frame = ReadClosedProperty(model.GetOperation(forwarding),
                GuildNamespace + "GuildLocalToolRegistry.Invocation", "Tool");

            return ReadClosedProperty(frame, GuildToolAdmission, "Frame") is IInstanceReferenceOperation;
        }

        private static bool ReferencesGuildAdmissionProperty(IOperation value, IParameterSymbol admission, string propertyName) =>
            ReadClosedProperty(value, GuildToolAdmission, propertyName) is IParameterReferenceOperation argument
            && SymbolEqualityComparer.Default.Equals(argument.Parameter, admission);
    }
}
