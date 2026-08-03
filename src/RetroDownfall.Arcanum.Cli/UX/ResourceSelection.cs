using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Resolves one CLI resource without moving fuzzy authority into the server API.
/// </summary>
public interface IResourceSelector<T>
    where T : class
{
    Task<ResourceSelectionResult<T>> SelectAsync(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken = default);
}

public enum ResourceSelectionStatus
{
    Selected,
    Cancelled,
    Error,
}

public sealed record ResourceSelectionResult<T>(
    ResourceSelectionStatus Status,
    T? Value = default,
    string? Error = null)
    where T : class
{
    public static ResourceSelectionResult<T> Selected(T value) =>
        new(ResourceSelectionStatus.Selected, value);

    public static ResourceSelectionResult<T> Cancelled() =>
        new(ResourceSelectionStatus.Cancelled);

    public static ResourceSelectionResult<T> Failure(string error) =>
        new(ResourceSelectionStatus.Error, Error: error);
}

public sealed record ResourceDescriptor<T>(
    string SingularName,
    IReadOnlyList<string> ColumnNames,
    Func<T, string> GetId,
    Func<T, string> GetName,
    Func<T, string> GetSummary,
    Func<T, string[]> GetCells)
    where T : class;

public sealed record ResourcePage<T>(IReadOnlyList<T> Items, string? NextToken)
    where T : class;

public sealed record ResourceSelectionRequest<T>(
    string ResourceKind,
    string? Identifier,
    bool IsInteractive,
    ResourceDescriptor<T> Descriptor,
    Func<string?, CancellationToken, Task<Result<ResourcePage<T>>>> FetchPageAsync,
    bool PickAmbiguousIdentifiers = false)
    where T : class;

public sealed record ResourcePickerRequest<T>(
    IReadOnlyList<T> Choices,
    ResourceDescriptor<T> Descriptor,
    bool Searchable = true,
    bool HasNextPage = false)
    where T : class;

public enum ResourcePickerStatus
{
    Selected,
    NextPage,
    Cancelled,
}

public sealed record ResourcePickerResult<T>(ResourcePickerStatus Status, T? Value = default)
    where T : class
{
    public static ResourcePickerResult<T> Selected(T value) =>
        new(ResourcePickerStatus.Selected, value);

    public static ResourcePickerResult<T> NextPage() =>
        new(ResourcePickerStatus.NextPage);

    public static ResourcePickerResult<T> Cancelled() =>
        new(ResourcePickerStatus.Cancelled);
}

public interface IResourcePicker
{
    Task<ResourcePickerResult<T>> PickAsync<T>(
        ResourcePickerRequest<T> request,
        CancellationToken cancellationToken)
        where T : class;
}

public interface IRecentResourceStore
{
    IReadOnlyList<string> GetRecentIds(string resourceKind);

    void Remember(string resourceKind, string id);
}

