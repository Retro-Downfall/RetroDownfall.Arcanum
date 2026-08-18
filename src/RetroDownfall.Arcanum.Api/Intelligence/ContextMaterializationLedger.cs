namespace RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Describes every context source that may materialize during one logical inference turn.
/// The ordinal values are the deterministic admission priority: lower values outrank higher ones.
/// </summary>
public enum ContextMaterializationSourceKind
{

    CurrentTurnAttachment = 0,

    ExplicitAttachmentReference = 1,

    ExplicitContextPin = 2,

    AttachSessionFile = 3,

    RefreshSessionFile = 4,

    AttachmentRag = 5,

    WorkspaceRag = 6,

    SagaMemory = 7,

    /// <summary>
    /// The Tapestry's hierarchical nodes (DESIGN §21.11). Ranked last deliberately: the documented
    /// source precedence is accepted explicit material &gt; exact raw leaf &gt; derived summary, so a
    /// model-authored abstraction is the first semantic source evicted under context pressure and the
    /// last admitted when it overlaps something exact.
    /// </summary>
    TapestryMemory = 8,

}

public enum ContextMaterializationOrigin
{

    Explicit,

    ModelRequested,

    Semantic,

}

public enum ContextMaterializationTrust
{

    UntrustedData,

    TrustedSystem,

}

public enum ContextMaterializationRejection
{

    None,

    MaterializationFailed,

    SessionMismatch,

    DuplicateIdentity,

    DuplicateContentRange,

    ExplicitSourceAlreadyMaterialized,

    StaleVersion,

    RetrievedChunkLimit,

    RetrievedAttachmentLimit,

    RetrievedByteLimit,

    RetrievedTokenLimit,

}

public readonly record struct ContextMaterializationRange(int Start, int End)
{

    public static ContextMaterializationRange Whole { get; } = new(-1, -1);

    public bool IsWhole => Start < 0 && End < 0;

}

public readonly record struct ContextMaterializationIdentity(
    ContextMaterializationSourceKind SourceKind,
    string SourceId,
    string VersionOrContentHash,
    ContextMaterializationRange Range);

public sealed record ContextMaterializationLimits(
    int MaxRetrievedChunks,
    int MaxRetrievedAttachments,
    int MaxRetrievedBytes,
    int MaxRetrievedTokens);

public sealed record ContextMaterializationCandidate(
    Guid? SessionId,
    ContextMaterializationSourceKind SourceKind,
    string SourceId,
    string VersionOrContentHash,
    int? VersionOrdinal,
    ContextMaterializationRange Range,
    ContextMaterializationOrigin Origin,
    string SourceLabel,
    string ContentHash,
    int EstimatedTokens,
    int MaterializedBytes,
    ContextMaterializationTrust Trust)
{

    public AttachmentMemoryProvenance? AttachmentProvenance { get; init; }

    public ContextMaterializationIdentity Identity =>
        new(SourceKind, SourceId, VersionOrContentHash, Range);

}

public sealed record ContextMaterializationEntry(
    ContextMaterializationIdentity Identity,
    Guid? SessionId,
    ContextMaterializationSourceKind SourceKind,
    ContextMaterializationOrigin Origin,
    string SourceLabel,
    string ContentHash,
    int EstimatedTokens,
    int MaterializedBytes,
    ContextMaterializationTrust Trust,
    bool Accepted,
    ContextMaterializationRejection Rejection,
    bool Injected = false,
    int? ProviderRound = null,
    int? VersionOrdinal = null)
{

    public AttachmentMemoryProvenance? AttachmentProvenance { get; init; }

    internal static ContextMaterializationEntry FromCandidate(
        ContextMaterializationCandidate candidate,
        bool accepted,
        ContextMaterializationRejection rejection) =>
        new(
            candidate.Identity,
            candidate.SessionId,
            candidate.SourceKind,
            candidate.Origin,
            candidate.SourceLabel,
            candidate.ContentHash,
            Math.Max(0, candidate.EstimatedTokens),
            Math.Max(0, candidate.MaterializedBytes),
            candidate.Trust,
            accepted,
            rejection,
            VersionOrdinal: candidate.VersionOrdinal)
        {
            AttachmentProvenance = candidate.AttachmentProvenance,
        };

}

