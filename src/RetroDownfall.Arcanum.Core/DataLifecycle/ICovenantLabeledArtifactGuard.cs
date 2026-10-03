using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// Issue #117 — the check a legacy raw delete makes before it removes a row that might be labelled.
/// </summary>
/// <remarks>
/// The six routes already dispatch through <see cref="ICovenantSensitiveArtifactPurger"/>, so in normal
/// operation this guard never fires. It exists for the caller that does not: a repository method is
/// reachable from anywhere in the process, and "every caller remembers to ask the purger first" is a
/// convention rather than a property. This turns it into a property.
///
/// <para>A labelled artifact removed through a raw delete leaves its label behind — pointing at content
/// nothing admits is tainted — and skips the erasure receipt that lets a replayed claim answer
/// <c>Covenant.ArtifactErased</c> instead of looking like data loss (§10.20.2).</para>
///
/// <para>This is the contract a caller outside the database layer sees, so it names no storage type.
/// The forms a delete that owns a write transaction has to use take that transaction, and live on the
/// Infrastructure interface that extends this one.</para>
/// </remarks>
public interface ICovenantLabeledArtifactGuard
{

    /// <summary>
    /// Confirms the artifact carries no live sensitivity label.
    /// </summary>
    /// <remarks>
    /// An installation with no Covenant arm answers success: its label table, a Core object, is empty and
    /// nothing is protected. A label table that cannot be read answers failure, not success, because a
    /// Grimoire whose protection cannot be checked is not one nothing is protected in. That failure is
    /// <c>Covenant.Unavailable</c>, the Grimoire's condition and not the caller's. A labelled artifact is
    /// refused with <c>Covenant.ForbiddenAuthority</c> instead, because it has to leave through a path that
    /// can erase it correctly.
    /// </remarks>
    ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default);

}