/// <summary>
/// Client-side resolution precedence is exact ID, exact case-insensitive name, unique name
/// prefix, then an interactive searchable picker only when the identifier was omitted.
/// </summary>
public sealed class ResourceSelector<T>(IResourcePicker picker, IRecentResourceStore recentStore)
    : IResourceSelector<T>
    where T : class
{
    private const int MaxDiagnosticCandidates = 8;

    private const int MaxDiagnosticChars = 180;

    public async Task<ResourceSelectionResult<T>> SelectAsync(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        string? identifier = string.IsNullOrWhiteSpace(request.Identifier)
            ? null
            : request.Identifier.Trim();

        return identifier is null
            ? await SelectWithoutIdentifierAsync(request, cancellationToken).ConfigureAwait(false)
            : await SelectByIdentifierAsync(request, identifier, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResourceSelectionResult<T>> SelectWithoutIdentifierAsync(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken)
    {
        if (!request.IsInteractive)
        {
            Result<ResourceScan> scanned = await ScanAsync(request, identifier: null, cancellationToken)
                .ConfigureAwait(false);

            if (scanned.IsFailure)
            {
                return ResourceSelectionResult<T>.Failure(scanned.Error.Message);
            }

            return ResourceSelectionResult<T>.Failure(
                $"A {request.Descriptor.SingularName} identifier or name is required when input or output is redirected. "
                + CandidateText(scanned.Value.All, request.Descriptor));
        }

        HashSet<string> tokens = new(StringComparer.Ordinal);

        string? token = null;

        bool foundAny = false;

        while (true)
        {
            Result<ResourcePage<T>> fetched = await request.FetchPageAsync(token, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.IsFailure)
            {
                return ResourceSelectionResult<T>.Failure(fetched.Error.Message);
            }

            ResourcePage<T> page = fetched.Value;

            if (page.Items.Count > 0)
            {
                foundAny = true;

                IReadOnlyList<T> ordered = OrderByRecent(
                    request.ResourceKind,
                    page.Items,
                    request.Descriptor);

                ResourcePickerResult<T> picked = await picker
                    .PickAsync(
                        new ResourcePickerRequest<T>(
                            ordered,
                            request.Descriptor,
                            HasNextPage: !string.IsNullOrEmpty(page.NextToken)),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (picked.Status == ResourcePickerStatus.Selected && picked.Value is not null)
                {
                    return RememberAndSelect(request.ResourceKind, picked.Value, request.Descriptor);
                }

                if (picked.Status != ResourcePickerStatus.NextPage)
                {
                    return ResourceSelectionResult<T>.Cancelled();
                }
            }

            Result<string?> next = NextToken(page.NextToken, tokens);

            if (next.IsFailure)
            {
                return ResourceSelectionResult<T>.Failure(next.Error.Message);
            }

            token = next.Value;

            if (token is null)
            {
                return foundAny
                    ? ResourceSelectionResult<T>.Cancelled()
                    : ResourceSelectionResult<T>.Failure(
                        $"No {request.Descriptor.SingularName} resources are available.");
            }
        }
    }

    private async Task<ResourceSelectionResult<T>> SelectByIdentifierAsync(
        ResourceSelectionRequest<T> request,
        string identifier,
        CancellationToken cancellationToken)
    {
        Result<ResourceScan> scanned = await ScanAsync(request, identifier, cancellationToken)
            .ConfigureAwait(false);

        if (scanned.IsFailure)
        {
            return ResourceSelectionResult<T>.Failure(scanned.Error.Message);
        }

        ResourceDescriptor<T> descriptor = request.Descriptor;

        ResourceScan matches = scanned.Value;

        if (matches.ExactIds.Count == 1)
        {
            return RememberAndSelect(request.ResourceKind, matches.ExactIds.Samples[0], descriptor);
        }

        if (matches.ExactIds.Count > 1)
        {
            return await ResolveAmbiguousAsync(
                request,
                identifier,
                matches.ExactIds,
                value => string.Equals(descriptor.GetId(value), identifier, StringComparison.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
        }

        if (matches.ExactNames.Count == 1)
        {
            return RememberAndSelect(request.ResourceKind, matches.ExactNames.Samples[0], descriptor);
        }

        if (matches.ExactNames.Count > 1)
        {
            return await ResolveAmbiguousAsync(
                request,
                identifier,
                matches.ExactNames,
                value => string.Equals(descriptor.GetName(value), identifier, StringComparison.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
        }

        if (matches.Prefixes.Count == 1)
        {
            return RememberAndSelect(request.ResourceKind, matches.Prefixes.Samples[0], descriptor);
        }

        if (matches.Prefixes.Count > 1)
        {
            return await ResolveAmbiguousAsync(
                request,
                identifier,
                matches.Prefixes,
                value => descriptor.GetName(value).StartsWith(identifier, StringComparison.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
        }

        return ResourceSelectionResult<T>.Failure(
            $"No {descriptor.SingularName} matches '{identifier}'. "
            + CandidateText(matches.All, descriptor));
    }

    private static async Task<Result<ResourceScan>> ScanAsync(
        ResourceSelectionRequest<T> request,
        string? identifier,
        CancellationToken cancellationToken)
    {
        ResourceScan scan = new();

        HashSet<string> tokens = new(StringComparer.Ordinal);

        string? token = null;

        while (true)
        {
            Result<ResourcePage<T>> fetched = await request.FetchPageAsync(token, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.IsFailure)
            {
                return Result<ResourceScan>.Failure(fetched.Error);
            }

            ResourcePage<T> page = fetched.Value;

            foreach (T value in page.Items)
            {
                scan.All.Add(value);

                if (identifier is null)
                {
                    continue;
                }

                if (string.Equals(
                    request.Descriptor.GetId(value),
                    identifier,
                    StringComparison.OrdinalIgnoreCase))
                {
                    scan.ExactIds.Add(value);
                }

                string name = request.Descriptor.GetName(value);

                if (string.Equals(name, identifier, StringComparison.OrdinalIgnoreCase))
                {
                    scan.ExactNames.Add(value);
                }

                if (name.StartsWith(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    scan.Prefixes.Add(value);
                }
            }

            Result<string?> next = NextToken(page.NextToken, tokens);

            if (next.IsFailure)
            {
                return Result<ResourceScan>.Failure(next.Error);
            }

            token = next.Value;

            if (token is null)
            {
                return Result<ResourceScan>.Success(scan);
            }
        }
    }

    private static Result<string?> NextToken(string? nextToken, HashSet<string> tokens)
    {
        if (string.IsNullOrEmpty(nextToken))
        {
            return Result<string?>.Success(null);
        }

        if (!tokens.Add(nextToken))
        {
            return Result<string?>.Failure(
                new Error(
                    "Cli.ResourcePagingLoop",
                    "The resource list returned a repeated page token. Retry the command after the resource provider advances its continuation."));
        }

        return Result<string?>.Success(nextToken);
    }

    private ResourceSelectionResult<T> RememberAndSelect(
        string resourceKind,
        T value,
        ResourceDescriptor<T> descriptor)
    {
        recentStore.Remember(resourceKind, descriptor.GetId(value));
        return ResourceSelectionResult<T>.Selected(value);
    }

    private static ResourceSelectionResult<T> Ambiguous(
        ResourceSelectionRequest<T> request,
        string identifier,
        CandidateSample matches) =>
        ResourceSelectionResult<T>.Failure(
            $"The {request.Descriptor.SingularName} identifier '{identifier}' is ambiguous; provide an exact ID or a longer name. "
            + CandidateText(matches, request.Descriptor));

    private async Task<ResourceSelectionResult<T>> ResolveAmbiguousAsync(
        ResourceSelectionRequest<T> request,
        string identifier,
        CandidateSample matches,
        Func<T, bool> isMatch,
        CancellationToken cancellationToken)
    {
        if (!request.IsInteractive || !request.PickAmbiguousIdentifiers)
        {
            return Ambiguous(request, identifier, matches);
        }

        HashSet<string> tokens = new(StringComparer.Ordinal);

        string? token = null;

        while (true)
        {
            Result<ResourcePage<T>> fetched = await request.FetchPageAsync(token, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.IsFailure)
            {
                return ResourceSelectionResult<T>.Failure(fetched.Error.Message);
            }

            ResourcePage<T> page = fetched.Value;

            T[] pageMatches = page.Items.Where(isMatch).ToArray();

            if (pageMatches.Length > 0)
            {
                IReadOnlyList<T> ordered = OrderByRecent(
                    request.ResourceKind,
                    pageMatches,
                    request.Descriptor);

                ResourcePickerResult<T> picked = await picker
                    .PickAsync(
                        new ResourcePickerRequest<T>(
                            ordered,
                            request.Descriptor,
                            HasNextPage: !string.IsNullOrEmpty(page.NextToken)),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (picked.Status == ResourcePickerStatus.Selected && picked.Value is not null)
                {
                    return RememberAndSelect(request.ResourceKind, picked.Value, request.Descriptor);
                }

                if (picked.Status != ResourcePickerStatus.NextPage)
                {
                    return ResourceSelectionResult<T>.Cancelled();
                }
            }

            Result<string?> next = NextToken(page.NextToken, tokens);

            if (next.IsFailure)
            {
                return ResourceSelectionResult<T>.Failure(next.Error.Message);
            }

            token = next.Value;

            if (token is null)
            {
                return Ambiguous(request, identifier, matches);
            }
        }
    }

    private IReadOnlyList<T> OrderByRecent(
        string resourceKind,
        IReadOnlyList<T> candidates,
        ResourceDescriptor<T> descriptor)
    {
        IReadOnlyList<string> recentIds = recentStore.GetRecentIds(resourceKind);
        Dictionary<string, int> ranks = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < recentIds.Count; index++)
        {
            ranks.TryAdd(recentIds[index], index);
        }

        return candidates
            .Select((value, originalIndex) => new
            {
                Value = value,
                OriginalIndex = originalIndex,
                Rank = ranks.TryGetValue(descriptor.GetId(value), out int rank) ? rank : int.MaxValue,
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Value)
            .ToArray();
    }

    private static string CandidateText(CandidateSample candidates, ResourceDescriptor<T> descriptor)
    {
        if (candidates.Count == 0)
        {
            return "No candidates are available.";
        }

        StringBuilder text = new("Candidates: ");
        for (int index = 0; index < candidates.Samples.Count; index++)
        {
            if (index > 0)
            {
                text.Append("; ");
            }

            T value = candidates.Samples[index];
            text.Append(descriptor.GetName(value));
            text.Append(" [");
            text.Append(descriptor.GetId(value));
            text.Append("] ");
            text.Append(TruncateSingleLine(descriptor.GetSummary(value)));
        }

        if (candidates.Count > candidates.Samples.Count)
        {
            text.Append($"; and {candidates.Count - candidates.Samples.Count} more");
        }

        return text.ToString();
    }

    private static string TruncateSingleLine(string value)
    {
        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= MaxDiagnosticChars
            ? singleLine
            : singleLine[..MaxDiagnosticChars] + "\u2026";
    }

    private sealed class ResourceScan
    {
        public CandidateSample All { get; } = new();

        public CandidateSample ExactIds { get; } = new();

        public CandidateSample ExactNames { get; } = new();

        public CandidateSample Prefixes { get; } = new();
    }

    private sealed class CandidateSample
    {
        public long Count { get; private set; }

        public List<T> Samples { get; } = [];

        public void Add(T value)
        {
            Count++;

            if (Samples.Count < MaxDiagnosticCandidates)
            {
                Samples.Add(value);
            }
        }
    }
}
