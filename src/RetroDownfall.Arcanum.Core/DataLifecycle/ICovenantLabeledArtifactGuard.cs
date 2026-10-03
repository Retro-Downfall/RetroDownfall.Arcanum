using System.Data.Common;

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
/// </remarks>
public interface ICovenantLabeledArtifactGuard
{

    /// <summary>
    /// Confirms the artifact carries no live sensitivity label.
    /// </summary>
    /// <remarks>
    /// An installation with no Covenant arm answers success: its label table, a Core object, is empty and
    /// nothing is protected. A label table that cannot be read answers failure, not success, because a
    /// Grimoire whose protection cannot be checked is not one nothing is protected in. A failure here
    /// otherwise means the caller reached a labelled artifact through a path that cannot erase it
    /// correctly.
    /// </remarks>
    ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms no artifact of this kind carries a live label anywhere in the installation.
    /// </summary>
    /// <remarks>
    /// The bulk arm. A set-based <c>DELETE FROM</c> examines no identity at all, so there is no single
    /// artifact to ask about — the only honest question is whether the kind has any protected member
    /// left, and the only safe answer for "yes" is to refuse.
    /// </remarks>
    ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the artifact carries no live sensitivity label, reading inside the caller's write
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The shape a delete that owns a transaction has to use. A check made before the transaction opens
    /// and a delete made inside it are two moments, and a label written between them is removed with its
    /// artifact and leaves nothing behind. Asked inside the transaction, the answer and the delete are
    /// one moment. The read goes through <paramref name="transaction"/> on its own connection, so no
    /// second connection is opened and a label the same transaction has written is seen.
    /// </remarks>
    ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms no artifact of this kind carries a live label anywhere in the installation, reading
    /// inside the caller's write transaction.
    /// </summary>
    /// <remarks>
    /// The bulk arm for a delete that owns a transaction, for the reason the single arm above has a
    /// transaction form.
    /// </remarks>
    ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);

}
