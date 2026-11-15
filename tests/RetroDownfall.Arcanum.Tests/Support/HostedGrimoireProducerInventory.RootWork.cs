namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The physical lease and the registered operation's admission have independent lifetimes.
        // Keeping them in one immutable value makes existing ownership transfers and clears atomic.
        private readonly record struct WorkAdmissionEvidence(string? Physical, string? RegisteredRoot)
        {
            internal static WorkAdmissionEvidence Both(string origin) => new(origin, origin);

            internal static WorkAdmissionEvidence? Create(string? physical, string? registeredRoot) =>
                physical is null && registeredRoot is null ? null : new WorkAdmissionEvidence(physical, registeredRoot);
        }

        private static WorkAdmissionEvidence? CommonWorkFrontier(IEnumerable<WorkAdmissionEvidence?> frontiers)
        {
            WorkAdmissionEvidence?[] all = [.. frontiers];

            return all.Length != 0 && all.All(frontier => frontier == all[0]) ? all[0] : null;
        }
    }
}
