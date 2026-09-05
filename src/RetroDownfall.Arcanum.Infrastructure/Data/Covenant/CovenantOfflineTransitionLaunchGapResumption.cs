using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Finishes the one launch a crash left committed with no journal, before readiness is published.
/// </summary>
/// <remarks>
/// The launch-row commit and the first journal publication cannot be one atomic storage transaction,
/// so a crash between them leaves a committed, nonterminal launch row and no journal. Nothing
/// destructive has happened — the journal is published and verified before admission closes — and the
/// row is both safe to resume and unsafe to leave: a nonterminal durable operation blocks every later
/// data-retention operation until somebody finishes it.
///
/// <para>Until now the adopted owner was handed to the periodic reconciler, which runs after readiness
/// on a ten-second startup budget, with the host already serving and the resumption free to spill into
/// the background while requests arrive. A transition closes admission; it may not begin after the
/// signal every pool, worker and endpoint waits on has been published.</para>
///
/// <para>The scan itself is unchanged and stays where it is.
/// <see cref="CovenantErasureStartupRecoveryOwnerAdopter"/> already admits only the two current launch
/// versions with their exact checkpoint references and recovery policies, already reports an ordinary
/// retention mutation as no owner at all, and already refuses a second adoptable row and every legacy,
/// malformed, and mis-policied shape. A second scanner over the same rows would be a second answer to
/// one question.</para>
/// </remarks>
internal static class CovenantOfflineTransitionLaunchGapResumption
{

    /// <summary>
    /// Resumes the adopted owner, or reports that nothing was adopted.
    /// </summary>
    /// <remarks>
    /// A null owner is success with no effect: it is what the adopter returns for an installation with
    /// no unfinished launch, and for an ordinary retention mutation that closed nothing. Only a durable
    /// verdict is a resumption — a parked transition has closed admission behind it, and publishing
    /// readiness over one would open the catalog to everything that waits on that signal.
    /// </remarks>
    internal static async Task<Result> ResumeBeforeReadinessAsync(
        IGrimoireOfflineTransitionHandlerDispatch dispatch,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CovenantExclusiveRecoveryOwner? adopted,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(dispatch);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (adopted is not { } owner)
        {

            return Result.Success();

        }

        Result<LongRunningOperationSettlementOutcome> dispatched = await dispatch
            .DispatchAsync(heldInstallationLock, guardedDirectory, owner.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (dispatched.IsFailure)
        {

            return Result.Failure(dispatched.Error);

        }

        return dispatched.Value is LongRunningOperationSettlementOutcome.Completed
            or LongRunningOperationSettlementOutcome.Failed
            or LongRunningOperationSettlementOutcome.Abandoned
                ? Result.Success()
                : Refusal();

    }

    private static Result Refusal() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "An offline transition launched before this start could not be finished before readiness.");

}
