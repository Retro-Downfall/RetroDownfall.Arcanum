using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal interface IClientMutationResetEvidenceProbe
{

    Task<Result<ActiveInstallationReset?>> InspectAsync(
        CancellationToken cancellationToken);

}

internal interface IClientMutationRestoreEvidenceProbe
{

    Task<Result<ActiveReplacementRestore?>> InspectAsync(
        CancellationToken cancellationToken);

}

internal sealed record ActiveReplacementRestore(Guid OperationId);

internal sealed class CompositeClientMutationEvidenceProbe(
    ClientMutationBlockerStore blockerStore,
    IClientMutationResetEvidenceProbe resetEvidence,
    IClientMutationRestoreEvidenceProbe restoreEvidence)
    : IClientMutationEvidenceProbe
{

    private readonly ClientMutationBlockerStore _blockerStore =
        blockerStore ?? throw new ArgumentNullException(nameof(blockerStore));

    private readonly IClientMutationResetEvidenceProbe _resetEvidence =
        resetEvidence ?? throw new ArgumentNullException(nameof(resetEvidence));

    private readonly IClientMutationRestoreEvidenceProbe _restoreEvidence =
        restoreEvidence ?? throw new ArgumentNullException(nameof(restoreEvidence));

    public async Task<ClientMutationEvidenceResult> InspectAsync(
        CancellationToken cancellationToken)
    {

        Result<ClientMutationBlockerPublication?> blocker = await _blockerStore
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (blocker.IsFailure)
        {

            return ClientMutationEvidenceResult.Unsafe(blocker.Error);

        }

        if (blocker.Value is not null)
        {

            return ClientMutationEvidenceResult.Blocked(new Error(
                ErrorCodes.Data.ResetInProgress,
                "An installation reset or replacement restore is active."));

        }

        Result<ActiveInstallationReset?> reset = await _resetEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {

            return ClientMutationEvidenceResult.Unsafe(reset.Error);

        }

        if (reset.Value is not null)
        {

            return ClientMutationEvidenceResult.Blocked(new Error(
                ErrorCodes.Data.ResetInProgress,
                "An installation reset is active."));

        }

        Result<ActiveReplacementRestore?> restore = await _restoreEvidence
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (restore.IsFailure)
        {

            return ClientMutationEvidenceResult.Unsafe(restore.Error);

        }

        return restore.Value is not null
            ? ClientMutationEvidenceResult.Blocked(new Error(
                ErrorCodes.Data.FileLocked,
                "A replacement restore is active."))
            : ClientMutationEvidenceResult.Clear();

    }

}
