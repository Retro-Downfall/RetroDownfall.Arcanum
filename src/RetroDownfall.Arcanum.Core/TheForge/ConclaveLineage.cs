using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.TheForge;

public static class ConclaveLineage
{

    public static async Task<Result> ValidateCastLimitsAsync(
        IApprenticeRepository repository,
        Guid? parentApprenticeId,
        int maxDelegationDepth,
        int maxDescendantsPerRoot,
        CancellationToken cancellationToken = default)
    {

        if (parentApprenticeId is not Guid parentId)
        {

            return Result.Success();

        }

        int parentDepth = await ComputeDepthFromRootAsync(repository, parentId, cancellationToken).ConfigureAwait(false);

        if (parentDepth + 1 > maxDelegationDepth)
        {

            return Result.Failure(
                new Error(
                    "Apprentice.ConclaveDepthExceeded",
                    $"Conclave delegation depth would exceed the configured maximum ({maxDelegationDepth})."));

        }

        Guid rootId = await FindRootAsync(repository, parentId, cancellationToken).ConfigureAwait(false);

        int descendants = await CountDescendantsOfRootAsync(
            repository,
            rootId,
            maxDescendantsPerRoot,
            cancellationToken).ConfigureAwait(false);

        if (descendants >= maxDescendantsPerRoot)
        {

            return Result.Failure(
                new Error(
                    "Apprentice.ConclaveBreadthExceeded",
                    $"Conclave descendant count for this root would exceed the configured maximum ({maxDescendantsPerRoot})."));

        }

        return Result.Success();

    }

    public static async Task<Guid> FindRootAsync(
        IApprenticeRepository repository,
        Guid apprenticeId,
        CancellationToken cancellationToken = default)
    {

        HashSet<Guid> visited = [];

        Guid current = apprenticeId;

        while (true)
        {

            if (!visited.Add(current))
            {

                return current;

            }

            Apprentice? node = await repository.GetByIdAsync(current, cancellationToken).ConfigureAwait(false);

            if (node is null)
            {

                return current;

            }

            Guid? parentId = ResolveParentId(node);

            if (parentId is not Guid parent)
            {

                return current;

            }

            current = parent;

        }

    }

    public static async Task<int> ComputeDepthFromRootAsync(
        IApprenticeRepository repository,
        Guid apprenticeId,
        CancellationToken cancellationToken = default)
    {

        Guid rootId = await FindRootAsync(repository, apprenticeId, cancellationToken).ConfigureAwait(false);

        if (rootId == apprenticeId)
        {

            return 0;

        }

        int depth = 0;

        HashSet<Guid> visited = [];

        Guid current = apprenticeId;

        while (current != rootId)
        {

            if (!visited.Add(current))
            {

                break;

            }

            Apprentice? node = await repository.GetByIdAsync(current, cancellationToken).ConfigureAwait(false);

            if (node is null)
            {

                break;

            }

            Guid? parentId = ResolveParentId(node);

            if (parentId is not Guid parent)
            {

                break;

            }

            depth++;

            current = parent;

        }

        return depth;

    }

    private const int LineagePageSize = 1_000;

    public static async Task<int> CountDescendantsOfRootAsync(
        IApprenticeRepository repository,
        Guid rootApprenticeId,
        int maxDescendants = int.MaxValue,
        CancellationToken cancellationToken = default)
    {

        // W3.6: page the full apprentice set into an in-memory parent map (id -> parentId). This
        // (a) counts descendants beyond the first page — the old single ListAsync(limit: 10_000)
        // ignored HasMore, so the maxDescendantsPerRoot guard could be silently bypassed past
        // 10k apprentices — and (b) walks ancestry in memory instead of issuing one GetByIdAsync
        // per node per level, removing the O(items × depth) DB round-trips.
        Dictionary<Guid, Guid?> parentById = await LoadParentMapAsync(repository, cancellationToken).ConfigureAwait(false);

        int count = 0;

        foreach (Guid id in parentById.Keys)
        {

            if (id == rootApprenticeId)
            {

                continue;

            }

            if (IsDescendantInMap(parentById, id, rootApprenticeId))
            {

                count++;

                if (count >= maxDescendants)
                {

                    break;

                }

            }

        }

        return count;

    }

    private static async Task<Dictionary<Guid, Guid?>> LoadParentMapAsync(
        IApprenticeRepository repository,
        CancellationToken cancellationToken)
    {

        Dictionary<Guid, Guid?> parentById = [];

        DateTimeOffset? cursor = null;

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            ListPageResult<Apprentice> page = await repository
                .ListAsync(campaignId: null, status: null, limit: LineagePageSize, beforeUpdatedAt: cursor, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (Apprentice apprentice in page.Items)
            {

                parentById[apprentice.Id] = apprentice.ParentApprenticeId;

            }

            if (!page.HasMore || page.NextBeforeUpdatedAt is not { } next || next == cursor)
            {

                break;

            }

            cursor = next;

        }

        return parentById;

    }

    private static bool IsDescendantInMap(Dictionary<Guid, Guid?> parentById, Guid apprenticeId, Guid rootApprenticeId)
    {

        HashSet<Guid> visited = [];

        Guid? current = apprenticeId;

        while (current is Guid id)
        {

            if (!visited.Add(id))
            {

                return false;

            }

            if (id == rootApprenticeId)
            {

                return true;

            }

            current = parentById.TryGetValue(id, out Guid? parent) ? parent : null;

        }

        return false;

    }

    private static Guid? ResolveParentId(Apprentice apprentice) =>
        apprentice.ParentApprenticeId;

}
