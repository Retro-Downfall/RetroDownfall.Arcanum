using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The pinned policy base constructor only chains to Object. AddPolicy validates
        // and stores a policy; Process/ProcessAsync belong to a later physical send.
        // Receiver/argument and authored constructor traversal remain independent.
        private static bool IsReviewedSdkPolicyStorageMember(IMethodSymbol method) =>
            IsExactSdkPolicyRegistration(method)
                || method is { MethodKind: MethodKind.Constructor, IsStatic: false, Arity: 0, Parameters.Length: 0 }
                    && IsExactFrameworkType(method.ContainingType, "System.ClientModel.Primitives.PipelinePolicy", typeof(System.ClientModel.Primitives.PipelinePolicy).Assembly.GetName());

        private static bool IsExactSdkPolicyRegistration(IMethodSymbol method) =>
            method is { MethodKind: MethodKind.Ordinary, IsStatic: false, IsVirtual: false, Arity: 0, Name: "AddPolicy", ReturnsVoid: true }
                && IsExactFrameworkType(method.ContainingType, "System.ClientModel.Primitives.ClientPipelineOptions", typeof(System.ClientModel.Primitives.ClientPipelineOptions).Assembly.GetName())
                && method.Parameters is [{ RefKind: RefKind.None } policy, { RefKind: RefKind.None } position]
                && IsExactFrameworkType(policy.Type, "System.ClientModel.Primitives.PipelinePolicy", typeof(System.ClientModel.Primitives.PipelinePolicy).Assembly.GetName())
                && IsExactFrameworkType(position.Type, "System.ClientModel.Primitives.PipelinePosition", typeof(System.ClientModel.Primitives.PipelinePosition).Assembly.GetName());

        private static bool IsExactSdkPolicyRegistrationReceiver(ExpressionSyntax reference, AuthoredMember owner) =>
            reference.Parent is MemberAccessExpressionSyntax access && access.Expression == reference
                && access.Parent is InvocationExpressionSyntax call
                && owner.Model.GetOperation(call) is IInvocationOperation invocation
                && invocation.Instance?.Syntax == reference
                && IsExactSdkPolicyRegistration(invocation.TargetMethod)
                && invocation.Arguments.All(ArgumentBoundaryUsesOnlyBuiltInConversions);
    }
}
