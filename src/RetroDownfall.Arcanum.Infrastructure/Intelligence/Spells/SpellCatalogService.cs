using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal interface ISpellCatalogProgressObserver
{

    void OnCandidateScanned(int retainedCandidates);

}

internal sealed class SpellCatalogService(
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ISpellCatalogProgressObserver? progressObserver = null) : IArcanumSpellCatalog
{

    internal const int PageSize = 50;

    private const int CandidateWindowSize = PageSize + 1;

    public async Task<Result<SpellCatalogPage>> PageAsync(
        string? workingDirectory,
        SpellCatalogQuery query,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        cancellationToken.ThrowIfCancellationRequested();

        if (query.Source == SpellSource.Campaign)
        {

            return Result<SpellCatalogPage>.Failure(
                new Error(
                    ErrorCodes.Spell.InvalidWorkspace,
                    "The Arcanum spell catalog does not include Forge campaign sources."));

        }

        string? workspaceRoot = ResolveWorkspaceRoot(workingDirectory);

        if (!string.IsNullOrWhiteSpace(workingDirectory)
            && workspaceRoot is null)
        {

            return Result<SpellCatalogPage>.Failure(
                new Error(
                    ErrorCodes.Spell.InvalidWorkspace,
                    "The workspace spell-catalog root is invalid or unavailable."));

        }

        byte[] queryFingerprint =
            SpellCatalogContinuationCursor.CreateQueryFingerprint(
                workspaceRoot,
                query);

        SpellCatalogCursorDecodeResult cursorResult =
            SpellCatalogContinuationCursor.TryDecode(
                query.Cursor,
                queryFingerprint,
                out SpellCatalogCheckpoint? checkpoint);

        if (cursorResult != SpellCatalogCursorDecodeResult.Success)
        {

            return Result<SpellCatalogPage>.Failure(
                new Error(
                    cursorResult == SpellCatalogCursorDecodeResult.QueryMismatch
                        ? ErrorCodes.Spell.ContinuationQueryMismatch
                        : ErrorCodes.Spell.ContinuationInvalid,
                    cursorResult == SpellCatalogCursorDecodeResult.QueryMismatch
                        ? "The opaque spell-catalog cursor belongs to different search arguments. Restart with cursor omitted."
                        : "The opaque spell-catalog cursor is invalid. Restart with cursor omitted."));

        }

        SortedSet<CatalogCandidate> retained = new(
            CatalogCandidateComparer.Instance);

        Dictionary<string, CatalogCandidate> retainedByName = new(
            StringComparer.OrdinalIgnoreCase);

        CatalogCandidate? anchor = null;

        string sanitizedQuery = SpellSearchSanitizer.SanitizeQuery(
            query.Query);

        string? tag = NormalizeFilter(query.Tag);

        string? tool = NormalizeFilter(query.Tool);

        long maxFileSizeBytes =
            ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(
                settingsMonitor.CurrentValue);

        async Task ScanRootAsync(
            string root,
            SpellSource source)
        {

            await foreach (SpellMetadata metadata in SpellScanner
                               .StreamMetadataTreeAsync(
                                   root,
                                   maxFileSizeBytes,
                                   cancellationToken)
                               .ConfigureAwait(false))
            {

                cancellationToken.ThrowIfCancellationRequested();

                SpellSummary summary = CreateSummary(
                    metadata,
                    source);

                if (Matches(
                        summary,
                        sanitizedQuery,
                        tag,
                        tool))
                {

                    CatalogCandidate candidate = CreateCandidate(
                        metadata,
                        summary,
                        source);

                    if (checkpoint is not null
                        && string.Equals(
                            candidate.Summary.Name,
                            checkpoint.Name,
                            StringComparison.OrdinalIgnoreCase)
                        && (anchor is null
                            || IsPreferred(candidate, anchor)))
                    {

                        anchor = candidate;

                    }

                    if (checkpoint is null
                        || CompareNames(
                            candidate.Summary.Name,
                            checkpoint.Name) > 0)
                    {

                        RetainCandidate(
                            candidate,
                            retained,
                            retainedByName);

                    }

                }

                progressObserver?.OnCandidateScanned(retained.Count);

                cancellationToken.ThrowIfCancellationRequested();

            }

        }

        if (query.Source is null or SpellSource.Builtin)
        {

            string globalRoot = Path.GetFullPath(
                ArcanumPaths.GlobalSpellsDirectory);

            if (Directory.Exists(globalRoot))
            {

                await ScanRootAsync(globalRoot, SpellSource.Builtin)
                    .ConfigureAwait(false);

            }

        }

        if (workspaceRoot is not null
            && (query.Source is null or SpellSource.Workspace))
        {

            await ScanRootAsync(workspaceRoot, SpellSource.Workspace)
                .ConfigureAwait(false);

        }

        if (checkpoint is not null
            && (anchor is null
                || anchor.Summary.Source != checkpoint.Source
                || !CryptographicOperations.FixedTimeEquals(
                    anchor.IdentityHash,
                    checkpoint.IdentityHash)))
        {

            return Result<SpellCatalogPage>.Failure(
                new Error(
                    ErrorCodes.Spell.ContinuationCheckpointMissing,
                    "The spell catalog changed and the continuation checkpoint no longer exists. Restart with cursor omitted."));

        }

        CatalogCandidate[] ordered = retained.ToArray();

        bool hasMore = ordered.Length > PageSize;

        CatalogCandidate[] pageCandidates = hasMore
            ? ordered[..PageSize]
            : ordered;

        string? nextCursor = null;

        string? continuationAction = null;

        if (hasMore)
        {

            CatalogCandidate last = pageCandidates[^1];

            if (!SpellCatalogContinuationCursor.TryEncode(
                    queryFingerprint,
                    last.Summary.Name,
                    last.Summary.Source,
                    last.IdentityHash,
                    out nextCursor,
                    out int nameByteCount))
            {

                return Result<SpellCatalogPage>.Failure(
                    new Error(
                        ErrorCodes.Spell.ContinuationFrameTooLarge,
                        $"Physical spell-catalog continuation-frame boundary: the catalog-owned UTF-8 spell name requires {nameByteCount:N0} bytes; the limit is {SpellCatalogContinuationCursor.MaximumNameBytes:N0} bytes. Server state was not changed. Rename that spell or narrow q/tag/tool/source, then restart with cursor omitted."));

            }

            continuationAction =
                "Request the same spell catalog search again with cursor set to nextCursor.";

        }

        return Result<SpellCatalogPage>.Success(
            new SpellCatalogPage(
                pageCandidates
                    .Select(static candidate => candidate.Summary)
                    .ToArray(),
                hasMore,
                nextCursor,
                continuationAction));

    }

    private static string? ResolveWorkspaceRoot(string? workingDirectory)
    {

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return null;

        }

        try
        {

            string root = Path.GetFullPath(workingDirectory.Trim());

            return Directory.Exists(root)
                ? root
                : null;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {

            return null;

        }

    }

    private static SpellSummary CreateSummary(
        SpellMetadata metadata,
        SpellSource source)
    {

        return new SpellSummary(
            metadata.Name,
            string.IsNullOrEmpty(metadata.Description)
                ? null
                : metadata.Description,
            source,
            metadata.Tags ?? [],
            DeclaredTools: metadata.Tools is { Length: > 0 }
                ? metadata.Tools
                : null);

    }

    private static CatalogCandidate CreateCandidate(
        SpellMetadata metadata,
        SpellSummary summary,
        SpellSource source)
    {

        return new CatalogCandidate(
            summary,
            metadata.FilePath,
            SpellCatalogContinuationCursor.CreateIdentityHash(
                source,
                metadata.Name,
                metadata.FilePath));

    }

    private static bool Matches(
        SpellSummary summary,
        string sanitizedQuery,
        string? tag,
        string? tool)
    {

        if (sanitizedQuery.Length > 0
            && !summary.Name.Contains(
                sanitizedQuery,
                StringComparison.OrdinalIgnoreCase)
            && !(summary.Description?.Contains(
                sanitizedQuery,
                StringComparison.OrdinalIgnoreCase) ?? false)
            && !summary.Tags.Any(
                tag => tag.Contains(
                    sanitizedQuery,
                    StringComparison.OrdinalIgnoreCase)))
        {

            return false;

        }

        if (tag is not null
            && !summary.Tags.Any(
                candidate => string.Equals(
                    candidate,
                    tag,
                    StringComparison.OrdinalIgnoreCase)))
        {

            return false;

        }

        if (tool is not null
            && !(summary.DeclaredTools?.Any(
                candidate => string.Equals(
                    candidate,
                    tool,
                    StringComparison.OrdinalIgnoreCase)) ?? false))
        {

            return false;

        }

        return true;

    }

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static void RetainCandidate(
        CatalogCandidate candidate,
        SortedSet<CatalogCandidate> retained,
        Dictionary<string, CatalogCandidate> retainedByName)
    {

        if (retainedByName.TryGetValue(
                candidate.Summary.Name,
                out CatalogCandidate? existing))
        {

            if (!IsPreferred(candidate, existing))
            {

                return;

            }

            _ = retained.Remove(existing);

            _ = retainedByName.Remove(existing.Summary.Name);

        }

        if (retained.Count >= CandidateWindowSize
            && CatalogCandidateComparer.Instance.Compare(
                candidate,
                retained.Max!) >= 0)
        {

            return;

        }

        _ = retained.Add(candidate);

        retainedByName[candidate.Summary.Name] = candidate;

        if (retained.Count <= CandidateWindowSize)
        {

            return;

        }

        CatalogCandidate removed = retained.Max!;

        _ = retained.Remove(removed);

        _ = retainedByName.Remove(removed.Summary.Name);

    }

    private static bool IsPreferred(
        CatalogCandidate candidate,
        CatalogCandidate existing)
    {

        if (candidate.Summary.Source != existing.Summary.Source)
        {

            return candidate.Summary.Source == SpellSource.Workspace;

        }

        return string.Compare(
            candidate.Path,
            existing.Path,
            StringComparison.Ordinal) < 0;

    }

    private static int CompareNames(string left, string right)
    {

        int insensitive = StringComparer.OrdinalIgnoreCase.Compare(
            left,
            right);

        return insensitive != 0
            ? insensitive
            : StringComparer.Ordinal.Compare(left, right);

    }

    private sealed record CatalogCandidate(
        SpellSummary Summary,
        string Path,
        byte[] IdentityHash);

    private sealed class CatalogCandidateComparer : IComparer<CatalogCandidate>
    {

        internal static CatalogCandidateComparer Instance { get; } = new();

        public int Compare(
            CatalogCandidate? left,
            CatalogCandidate? right)
        {

            if (ReferenceEquals(left, right))
            {

                return 0;

            }

            if (left is null)
            {

                return -1;

            }

            if (right is null)
            {

                return 1;

            }

            int name = CompareNames(
                left.Summary.Name,
                right.Summary.Name);

            if (name != 0)
            {

                return name;

            }

            int source = left.Summary.Source.CompareTo(
                right.Summary.Source);

            return source != 0
                ? source
                : StringComparer.Ordinal.Compare(left.Path, right.Path);

        }

    }

}