/// <summary>
/// One non-persisted ledger for a logical turn. It owns cross-path deduplication, semantic
/// retrieval bounds, version replacement, injection rounds, and semantic-first eviction.
/// </summary>
public sealed class ContextMaterializationLedger
{

    private readonly Guid? _sessionId;

    private readonly ContextMaterializationLimits _limits;

    private readonly List<ContextMaterializationEntry> _entries = [];

    private readonly HashSet<string> _explicitWorkspaceSources = new(StringComparer.Ordinal);

    private int _droppedAttachmentRagChunks;

    private int _droppedAttachmentRagTokens;

    private int _droppedWorkspaceRagChunks;

    private int _droppedWorkspaceRagTokens;

    private int _droppedTapestryNodes;

    private int _droppedTapestryTokens;

    public ContextMaterializationLedger(
        Guid? sessionId,
        ContextMaterializationLimits limits)
    {

        ArgumentNullException.ThrowIfNull(limits);

        _sessionId = sessionId;

        _limits = new ContextMaterializationLimits(
            Math.Max(0, limits.MaxRetrievedChunks),
            Math.Max(0, limits.MaxRetrievedAttachments),
            Math.Max(0, limits.MaxRetrievedBytes),
            Math.Max(0, limits.MaxRetrievedTokens));

    }

    public IReadOnlyList<ContextMaterializationEntry> Entries => _entries;

    public Guid? SessionId => _sessionId;

    public int DroppedAttachmentRagChunks => _droppedAttachmentRagChunks;

    public int DroppedAttachmentRagTokens => _droppedAttachmentRagTokens;

    public int DroppedWorkspaceRagChunks => _droppedWorkspaceRagChunks;

    public int DroppedWorkspaceRagTokens => _droppedWorkspaceRagTokens;

    public int DroppedTapestryNodes => _droppedTapestryNodes;

    public int DroppedTapestryTokens => _droppedTapestryTokens;

    public ContextMaterializationEntry Accept(
        ContextMaterializationCandidate candidate,
        bool materialized)
    {

        ArgumentNullException.ThrowIfNull(candidate);

        if (!materialized)
        {

            return Reject(candidate, ContextMaterializationRejection.MaterializationFailed);

        }

        if (_sessionId is { } expectedSession
            && candidate.SessionId is { } candidateSession
            && candidateSession != expectedSession)
        {

            return Reject(candidate, ContextMaterializationRejection.SessionMismatch);

        }

        if (_entries.Any(entry => entry.Identity == candidate.Identity))
        {

            return Reject(candidate, ContextMaterializationRejection.DuplicateIdentity);

        }

        if (_entries.Any(
                entry => string.Equals(
                        entry.ContentHash,
                        candidate.ContentHash,
                        StringComparison.OrdinalIgnoreCase)
                    && entry.Identity.Range == candidate.Range
                    && (entry.SourceKind != ContextMaterializationSourceKind.CurrentTurnAttachment
                        || candidate.SourceKind != ContextMaterializationSourceKind.CurrentTurnAttachment)))
        {

            return Reject(candidate, ContextMaterializationRejection.DuplicateContentRange);

        }

        if (IsStaleAttachmentVersion(candidate))
        {

            return Reject(candidate, ContextMaterializationRejection.StaleVersion);

        }

        if (candidate.SourceKind == ContextMaterializationSourceKind.AttachmentRag
            && _entries.Any(entry => ExplicitWholeSourceMatches(entry, candidate)))
        {

            return Reject(
                candidate,
                ContextMaterializationRejection.ExplicitSourceAlreadyMaterialized);

        }

        if (candidate.SourceKind == ContextMaterializationSourceKind.WorkspaceRag
            && IsExplicitWorkspaceSource(candidate.SourceId))
        {

            return Reject(
                candidate,
                ContextMaterializationRejection.ExplicitSourceAlreadyMaterialized);

        }

        // Source precedence: accepted explicit material > exact raw leaf > derived Tapestry node.
        // The range-sensitive check above cannot enforce it, because a Tapestry node carries a whole
        // range while a workspace chunk carries its chunk index — identical text under different
        // ranges would slip through. A derived node is therefore rejected whenever its exact content
        // is already present under any range.
        if (candidate.SourceKind == ContextMaterializationSourceKind.TapestryMemory
            && _entries.Any(
                entry => entry.SourceKind != ContextMaterializationSourceKind.TapestryMemory
                    && string.Equals(
                        entry.ContentHash,
                        candidate.ContentHash,
                        StringComparison.OrdinalIgnoreCase)))
        {

            return Reject(candidate, ContextMaterializationRejection.DuplicateContentRange);

        }

        if (candidate.Origin == ContextMaterializationOrigin.Semantic
            && TryGetSemanticLimitRejection(candidate, out ContextMaterializationRejection rejection))
        {

            return Reject(candidate, rejection);

        }

        if (candidate.Origin != ContextMaterializationOrigin.Semantic
            && candidate.Range.IsWhole)
        {

            RemoveSuppressedAttachmentSemantic(candidate);

        }

        ContextMaterializationEntry accepted = ContextMaterializationEntry.FromCandidate(
            candidate,
            accepted: true,
            ContextMaterializationRejection.None);

        _entries.Add(accepted);

        if (candidate.AttachmentProvenance is { } provenance)
        {

            AttachmentMemoryGateAmbient.RegisterMaterialized(provenance);

        }
        else if (candidate.SourceKind is ContextMaterializationSourceKind.CurrentTurnAttachment
            or ContextMaterializationSourceKind.ExplicitAttachmentReference
            or ContextMaterializationSourceKind.AttachSessionFile
            or ContextMaterializationSourceKind.RefreshSessionFile
            or ContextMaterializationSourceKind.AttachmentRag)
        {

            AttachmentMemoryGateAmbient.RegisterUnprovenancedMaterialization();

        }

        return accepted;

    }

