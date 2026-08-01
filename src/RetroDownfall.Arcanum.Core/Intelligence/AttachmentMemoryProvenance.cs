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
public static class AttachmentMemoryGateAmbient
{

    private static readonly AsyncLocal<State?> Current = new();

    private static readonly ConcurrentDictionary<Guid, State> BySession = [];

    public static bool HasMaterializedAttachmentContent =>
        ResolveState() is { HadAttachmentContent: true };

    public static bool HasUnprovenancedAttachmentContent =>
        ResolveState() is { HadUnprovenancedAttachmentContent: true };

    public static IDisposable BeginTurn(Guid? sessionId = null)
    {

        State? prior = Current.Value;

        State state = new(sessionId);

        Current.Value = state;

        if (sessionId is { } boundSessionId)
        {

            BySession[boundSessionId] = state;

        }

        return new Scope(prior, state);

    }

    public static void RegisterMaterialized(AttachmentMemoryProvenance provenance)
    {

        ArgumentNullException.ThrowIfNull(provenance);

        State? state = ResolveState();

        if (state is null)
        {

            return;

        }

        state.MarkAttachmentContent();

        state.Materialized[provenance.AttachmentId] = provenance with
        {
            Availability = AttachmentSourceAvailability.Available,
        };

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
            ? [.. state.Materialized.Values.OrderBy(static source => source.AttachmentId)]
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

    private sealed class State(Guid? sessionId)
    {

        public Guid? SessionId { get; } = sessionId;

        private int _hadAttachmentContent;

        private int _hadUnprovenancedAttachmentContent;

        public ConcurrentDictionary<Guid, AttachmentMemoryProvenance> Materialized { get; } = [];

        public bool HadAttachmentContent => Volatile.Read(ref _hadAttachmentContent) != 0;

        public bool HadUnprovenancedAttachmentContent =>
            Volatile.Read(ref _hadUnprovenancedAttachmentContent) != 0;

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

    private sealed class Scope(State? prior, State owned) : IDisposable
    {

        private State? _prior = prior;

        public void Dispose()
        {

            Current.Value = Interlocked.Exchange(ref _prior, null);

            if (owned.SessionId is { } sessionId)
            {

                _ = BySession.TryRemove(
                    new KeyValuePair<Guid, State>(sessionId, owned));

            }

        }

    }

}
