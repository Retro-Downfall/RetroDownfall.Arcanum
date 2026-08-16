using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>One assistant artifact returned under a proven-current lease.</summary>
public sealed record ProtectedAssistantArtifact(
    Guid SessionId,
    Guid AssistantEntryId,
    string Content,
    ContentSensitivity Sensitivity,
    ArtifactSensitivityLabel? Label);

/// <summary>
/// The one reader of a possibly-tainted assistant artifact.
/// </summary>
/// <remarks>
/// Reads the artifact and its label in the same SQLite snapshot. Two reads would let a label be
/// written, replaced, or purged between them, and the interesting failure is the one where the
/// artifact looks clean because its label had not landed yet (§10.12).
///
/// <para>The lease is the existing generation-bound <see cref="ICovenantSnapshotReadLease"/> the
/// operation gate issues, revalidated on entry and again before the value is handed back. A reset,
/// an erasure, a Campaign remap, or a host-tools taint advances the generation it carries, so a
/// long-running read cannot outlive the authority that permitted it.</para>
///
/// <para>Every failure mode is a refusal rather than a redaction. Returning the row with its content
/// blanked would still disclose that a protected artifact exists at that identity, which is the
/// membership signal the protected partition exists to withhold.</para>
/// </remarks>
public interface IProtectedAssistantArtifactReader
{

    Task<Result<ProtectedAssistantArtifact>> ReadAsync(
        Guid sessionId,
        Guid assistantEntryId,
        ICovenantSnapshotReadLease lease,
        CancellationToken cancellationToken);

}