    public bool TryMarkInjected(
        ContextMaterializationIdentity identity,
        int providerRound)
    {

        int index = _entries.FindIndex(entry => entry.Identity == identity);

        if (index < 0 || _entries[index].Injected)
        {

            return false;

        }

        _entries[index] = _entries[index] with
        {
            Injected = true,
            ProviderRound = Math.Max(0, providerRound),
        };

        return true;

    }

    public bool Contains(ContextMaterializationIdentity identity) =>
        _entries.Any(entry => entry.Identity == identity);

    public void RegisterExplicitWorkspaceSource(string relativePath)
    {

        string normalized = NormalizeWorkspaceSource(relativePath);

        if (normalized.Length == 0 || !_explicitWorkspaceSources.Add(normalized))
        {

            return;

        }

        _entries.RemoveAll(
            entry => entry.SourceKind == ContextMaterializationSourceKind.WorkspaceRag
                && IsExplicitWorkspaceSource(entry.Identity.SourceId));

    }

    public ContextMaterializationEntry? DropLowestPrioritySemantic()
    {

        int index = -1;

        for (int i = 0; i < _entries.Count; i++)
        {

            ContextMaterializationEntry entry = _entries[i];

            if (entry.Origin != ContextMaterializationOrigin.Semantic)
            {

                continue;

            }

            if (index < 0
                || entry.SourceKind > _entries[index].SourceKind
                || entry.SourceKind == _entries[index].SourceKind && i > index)
            {

                index = i;

            }

        }

        if (index < 0)
        {

            return null;

        }

        ContextMaterializationEntry removed = _entries[index];

        _entries.RemoveAt(index);

        RecordContextPressureDrop(removed);

        return removed;

    }

