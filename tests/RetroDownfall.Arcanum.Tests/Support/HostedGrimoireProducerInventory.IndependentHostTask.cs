using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private sealed class IndependentHostCapture(string operation, string family, int occurrence)
        {
            internal string Operation { get; } = operation;

            internal string Family { get; } = family;

            internal int Occurrence { get; } = occurrence;

            internal TraversalStateIdentity? State { get; set; }

            internal TraversalCompletionFrame? Frame { get; set; }

            internal TraversalEvidence Evidence { get; set; }

            internal bool Complete { get; set; }

            internal bool Returned { get; set; }
        }

        private readonly Dictionary<TraversalStateIdentity, IndependentHostCapture> independentHostResults = [];

        private IndependentHostCapture? independentHostCapture;

        private HashSet<TraversalStateIdentity>? independentHostSafetyAncestors;

        private int independentHostDepthOffset;

        private bool TryTraverseIndependentHost(
            AuthoredMember caller,
            InvocationExpressionSyntax dispatch,
            HostHandoffProof handoff,
            AuthoredMember target,
            PlannedRoot? plan,
            bool plannedLifecycleReceiverEntry,
            HashSet<TraversalStateIdentity> active,
            out TraversalEvidence evidence)
        {
            evidence = TraversalEvidence.Inconclusive;

            if (!plannedLifecycleReceiverEntry || plan is null || activeBoundedDataProbe is not null
                || detachedCallbackOuterStates is not null || detachedSubstitutedStates is not null
                || independentHostCapture is not null || handoff.Root.LeaseFormal is not null
                || handoff.Selection != target.Syntax
                || handoff.Root.OperationId != TypeKey(target.Symbol.ContainingType) + "." + target.Symbol.Name
                || selectedRoots.ContainsKey(MemberIdentity(target))
                || analyzedStateCounts.GetValueOrDefault(handoff.RootType + "|" + RootOperation(handoff.Root.OperationId)) != 0
                || caller.Model.GetSymbolInfo(dispatch).Symbol is not IMethodSymbol scheduler
                || !IsExactFrameworkMethod(scheduler, "System.Threading.Tasks.Task", "Run", "System.Private.CoreLib")
                || caller.Model.GetOperation(dispatch) is not IInvocationOperation { Arguments.Length: 2 } scheduled
                || scheduled.Arguments[0].Value.Syntax is not AnonymousFunctionExpressionSyntax { Body: InvocationExpressionSyntax call }
                || caller.Model.GetOperation(call) is not IInvocationOperation { IsVirtual: false, Arguments.Length: 1, Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } } invocation
                || invocation.TargetMethod.IsStatic || invocation.TargetMethod.Parameters is not [IParameterSymbol { RefKind: RefKind.None } formal]
                || !IsExactCancellationToken(formal.Type)
                || !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, target.Symbol.OriginalDefinition)
                || !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, caller.Symbol.ContainingType)
                || invocation.Arguments[0].Value is not IPropertyReferenceOperation { Property.Name: "Token", Instance: IFieldReferenceOperation field } token
                || token.Property.IsStatic || token.Property.GetMethod is not { IsVirtual: false }
                || !IsExactFrameworkType(token.Property.ContainingType, "System.Threading.CancellationTokenSource", typeof(CancellationTokenSource).Assembly.GetName())
                || !field.Field.IsReadOnly || field.Field.IsStatic
                || !SymbolEqualityComparer.Default.Equals(field.Field.ContainingType, caller.Symbol.ContainingType)
                || field.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                || !IsExactFrameworkType(field.Field.Type, "System.Threading.CancellationTokenSource", typeof(CancellationTokenSource).Assembly.GetName())
                || scheduled.Arguments[1].Value is not IPropertyReferenceOperation { Property.Name: "None", Property.IsStatic: true } none
                || !IsExactCancellationToken(none.Property.ContainingType))
            {
                return false;
            }

            bool leaseFamily = leaseHandoffRootTypes.Values.Contains(handoff.RootType);

            IndependentHostCapture capture = new(handoff.Root.OperationId,
                HostedProducerRootWorkers.FamilyOf(handoff.RootType, RootOperation(handoff.Root.OperationId), leaseFamily), plan.Occurrence);

            HashSet<TraversalStateIdentity>? previousSafety = independentHostSafetyAncestors;

            int previousOffset = independentHostDepthOffset;

            // The scheduled task has its own authority stack. Keep the original
            // lifecycle lane for its actual census and cache partition.
            independentHostSafetyAncestors = [.. active];

            independentHostDepthOffset += active.Count;

            independentHostCapture = capture;

            try
            {
                evidence = Traverse(target, handoff.RootType, handoff.Root.OperationId, [], lifecycle: true, selection: handoff.Selection);

                if (capture.Returned && capture.State is { EvaluationEnvironmentToken: not null } state)
                {
                    independentHostResults.TryAdd(state, capture);
                }

                return true;
            }
            finally
            {
                independentHostCapture = null;

                independentHostSafetyAncestors = previousSafety;

                independentHostDepthOffset = previousOffset;
            }
        }
    }
}
