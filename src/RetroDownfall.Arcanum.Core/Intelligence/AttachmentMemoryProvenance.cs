namespace RetroDownfall.Arcanum.Core.Intelligence;

using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Storage;

public enum AttachmentSourceAvailability
{
    Available,

    Unavailable,
}

/// <summary>
/// Typed source identity carried by every durable memory that was derived from attachment content.
/// It contains no raw bytes or host paths and remains useful after the source is deleted.
/// </summary>
public sealed record AttachmentMemoryProvenance(
    Guid SessionId,
    Guid AttachmentId,
    string LogicalKey,
    int Version,
    string ContentHash,
    DateTimeOffset MaterializedAt,
    string SourceType,
    AttachmentSourceAvailability Availability);

public interface IAttachmentMemoryProvenanceStore
{
    Task RecordConsultationsAsync(
        Guid sourceEntryId,
        IReadOnlyList<AttachmentMemoryProvenance> provenance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AttachmentMemoryProvenance>> ListConsultationsAsync(
        Guid sessionId,
        DateTimeOffset afterExclusive,
        DateTimeOffset throughInclusive,
        CancellationToken cancellationToken);
}

/// <summary>
/// Logical-turn attachment promotion authority. Only successfully materialized attachment versions
/// are resolvable, and the scope is cleared when the inference turn ends.
/// </summary>
/// <remarks>
/// The turn's state is held by the <see cref="TurnScope"/> <see cref="BeginTurn"/> returns, not by the
/// ambient. The ambient is only how a tool finds it, and an <c>AsyncLocal</c> written inside an async
/// iterator does not survive the iterator's next <c>yield return</c>, so the turn loop keeps the scope
/// and re-establishes it with <see cref="Enter"/> before each tool call. Ending the turn disposes that
/// same scope, which is what removes its per-Session entry.
/// </remarks>
public static class AttachmentMemoryGateAmbient
{
    private static readonly AsyncLocal<State?> Current = new();

    private static readonly ConcurrentDictionary<Guid, State> BySession = [];

    public static bool HasMaterializedAttachmentContent =>
        ResolveState() is { HadAttachmentContent: true };

    public static bool HasUnprovenancedAttachmentContent =>
        ResolveState() is { HadUnprovenancedAttachmentContent: true };

    /// <summary>Test hook: whether a turn's per-Session gate state is still registered.</summary>
    internal static bool HasSessionStateForTests(Guid sessionId) => BySession.ContainsKey(sessionId);

    public static TurnScope BeginTurn(Guid? sessionId = null)
    {
        State? prior = Current.Value;

        State state = new(sessionId);

        Current.Value = state;

        if (sessionId is { } boundSessionId)
        {
            BySession[boundSessionId] = state;
        }

        return new TurnScope(prior, state);
    }

    /// <summary>
    /// Makes an already-begun turn's state the ambient one again, without replacing it.
    /// </summary>
    /// <remarks>
    /// Not a second <see cref="BeginTurn"/>: that would start an empty state and drop every attachment
    /// the turn has materialized so far.
    /// </remarks>
    public static void Enter(TurnScope? scope) => Current.Value = scope?.Owned;

    public static void RegisterMaterialized(AttachmentMemoryProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        if (ResolveState() is { } state)
        {
            state.RegisterMaterialized(provenance);
        }
    }

    public static void RegisterUnprovenancedMaterialization()
    {
        if (ResolveState() is { } state)
        {
            state.MarkUnprovenancedAttachmentContent();
        }
    }

    public static bool TryResolve(
        Guid attachmentId,
        out AttachmentMemoryProvenance provenance)
    {
        if (ResolveState() is { } state
            && state.Materialized.TryGetValue(attachmentId, out AttachmentMemoryProvenance? found))
        {
            provenance = found;

            return true;
        }

        provenance = null!;

        return false;
    }

    public static IReadOnlyList<AttachmentMemoryProvenance> Snapshot() =>
        ResolveState() is { } state
            ? state.Snapshot()
            : [];

    private static State? ResolveState()
    {
        if (Current.Value is { } current)
        {
            return current;
        }

        return SessionAttachmentToolAmbient.CurrentSessionId is { } sessionId
            && BySession.TryGetValue(sessionId, out State? bound)
                ? bound
                : null;
    }

    internal sealed class State(Guid? sessionId)
    {
        public Guid? SessionId { get; } = sessionId;

        private int _hadAttachmentContent;

        private int _hadUnprovenancedAttachmentContent;

        public ConcurrentDictionary<Guid, AttachmentMemoryProvenance> Materialized { get; } = [];

        public bool HadAttachmentContent => Volatile.Read(ref _hadAttachmentContent) != 0;

        public bool HadUnprovenancedAttachmentContent =>
            Volatile.Read(ref _hadUnprovenancedAttachmentContent) != 0;

        public void RegisterMaterialized(AttachmentMemoryProvenance provenance)
        {
            MarkAttachmentContent();

            Materialized[provenance.AttachmentId] = provenance with
            {
                Availability = AttachmentSourceAvailability.Available,
            };
        }

        public IReadOnlyList<AttachmentMemoryProvenance> Snapshot() =>
            [.. Materialized.Values.OrderBy(static source => source.AttachmentId)];

        public void MarkAttachmentContent()
        {
            _ = Interlocked.Exchange(ref _hadAttachmentContent, 1);
        }

        public void MarkUnprovenancedAttachmentContent()
        {
            MarkAttachmentContent();

            _ = Interlocked.Exchange(ref _hadUnprovenancedAttachmentContent, 1);
        }
    }

    /// <summary>
    /// One turn's attachment promotion state, held by the turn that began it.
    /// </summary>
    /// <remarks>
    /// Every member reads or writes the captured state directly, so the turn's own bookkeeping — the
    /// ledger registering what it materialized, the end-of-turn provenance snapshot, and disposal — is
    /// correct whatever the ambient happens to hold in the segment that runs it.
    /// </remarks>
    public sealed class TurnScope : IDisposable
    {
        private State? _prior;

        internal TurnScope(State? prior, State owned)
        {
            _prior = prior;

            Owned = owned;
        }

        internal State Owned { get; }

        public bool HasMaterializedAttachmentContent => Owned.HadAttachmentContent;

        public bool HasUnprovenancedAttachmentContent => Owned.HadUnprovenancedAttachmentContent;

        public void RegisterMaterialized(AttachmentMemoryProvenance provenance)
        {
            ArgumentNullException.ThrowIfNull(provenance);

            Owned.RegisterMaterialized(provenance);
        }

        public void RegisterUnprovenancedMaterialization() => Owned.MarkUnprovenancedAttachmentContent();

        public IReadOnlyList<AttachmentMemoryProvenance> Snapshot() => Owned.Snapshot();

        public void Dispose()
        {
            Current.Value = Interlocked.Exchange(ref _prior, null);

            if (Owned.SessionId is { } sessionId)
            {
                _ = BySession.TryRemove(
                    new KeyValuePair<Guid, State>(sessionId, Owned));
            }
        }
    }
}
