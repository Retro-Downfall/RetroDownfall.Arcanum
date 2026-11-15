namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private static void ValidateManifestSiteFrontiers(HostedProducerSite site,
        IReadOnlyList<HostedProducerSite> sites, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        ValidateManifestFrontier(site, site.WorkFrontierCapsuleId, IsWorkFrontierCallee, "work", sites, diagnostics);

        ValidateManifestFrontier(site, site.RegisteredRootWorkFrontierCapsuleId, IsWorkFrontierCallee, "registered-root-work", sites, diagnostics);

        ValidateManifestFrontier(site, site.EffectFrontierCapsuleId,
            static callee => callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal), "effect", sites, diagnostics);
    }

    private static bool SameManifestFrontierIntrinsics(HostedProducerSite first, HostedProducerSite candidate, bool work) =>
        (candidate with
        {
            OperationId = first.OperationId,
            EffectFrontierCapsuleId = first.EffectFrontierCapsuleId,
            RegisteredRootWorkFrontierCapsuleId = first.RegisteredRootWorkFrontierCapsuleId,
            // Work's own capsule is intrinsic; a group's parent physical Work is contextual.
            WorkFrontierCapsuleId = work ? candidate.WorkFrontierCapsuleId : first.WorkFrontierCapsuleId,
        }) == first;

    private static bool SameManifestWorkContextExceptIncomingEffect(HostedProducerSite first, HostedProducerSite candidate) =>
        (candidate with { OperationId = first.OperationId, EffectFrontierCapsuleId = first.EffectFrontierCapsuleId }) == first;

    private static bool SameManifestContextRow(HostedProducerSite first, HostedProducerSite candidate) =>
        (candidate with { OperationId = first.OperationId }) == first;

    private static bool ManifestWorkOnlyDropsUnrelatedEffects(HostedProducerSite site,
        HostedProducerSite[] acquisitions, IReadOnlyList<HostedProducerSite> sites)
    {
        if (site.RegisteredRootWorkFrontierCapsuleId is not { } root
            || site.WorkFrontierCapsuleId is not { } work || work == root
            || acquisitions.Any(static acquisition => acquisition.EffectFrontierCapsuleId is null)
            || acquisitions.Select(static acquisition => acquisition.EffectFrontierCapsuleId).Distinct().Count() != acquisitions.Length)
            return false;

        foreach (HostedProducerSite acquisition in acquisitions)
        {
            HostedProducerSite[] groups = sites.Where(candidate => candidate.RootType == site.RootType
                && candidate.AuthorityOperationId == site.AuthorityOperationId
                && candidate.CapsuleId == acquisition.EffectFrontierCapsuleId
                && candidate.Kind == HostedProducerSiteKind.EffectFrontier
                && candidate.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal)).ToArray();

            if (groups is not [HostedProducerSite group]
                || group.EffectFrontierCapsuleId != group.CapsuleId
                || group.RegisteredRootWorkFrontierCapsuleId != root
                || group.AdmittedWorkKind is not null
                || group.WorkFrontierCapsuleId is not { } parent || parent == work)
                return false;

            HostedProducerSite[] physicalParents = sites.Where(candidate => candidate.RootType == site.RootType
                && candidate.AuthorityOperationId == site.AuthorityOperationId
                && candidate.CapsuleId == parent && candidate.Kind == HostedProducerSiteKind.EffectFrontier
                && IsWorkFrontierCallee(candidate.Callee)).ToArray();

            if (physicalParents.Length == 0
                || !physicalParents.All(candidate => SameManifestFrontierIntrinsics(physicalParents[0], candidate, work: true)))
                return false;

            HostedProducerSite[] parents = physicalParents.Where(candidate =>
                candidate.RegisteredRootWorkFrontierCapsuleId == root).ToArray();

            if (parents is not [HostedProducerSite parentWork]
                || parentWork.WorkFrontierCapsuleId != parent || parentWork.AdmittedWorkKind is null)
                return false;
        }

        return true;
    }

    private static void ValidateManifestFrontierContext(HostedProducerSite site, string? frontierId,
        Func<string, bool> isFrontierCallee, string frontierKind,
        IReadOnlyList<HostedProducerSite> sites, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        if (frontierId is null) return;

        HostedProducerSite[] physical = sites.Where(candidate => candidate.RootType == site.RootType
            && candidate.AuthorityOperationId == site.AuthorityOperationId
            && candidate.CapsuleId == frontierId && candidate.Kind == HostedProducerSiteKind.EffectFrontier
            && isFrontierCallee(candidate.Callee)).ToArray();

        bool workTarget = frontierKind is "work" or "registered-root-work";

        // Check ALL aliases before selecting R/P. A conflicting kind/own Work or
        // source/member/type cannot disappear into another contextual bucket.
        bool coherent = physical.Length == 0
            || physical.All(candidate => SameManifestFrontierIntrinsics(physical[0], candidate, workTarget));

        bool incomingEffect = frontierKind == "effect" && site.Kind == HostedProducerSiteKind.EffectFrontier
            && IsWorkFrontierCallee(site.Callee);

        IEnumerable<HostedProducerSite> selected = physical;

        if (workTarget)
        {
            selected = selected.Where(candidate => candidate.WorkFrontierCapsuleId == frontierId);

            if (frontierKind == "registered-root-work")
            {
                selected = selected.Where(candidate => candidate.RegisteredRootWorkFrontierCapsuleId == frontierId);
            }
            else if (site.RegisteredRootWorkFrontierCapsuleId is { } physicalRoot)
            {
                selected = selected.Where(candidate => candidate.RegisteredRootWorkFrontierCapsuleId == physicalRoot);
            }
        }
        else if (!incomingEffect && site.RegisteredRootWorkFrontierCapsuleId is { } effectRoot)
        {
            selected = selected.Where(candidate => candidate.RegisteredRootWorkFrontierCapsuleId == effectRoot);

            if (site.WorkFrontierCapsuleId is { } physicalWork)
                selected = selected.Where(candidate => candidate.WorkFrontierCapsuleId == physicalWork);
        }

        HostedProducerSite[] matches = selected.ToArray();

        // No R is inferred from a historical target. Such references stay strictly
        // unique before considering the caller's physical pair; no sibling is selected.
        bool validPhysicalPair = frontierKind != "effect" || incomingEffect
            || site.WorkFrontierCapsuleId is null
            || site.RegisteredRootWorkFrontierCapsuleId is not null
            || matches.Length == 1 && matches[0].WorkFrontierCapsuleId == site.WorkFrontierCapsuleId;

        bool sameRootContext = matches.All(candidate =>
            candidate.RegisteredRootWorkFrontierCapsuleId == site.RegisteredRootWorkFrontierCapsuleId);

        // The old incoming-Effect-only exceptions survive only inside the selected
        // R (including explicit all-null fixture context), never across root witnesses.
        bool workSelf = matches.Length > 1 && workTarget && sameRootContext
            && site.Kind == HostedProducerSiteKind.EffectFrontier && IsWorkFrontierCallee(site.Callee)
            && site.CapsuleId == frontierId && FrontierIdentity(site, frontierKind) == frontierId
            && site.AdmittedWorkKind.HasValue
            && matches.Count(candidate => SameManifestContextRow(site, candidate)) == 1
            && matches.All(candidate => SameManifestWorkContextExceptIncomingEffect(site, candidate));

        bool workOnly = matches.Length > 1 && workTarget && sameRootContext
            && (site.Kind is HostedProducerSiteKind.DatabaseAccess or HostedProducerSiteKind.FileSystemRead or HostedProducerSiteKind.OrdinaryConnectionRoute)
            && site.EffectFrontierCapsuleId is null && matches[0].AdmittedWorkKind.HasValue
            && matches.All(candidate => SameManifestWorkContextExceptIncomingEffect(matches[0], candidate))
            && (matches.Count(candidate => candidate.EffectFrontierCapsuleId is null) == 1
                // A fresh physical Work cannot borrow either historical parent's Effect.
                // Keep both incoming rows; only the already null-Effect read may converge.
                || ManifestWorkOnlyDropsUnrelatedEffects(site, matches, sites));

        if (!coherent || !validPhysicalPair || matches.Length != 1 && !workSelf && !workOnly)
        {
            diagnostics.Add(new("HOSTED_MANIFEST_FRONTIER_UNRESOLVED", StableSiteIdentity(site),
                $"The exact same-operation {frontierKind} frontier capsule must resolve once in its physical/root context; found {matches.Length}."));
        }
    }
}
