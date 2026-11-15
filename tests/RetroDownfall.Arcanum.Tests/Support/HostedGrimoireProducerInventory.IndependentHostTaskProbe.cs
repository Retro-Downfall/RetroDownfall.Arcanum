using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static HostedProducerDiscovery<HostedProducerSite> ProbeIndependentHostDirectFirst(
        CSharpCompilation compilation,
        HostedProducerOperationEntry startup,
        HostedProducerOperationEntry runtime,
        Action<HostedProducerAdmittedStateObservation> observer) =>
        new ProducerGraph([compilation], DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, admittedStateObserver: observer)
            .ProbeIndependentHostDirectFirst(startup, runtime);

    private sealed partial class ProducerGraph
    {
        internal HostedProducerDiscovery<HostedProducerSite> ProbeIndependentHostDirectFirst(HostedProducerOperationEntry startup, HostedProducerOperationEntry runtime)
        {
            PlannedRoot[] plans = PrepareRoots(new([startup.EnclosingType], []), [new(startup.EnclosingType, [startup, runtime])], []);

            PlannedRoot direct = plans.Single(plan => !plan.Lifecycle && plan.Operation == runtime.OperationId);

            PlannedRoot start = plans.Single(plan => plan.Lifecycle && plan.Operation == startup.EnclosingType + ".StartAsync");

            TraversePlannedRoot(direct);

            TraversePlannedRoot(start);

            CompletePrimaryRoots(null);

            return CompleteDiscovery();
        }
    }
}
