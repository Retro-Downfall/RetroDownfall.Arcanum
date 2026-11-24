using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private const string WorkspaceFenceType = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceMutationFence";

    private const string WorkspaceOperatorType = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildOperatorWorkspaceWrite";

    private const string WorkspaceOperatorCall = WorkspaceOperatorType + ".Call";

    internal static bool HasExclusiveOrdinaryOperatorIngress(IReadOnlyList<CSharpCompilation> compilations) =>
        new InventoryFacts(compilations).ExclusiveOrdinaryOperatorIngress;

    private static bool ScanExclusiveOrdinaryOperatorIngress(IReadOnlyList<CSharpCompilation> compilations,
        Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf)
    {
        CSharpCompilation? infrastructure = compilations.SingleOrDefault(static compilation =>
            compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol? fence = infrastructure?.GetTypeByMetadataName(WorkspaceFenceType);

        // The private owner publishes captured invocations and restores the prior value on scope disposal.
        // Pin that bounded source shape so new publishers, aliases or writes to the forwarded call parameter
        // withhold the premise even when invocation call sites themselves are unchanged.
        const string fenceShape = "42BC5A119217C591E2CB3ACD97471DEC22CF5DBA272D4F3AC877A5F8823F464F";

        if (fence is not { IsSealed: true, DeclaringSyntaxReferences: [var declaration] }
            || Fingerprint(declaration.GetSyntax()) != fenceShape)
        {
            return false;
        }

        int operatorBindings = 0;

        int questBindings = 0;

        foreach (CSharpCompilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                SemanticModel? model = null;

                foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (name.Identifier.ValueText != "EnterInvocation")
                    {
                        continue;
                    }

                    model ??= modelOf(compilation, tree);

                    SymbolInfo reference = model.GetSymbolInfo(name);

                    if (reference.Symbol is null)
                    {
                        return false;
                    }

                    if (reference.Symbol is not IMethodSymbol method
                        || Normalize(method) != WorkspaceFenceType + ".EnterInvocation")
                    {
                        continue;
                    }

                    if (name.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } call
                        || model.GetOperation(call) is not IInvocationOperation action
                        || Normalize(action.TargetMethod) != Normalize(method)
                        || model.GetEnclosingSymbol(name.SpanStart) is not IMethodSymbol caller)
                    {
                        return false;
                    }

                    IArgumentOperation? argument = action.Arguments.SingleOrDefault(static argument =>
                        argument.Parameter?.Name == "operatorCall");

                    if (Normalize(caller) == WorkspaceOperatorType + ".Enter"
                        && argument?.Value is IParameterReferenceOperation { Parameter.Name: "call" } sourceCall
                        && TypeKey(sourceCall.Parameter.Type) == WorkspaceOperatorCall)
                    {
                        operatorBindings++;
                    }
                    else if (Normalize(caller) == "RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceMutationTools.ExecuteAsync"
                        && argument?.Value.ConstantValue is { HasValue: true, Value: null })
                    {
                        questBindings++;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }

        return operatorBindings == 1 && questBindings == 1;
    }

    private sealed partial class ProducerGraph
    {
        private static bool HasNoOrdinaryOperatorAtEntry(string operation) =>
            operation is
                "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync"
                or "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync"
                or "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync"
                or "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync"
                or "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync"
                or "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync";

        private bool IsAbsentOrdinaryOperatorValidation(AuthoredMember caller, InvocationExpressionSyntax call,
            AuthoredMember target)
        {
            if (Normalize(target.Symbol) != WorkspaceOperatorCall + ".ValidateAsync"
                || Normalize(caller.Symbol) != WorkspaceFenceType + ".Lease.ValidateAsync"
                || traversalCompletion?.OrdinaryOperatorAbsent != true
                || call.FirstAncestorOrSelf<IfStatementSyntax>() is not
                {
                    Condition: BinaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.LogicalAndExpression,
                        Right: IsPatternExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "OperatorCall" } access,
                            Pattern: RecursivePatternSyntax
                            {
                                Type: null,
                                PositionalPatternClause: null,
                                PropertyPatternClause.Subpatterns.Count: 0,
                                Designation: SingleVariableDesignationSyntax local
                            }
                        }
                    }
                } branch
                || !branch.Statement.Span.Contains(call.Span)
                || caller.Model.GetOperation(access) is not IPropertyReferenceOperation property
                || TypeKey(property.Property.ContainingType) != WorkspaceFenceType + ".Invocation"
                || caller.Model.GetOperation(call) is not IInvocationOperation { Instance: ILocalReferenceOperation receiver }
                || !SymbolEqualityComparer.Default.Equals(receiver.Local, caller.Model.GetDeclaredSymbol(local)))
            {
                return false;
            }

            return inventoryFacts.ExclusiveOrdinaryOperatorIngress;
        }
    }
}
