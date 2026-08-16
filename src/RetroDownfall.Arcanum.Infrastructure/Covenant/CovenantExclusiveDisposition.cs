using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// What an exclusive operation proved before it decided how to leave admission.
/// </summary>
/// <param name="StorageVerified">
/// The durable effect the operation attempted was observed to have either fully committed or fully
/// not happened. Uncertainty here is the case that must never reopen.
/// </param>
/// <param name="AuthorityVerified">
/// The lease and the authority generation it captured were still current at the decision point.
/// </param>
/// <param name="DurablyMutated">
/// Whether any irreversible change was actually written. Only a proven "no" may roll back.
/// </param>
/// <param name="HealthPublished">
/// Whether the matching health, key, and availability transition was published while the gate was
/// still closed.
/// </param>
internal readonly record struct CovenantExclusiveDispositionEvidence(
    bool StorageVerified,
    bool AuthorityVerified,
    bool DurablyMutated,
    bool HealthPublished);

/// <summary>
/// The one place an exclusive Covenant operation decides how it leaves admission.
/// </summary>
/// <remarks>
/// A pure function over evidence, with exactly one outcome per input, because the three dispositions
/// are not interchangeable and the lease will only accept one of them. <c>RollbackAndReopen</c>
/// asserts that nothing durable happened; <c>CommitAndReopen</c> asserts that everything did and that
/// the matching transition is already published; <c>KeepClosed</c> asserts neither, which is why it is
/// the answer to every form of uncertainty rather than a failure code.
///
/// <para>The asymmetry is deliberate. Reopening after an unproven commit hands live traffic a database
/// nobody verified, while staying closed costs availability and preserves the operation for recovery —
/// and recovery can still finish it, because <c>KeepClosed</c> keeps the recovery owner (§10.17).</para>
/// </remarks>
internal static class CovenantExclusiveDisposition
{

    internal static CovenantExclusiveLeaseDisposition Select(CovenantExclusiveDispositionEvidence evidence)
    {

        if (!evidence.StorageVerified || !evidence.AuthorityVerified)
        {

            return CovenantExclusiveLeaseDisposition.KeepClosed;

        }

        if (!evidence.DurablyMutated)
        {

            return CovenantExclusiveLeaseDisposition.RollbackAndReopen;

        }

        return evidence.HealthPublished
            ? CovenantExclusiveLeaseDisposition.CommitAndReopen
            : CovenantExclusiveLeaseDisposition.KeepClosed;

    }

    /// <summary>
    /// Whether a disposition permits the caller's durable journal to reach a terminal phase.
    /// </summary>
    /// <remarks>
    /// Only the two reopening dispositions do. A journal that terminalized after
    /// <see cref="CovenantExclusiveLeaseDisposition.KeepClosed"/> would record a finished operation
    /// while admission is still shut, and the next start would find nothing to resume.
    /// </remarks>
    internal static bool IsTerminalizing(CovenantExclusiveLeaseDisposition disposition) =>
        disposition is CovenantExclusiveLeaseDisposition.CommitAndReopen
            or CovenantExclusiveLeaseDisposition.RollbackAndReopen;

}

/// <summary>
/// The one-shot finalizer that advances a schema-repair journal after its disposition succeeded.
/// </summary>
/// <remarks>
/// It runs only after the exact lease returns success from its one disposition, and never otherwise.
/// A failed disposition leaves the journal at <c>ReopenPending</c> on purpose: the operation is then
/// resumable, which is strictly safer than recording a terminal phase nobody proved.
/// </remarks>
internal sealed class CovenantSchemaRepairPostDispositionFinalizer(
    Func<CovenantSchemaRepairPhase, CancellationToken, Task<Result>> advance)
    : ICovenantExclusivePostDispositionFinalizer
{

    private readonly Func<CovenantSchemaRepairPhase, CancellationToken, Task<Result>> _advance =
        advance ?? throw new ArgumentNullException(nameof(advance));

    private int _invoked;

    /// <summary>Whether the finalizer has already been used.</summary>
    internal bool WasInvoked => Volatile.Read(ref _invoked) != 0;

    public async ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken)
    {

        if (Interlocked.Exchange(ref _invoked, 1) != 0)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "A schema repair journal finalizer runs exactly once."));

        }

        if (!CovenantExclusiveDisposition.IsTerminalizing(disposition))
        {

            // A successful KeepClosed is a real outcome, and its journal must stay exactly where it
            // is. Verifying rather than advancing is the whole contribution this arm makes.
            return Result.Success();

        }

        return await _advance(
            disposition == CovenantExclusiveLeaseDisposition.CommitAndReopen
                ? CovenantSchemaRepairPhase.Completed
                : CovenantSchemaRepairPhase.Abandoned,
            cancellationToken).ConfigureAwait(false);

    }

}