    /// <summary>
    /// Un-counts a pressure drop that turned out to free nothing. <see cref="DropLowestPrioritySemantic"/>
    /// records the eviction as it surrenders the entry, but only the caller can tell whether the
    /// rendered prompt actually held a counterpart; when it did not, the reported <c>Dropped*</c> counts
    /// would otherwise claim context relief the turn never got. The entry itself stays surrendered —
    /// nothing in the payload matches it, so it is not context either.
    /// </summary>
    public void RescindContextPressureDrop(ContextMaterializationEntry removed)
    {

        ArgumentNullException.ThrowIfNull(removed);

        int tokens = Math.Max(0, removed.EstimatedTokens);

        if (removed.SourceKind == ContextMaterializationSourceKind.AttachmentRag)
        {

            _droppedAttachmentRagChunks = SaturatingDecrement(_droppedAttachmentRagChunks);

            _droppedAttachmentRagTokens = SaturatingSubtract(
                _droppedAttachmentRagTokens,
                tokens);

        }
        else if (removed.SourceKind == ContextMaterializationSourceKind.WorkspaceRag)
        {

            _droppedWorkspaceRagChunks = SaturatingDecrement(_droppedWorkspaceRagChunks);

            _droppedWorkspaceRagTokens = SaturatingSubtract(
                _droppedWorkspaceRagTokens,
                tokens);

        }
        else if (removed.SourceKind == ContextMaterializationSourceKind.TapestryMemory)
        {

            _droppedTapestryNodes = SaturatingDecrement(_droppedTapestryNodes);

            _droppedTapestryTokens = SaturatingSubtract(
                _droppedTapestryTokens,
                tokens);

        }

    }

