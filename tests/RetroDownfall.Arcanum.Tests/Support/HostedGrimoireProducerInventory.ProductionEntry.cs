namespace RetroDownfall.Arcanum.Tests.Support;

internal enum HostedProducerProductionPartition : byte
{
    Primary = 1,

    Additional = 2,
}

internal sealed record ProductionRootSelection(
    HostedProducerDiscovery<string> Registrations,
    IReadOnlyList<HostedProducerServiceEntry> Catalog,
    IReadOnlyList<NonHostedProducerChainEntry> NonHostedCatalog);

internal static partial class HostedGrimoireProducerInventory
{
    // The premises every production discovery requests. Fixtures cannot name this constant's effect:
    // they reach the engine only through DiscoverProducerSites, which fixes the premises at None.
    internal const HostedProducerPremises ProductionPremises = HostedProducerPremises.SqliteSealedByUse | HostedProducerPremises.AuthoredEnumerablesAbsentByUse;

    // A root subset narrows a production run to named roots without touching any limit, scan or refusal. A
    // selector is a hosted service type name (every declared operation of that service), a declared
    // operation id, or a non-hosted chain id. A selector that names nothing is an error, because a scoped
    // run that silently analyses zero roots would look green. A Lease-Handoff Root is declared in its callers' service, so
    // selecting the service keeps the root and the callers that cut to it together; selecting only the caller's operation
    // leaves the root undeclared and traverses its closure in line, and selecting only the root's operation leaves its
    // callers unanalysed, which the Handoff Caller Census reports.
    internal static ProductionRootSelection SelectProductionRoots(
        IReadOnlyCollection<string>? rootSubset,
        HostedProducerDiscovery<string> registrations,
        IReadOnlyList<HostedProducerServiceEntry> catalog,
        IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog)
    {
        if (rootSubset is null)
        {
            return new(registrations, catalog, nonHostedCatalog);
        }

        if (rootSubset.Count == 0)
        {
            throw new ArgumentException("A root subset must name at least one root.", nameof(rootSubset));
        }

        HashSet<string> selectors = new(rootSubset, StringComparer.Ordinal);

        HashSet<string> matched = new(StringComparer.Ordinal);

        List<HostedProducerServiceEntry> services = [];

        foreach (HostedProducerServiceEntry service in catalog)
        {
            bool wholeService = selectors.Contains(service.ServiceType);

            if (wholeService)
            {
                matched.Add(service.ServiceType);
            }

            HostedProducerOperationEntry[] operations = [.. service.Operations.Where(operation =>
            {
                bool selected = selectors.Contains(operation.OperationId);

                if (selected)
                {
                    matched.Add(operation.OperationId);
                }

                return wholeService || selected;
            })];

            if (operations.Length != 0)
            {
                services.Add(service with { Operations = operations });
            }
        }

        NonHostedProducerChainEntry[] chains = [.. nonHostedCatalog.Where(chain =>
        {
            bool selected = selectors.Contains(chain.ChainId);

            if (selected)
            {
                matched.Add(chain.ChainId);
            }

            return selected;
        })];

        string[] unmatched = [.. selectors.Where(selector => !matched.Contains(selector)).Order(StringComparer.Ordinal)];

        if (unmatched.Length != 0)
        {
            throw new ArgumentException("The root subset names no declared production root: " + string.Join(", ", unmatched), nameof(rootSubset));
        }

        HashSet<string> keptServices = new(services.Select(static service => service.ServiceType), StringComparer.Ordinal);

        HostedProducerDiscovery<string> hosted = registrations with { Items = [.. registrations.Items.Where(keptServices.Contains)] };

        return new(hosted, services, chains);
    }
}
