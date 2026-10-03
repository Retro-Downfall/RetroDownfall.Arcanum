using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The labelled-artifact guard's forms for a delete that owns a write transaction.
/// </summary>
/// <remarks>
/// A check made before the transaction opens and a delete made inside it are two moments, and a label
/// written between them is removed with its artifact and leaves nothing behind. Asked inside the
/// transaction, the answer and the delete are one moment. The read goes through the transaction it is
/// handed, on the connection that transaction is on, so no second connection is opened and a label the
/// same transaction has written is seen.
///
/// <para>The connection travels with the transaction, as it does for every other helper that reads or
/// writes inside one. A transaction names its connection only as the provider-neutral base type, with
/// nothing to say what concrete connection that is; the caller that began the transaction on a
/// connection it opened is the one place that knows, and a guard that read the connection back off the
/// transaction would have to take it on trust. A transaction that is not on the connection handed with
/// it is refused rather than read around.</para>
///
/// <para>These forms are on an interface of their own, inside Infrastructure, because they name an ADO.NET
/// transaction and Core names no storage type. Every caller that owns a transaction is in this assembly;
/// the composition registers one guard behind both interfaces, so the Core form and these resolve to the
/// same instance.</para>
/// </remarks>
internal interface ICovenantLabeledArtifactTransactionGuard : ICovenantLabeledArtifactGuard
{

    /// <summary>
    /// Confirms the artifact carries no live sensitivity label, reading inside the caller's write
    /// transaction.
    /// </summary>
    ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms no artifact of this kind carries a live label anywhere in the installation, reading
    /// inside the caller's write transaction.
    /// </summary>
    /// <remarks>
    /// The bulk arm. A set-based <c>DELETE FROM</c> examines no identity at all, so there is no single
    /// artifact to ask about — the only honest question is whether the kind has any protected member
    /// left, and the only safe answer for "yes" is to refuse. It has no form outside a transaction: a
    /// bulk delete always owns one, and an answer given outside it would be the check-then-delete shape
    /// this interface exists to remove.
    /// </remarks>
    ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);

}