    private void RecordContextPressureDrop(ContextMaterializationEntry removed)
    {

        int tokens = Math.Max(0, removed.EstimatedTokens);

        if (removed.SourceKind == ContextMaterializationSourceKind.AttachmentRag)
        {

            _droppedAttachmentRagChunks = SaturatingIncrement(_droppedAttachmentRagChunks);

            _droppedAttachmentRagTokens = SaturatingAdd(
                _droppedAttachmentRagTokens,
                tokens);

        }
        else if (removed.SourceKind == ContextMaterializationSourceKind.WorkspaceRag)
        {

            _droppedWorkspaceRagChunks = SaturatingIncrement(_droppedWorkspaceRagChunks);

            _droppedWorkspaceRagTokens = SaturatingAdd(
                _droppedWorkspaceRagTokens,
                tokens);

        }
        else if (removed.SourceKind == ContextMaterializationSourceKind.TapestryMemory)
        {

            _droppedTapestryNodes = SaturatingIncrement(_droppedTapestryNodes);

            _droppedTapestryTokens = SaturatingAdd(
                _droppedTapestryTokens,
                tokens);

        }

    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static int SaturatingAdd(int left, int right) =>
        int.CreateSaturating((long)Math.Max(0, left) + Math.Max(0, right));

    private static int SaturatingDecrement(int value) =>
        Math.Max(0, value - 1);

    private static int SaturatingSubtract(int left, int right) =>
        Math.Max(0, left - Math.Max(0, right));

    private bool IsStaleAttachmentVersion(ContextMaterializationCandidate candidate)
    {

        if (candidate.SourceKind != ContextMaterializationSourceKind.AttachmentRag
            || candidate.VersionOrdinal is not { } candidateVersion)
        {

            return false;

        }

        return _entries.Any(
            entry => entry.SourceKind == ContextMaterializationSourceKind.RefreshSessionFile
                && string.Equals(entry.Identity.SourceId, candidate.SourceId, StringComparison.Ordinal)
                && entry.VersionOrdinal is { } currentVersion
                && currentVersion > candidateVersion);

    }

    private static bool ExplicitWholeSourceMatches(
        ContextMaterializationEntry entry,
        ContextMaterializationCandidate candidate) =>
        entry.Origin != ContextMaterializationOrigin.Semantic
        && entry.Identity.Range.IsWhole
        && string.Equals(entry.Identity.SourceId, candidate.SourceId, StringComparison.Ordinal)
        && string.Equals(
            entry.Identity.VersionOrContentHash,
            candidate.VersionOrContentHash,
            StringComparison.Ordinal);

    private bool IsExplicitWorkspaceSource(string sourceId)
    {

        int rangeSeparator = sourceId.IndexOf('\u001f', StringComparison.Ordinal);

        string path = rangeSeparator < 0 ? sourceId : sourceId[..rangeSeparator];

        return _explicitWorkspaceSources.Contains(NormalizeWorkspaceSource(path));

    }

    private static string NormalizeWorkspaceSource(string value) =>
        value.Trim().Replace('\\', '/');

    private void RemoveSuppressedAttachmentSemantic(ContextMaterializationCandidate explicitSource)
    {

        if (explicitSource.VersionOrdinal is not { } currentVersion)
        {

            return;

        }

        _entries.RemoveAll(
            entry => entry.Origin == ContextMaterializationOrigin.Semantic
                && entry.SourceKind == ContextMaterializationSourceKind.AttachmentRag
                && string.Equals(entry.Identity.SourceId, explicitSource.SourceId, StringComparison.Ordinal)
                && entry.VersionOrdinal is { } existingVersion
                && (existingVersion == currentVersion
                    || explicitSource.SourceKind == ContextMaterializationSourceKind.RefreshSessionFile
                        && existingVersion < currentVersion));

    }

    private bool TryGetSemanticLimitRejection(
        ContextMaterializationCandidate candidate,
        out ContextMaterializationRejection rejection)
    {

        if (candidate.SourceKind != ContextMaterializationSourceKind.AttachmentRag)
        {

            rejection = ContextMaterializationRejection.None;

            return false;

        }

        ContextMaterializationEntry[] semantic = _entries
            .Where(entry => entry.SourceKind == ContextMaterializationSourceKind.AttachmentRag)
            .ToArray();

        if (semantic.Length >= _limits.MaxRetrievedChunks)
        {

            rejection = ContextMaterializationRejection.RetrievedChunkLimit;

            return true;

        }

        int representedAttachments = semantic
            .Where(entry => entry.SourceKind == ContextMaterializationSourceKind.AttachmentRag)
            .Select(entry => entry.Identity.SourceId)
            .Append(candidate.SourceId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (candidate.SourceKind == ContextMaterializationSourceKind.AttachmentRag
            && representedAttachments > _limits.MaxRetrievedAttachments)
        {

            rejection = ContextMaterializationRejection.RetrievedAttachmentLimit;

            return true;

        }

        long bytes = semantic.Sum(static entry => (long)entry.MaterializedBytes)
            + Math.Max(0, candidate.MaterializedBytes);

        if (bytes > _limits.MaxRetrievedBytes)
        {

            rejection = ContextMaterializationRejection.RetrievedByteLimit;

            return true;

        }

        long tokens = semantic.Sum(static entry => (long)entry.EstimatedTokens)
            + Math.Max(0, candidate.EstimatedTokens);

        if (tokens > _limits.MaxRetrievedTokens)
        {

            rejection = ContextMaterializationRejection.RetrievedTokenLimit;

            return true;

        }

        rejection = ContextMaterializationRejection.None;

        return false;

    }

    private static ContextMaterializationEntry Reject(
        ContextMaterializationCandidate candidate,
        ContextMaterializationRejection rejection) =>
        ContextMaterializationEntry.FromCandidate(candidate, accepted: false, rejection);

}

/// <summary>
/// Makes the owning turn ledger visible to post-tool attachment materialization without creating
/// another tool loop or persisting turn-local content state.
/// </summary>
public static class ContextMaterializationLedgerAmbient
{

    private static readonly AsyncLocal<State?> Current = new();

    public static ContextMaterializationLedger? Ledger => Current.Value?.Ledger;

    public static int ProviderRound => Current.Value?.ProviderRound ?? 0;

    public static void Begin(ContextMaterializationLedger ledger)
    {

        ArgumentNullException.ThrowIfNull(ledger);

        Current.Value?.AttachmentMemoryScope.Dispose();

        Current.Value = new State(
            ledger,
            0,
            AttachmentMemoryGateAmbient.BeginTurn(ledger.SessionId));

    }

    public static void SetProviderRound(int providerRound)
    {

        if (Current.Value is { } state)
        {

            state.ProviderRound = Math.Max(0, providerRound);

        }

    }

    public static void End()
    {

        State? state = Current.Value;

        Current.Value = null;

        state?.AttachmentMemoryScope.Dispose();

    }

    private sealed class State(
        ContextMaterializationLedger ledger,
        int providerRound,
        IDisposable attachmentMemoryScope)
    {

        public ContextMaterializationLedger Ledger { get; } = ledger;

        public int ProviderRound { get; set; } = providerRound;

        public IDisposable AttachmentMemoryScope { get; } = attachmentMemoryScope;

    }

}
