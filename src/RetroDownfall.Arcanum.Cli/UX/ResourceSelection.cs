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
    bool Searchable = true)
    where T : class;

public interface IResourcePicker
{
    Task<T?> PickAsync<T>(
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
    private const int MaxPages = 100;

    private const int MaxDiagnosticCandidates = 8;

    private const int MaxDiagnosticChars = 180;

    public async Task<ResourceSelectionResult<T>> SelectAsync(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<T>> fetched = await FetchAllAsync(request, cancellationToken).ConfigureAwait(false);
        if (fetched.IsFailure)
        {
            return ResourceSelectionResult<T>.Failure(fetched.Error.Message);
        }

        IReadOnlyList<T> candidates = fetched.Value;
        ResourceDescriptor<T> descriptor = request.Descriptor;
        string? identifier = string.IsNullOrWhiteSpace(request.Identifier)
            ? null
            : request.Identifier.Trim();

        if (identifier is not null)
        {
            T[] exactIds = candidates
                .Where(value => string.Equals(descriptor.GetId(value), identifier, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (exactIds.Length == 1)
            {
                return RememberAndSelect(request.ResourceKind, exactIds[0], descriptor);
            }

            T[] exactNames = candidates
                .Where(value => string.Equals(descriptor.GetName(value), identifier, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (exactNames.Length == 1)
            {
                return RememberAndSelect(request.ResourceKind, exactNames[0], descriptor);
            }

            if (exactNames.Length > 1)
            {
                return await ResolveAmbiguousAsync(
                    request,
                    identifier,
                    exactNames,
                    cancellationToken).ConfigureAwait(false);
            }

            T[] prefixes = candidates
                .Where(value => descriptor.GetName(value).StartsWith(identifier, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (prefixes.Length == 1)
            {
                return RememberAndSelect(request.ResourceKind, prefixes[0], descriptor);
            }

            if (prefixes.Length > 1)
            {
                return await ResolveAmbiguousAsync(
                    request,
                    identifier,
                    prefixes,
                    cancellationToken).ConfigureAwait(false);
            }

            return ResourceSelectionResult<T>.Failure(
                $"No {descriptor.SingularName} matches '{identifier}'. " + CandidateText(candidates, descriptor));
        }

        if (!request.IsInteractive)
        {
            return ResourceSelectionResult<T>.Failure(
                $"A {descriptor.SingularName} identifier or name is required when input or output is redirected. "
                + CandidateText(candidates, descriptor));
        }

        if (candidates.Count == 0)
        {
            return ResourceSelectionResult<T>.Failure($"No {descriptor.SingularName} resources are available.");
        }

        IReadOnlyList<T> ordered = OrderByRecent(request.ResourceKind, candidates, descriptor);
        T? selected = await picker
            .PickAsync(new ResourcePickerRequest<T>(ordered, descriptor), cancellationToken)
            .ConfigureAwait(false);
        if (selected is null)
        {
            return ResourceSelectionResult<T>.Cancelled();
        }

        return RememberAndSelect(request.ResourceKind, selected, descriptor);
    }

    private static async Task<Result<IReadOnlyList<T>>> FetchAllAsync(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken)
    {
        List<T> all = [];
        HashSet<string> tokens = new(StringComparer.Ordinal);
        string? token = null;

        for (int pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            Result<ResourcePage<T>> result = await request.FetchPageAsync(token, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result<IReadOnlyList<T>>.Failure(result.Error);
            }

            ResourcePage<T> page = result.Value;
            all.AddRange(page.Items);
            if (string.IsNullOrEmpty(page.NextToken))
            {
                return Result<IReadOnlyList<T>>.Success(all);
            }

            if (!tokens.Add(page.NextToken))
            {
                return Result<IReadOnlyList<T>>.Failure(
                    new Error("Cli.ResourcePagingLoop", "The resource list returned a repeated page token."));
            }

            token = page.NextToken;
        }

        return Result<IReadOnlyList<T>>.Failure(
            new Error("Cli.ResourcePageLimit", $"The {request.Descriptor.SingularName} list exceeded the safe page limit."));
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
        IReadOnlyList<T> matches) =>
        ResourceSelectionResult<T>.Failure(
            $"The {request.Descriptor.SingularName} identifier '{identifier}' is ambiguous; provide an exact ID or a longer name. "
            + CandidateText(matches, request.Descriptor));

    private async Task<ResourceSelectionResult<T>> ResolveAmbiguousAsync(
        ResourceSelectionRequest<T> request,
        string identifier,
        IReadOnlyList<T> matches,
        CancellationToken cancellationToken)
    {
        if (!request.IsInteractive || !request.PickAmbiguousIdentifiers)
        {
            return Ambiguous(request, identifier, matches);
        }

        IReadOnlyList<T> ordered = OrderByRecent(
            request.ResourceKind,
            matches,
            request.Descriptor);

        T? selected = await picker
            .PickAsync(
                new ResourcePickerRequest<T>(ordered, request.Descriptor),
                cancellationToken)
            .ConfigureAwait(false);

        return selected is null
            ? ResourceSelectionResult<T>.Cancelled()
            : RememberAndSelect(
                request.ResourceKind,
                selected,
                request.Descriptor);
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

    private static string CandidateText(IReadOnlyList<T> candidates, ResourceDescriptor<T> descriptor)
    {
        if (candidates.Count == 0)
        {
            return "No candidates are available.";
        }

        StringBuilder text = new("Candidates: ");
        for (int index = 0; index < Math.Min(candidates.Count, MaxDiagnosticCandidates); index++)
        {
            if (index > 0)
            {
                text.Append("; ");
            }

            T value = candidates[index];
            text.Append(descriptor.GetName(value));
            text.Append(" [");
            text.Append(descriptor.GetId(value));
            text.Append("] ");
            text.Append(TruncateSingleLine(descriptor.GetSummary(value)));
        }

        if (candidates.Count > MaxDiagnosticCandidates)
        {
            text.Append($"; and {candidates.Count - MaxDiagnosticCandidates} more");
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
}
