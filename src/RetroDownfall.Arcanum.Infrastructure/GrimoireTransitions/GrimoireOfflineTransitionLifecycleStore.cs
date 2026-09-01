using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal sealed record GrimoireOfflineTransitionTypedPublication(
    GrimoireOfflineTransitionJournalPublication Raw,
    IGrimoireOfflineTransitionHandler Handler,
    IGrimoireOfflineTransitionPayload Payload,
    GrimoireOfflineTransitionHandlerOutcome Outcome);

internal enum GrimoireOfflineTransitionTypedRecoveryOutcome : byte
{

    NoActiveJournal = 1,

    Authenticated = 2,

}

internal sealed record GrimoireOfflineTransitionTypedRecoveryState(
    GrimoireOfflineTransitionTypedRecoveryOutcome Outcome,
    GrimoireOfflineTransitionTypedPublication? Publication);

internal sealed class GrimoireOfflineTransitionLifecycleStore(
    IGrimoireOfflineTransitionJournalStore journal,
    GrimoireOfflineTransitionHandlerRegistry registry)
{

    private readonly IGrimoireOfflineTransitionJournalStore _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));

    private readonly GrimoireOfflineTransitionHandlerRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    internal async Task<Result<GrimoireOfflineTransitionTypedPublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        IGrimoireOfflineTransitionPayload payload,
        CancellationToken cancellationToken)
    {

        if (payload is null
            || payload.Lifecycle.State is not GrimoireOfflineTransitionState.Prepared
            || payload.Lifecycle.TerminalIntent
                is not GrimoireOfflineTransitionTerminalIntent.Undecided
            || payload.Lifecycle.Blocker is not null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result<byte[]> encoded = _registry.Encode(payload);

        if (encoded.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result<GrimoireOfflineTransitionJournalPublication> begun = await _journal.BeginAsync(
                heldInstallationLock,
                guardedDirectory,
                installationId,
                payload.Binding.OperationId,
                payload.Binding.Kind,
                payload.Binding.PayloadVersion,
                encoded.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return begun.IsFailure
            ? Result<GrimoireOfflineTransitionTypedPublication>.Failure(begun.Error)
            : Decode(begun.Value);

    }

    internal async Task<Result<GrimoireOfflineTransitionTypedPublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionTypedPublication current,
        IGrimoireOfflineTransitionPayload next,
        CancellationToken cancellationToken)
    {

        if (current is null || next is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result<GrimoireOfflineTransitionTypedPublication> decodedCurrent = Decode(current.Raw);

        if (decodedCurrent.IsFailure
            || !Equals(decodedCurrent.Value.Payload, current.Payload)
            || decodedCurrent.Value.Handler.Kind != current.Handler.Kind
            || decodedCurrent.Value.Handler.PayloadVersion != current.Handler.PayloadVersion)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result valid = current.Handler.ValidateAdvance(current.Payload, next);

        Result<byte[]> encoded = valid.IsSuccess
            ? _registry.Encode(next)
            : Result<byte[]>.Failure(valid.Error);

        if (encoded.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result<GrimoireOfflineTransitionJournalPublication> advanced =
            await _journal.AdvanceAsync(
                    heldInstallationLock,
                    current.Raw,
                    encoded.Value,
                    cancellationToken)
                .ConfigureAwait(false);

        return advanced.IsFailure
            ? Result<GrimoireOfflineTransitionTypedPublication>.Failure(advanced.Error)
            : Decode(advanced.Value);

    }

    internal async Task<Result<GrimoireOfflineTransitionTypedRecoveryState>> RecoverAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered =
            await _journal.RecoverAsync(
                    heldInstallationLock,
                    guardedDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<GrimoireOfflineTransitionTypedRecoveryState>.Failure(recovered.Error);

        }

        if (recovered.Value.Outcome
            is GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal)
        {

            return new GrimoireOfflineTransitionTypedRecoveryState(
                GrimoireOfflineTransitionTypedRecoveryOutcome.NoActiveJournal,
                Publication: null);

        }

        if (recovered.Value.Publication is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedRecoveryState>();

        }

        Result<GrimoireOfflineTransitionTypedPublication> decoded =
            Decode(recovered.Value.Publication);

        return decoded.IsSuccess
            ? new GrimoireOfflineTransitionTypedRecoveryState(
                GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated,
                decoded.Value)
            : RecoveryRequired<GrimoireOfflineTransitionTypedRecoveryState>();

    }

    internal async Task<Result> RetireAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionTypedPublication terminal,
        CancellationToken cancellationToken)
    {

        if (terminal is null
            || terminal.Payload.Lifecycle.State
                is not GrimoireOfflineTransitionState.RetirementPending
            || terminal.Payload.Lifecycle.TerminalIntent
                is GrimoireOfflineTransitionTerminalIntent.Undecided
            || terminal.Payload.Lifecycle.ReconciliationEvidence is not { IsComplete: true })
        {

            return RecoveryRequired();

        }

        Result<GrimoireOfflineTransitionTypedPublication> decoded = Decode(terminal.Raw);

        if (decoded.IsFailure || !Equals(decoded.Value.Payload, terminal.Payload))
        {

            return RecoveryRequired();

        }

        return await _journal.RetireAsync(
                heldInstallationLock,
                terminal.Raw,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private Result<GrimoireOfflineTransitionTypedPublication> Decode(
        GrimoireOfflineTransitionJournalPublication publication)
    {

        if (publication is null
            || publication.Envelope.OperationId == Guid.Empty
            || publication.Envelope.SlotEpoch == 0
            || publication.PayloadBytes is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

        }

        Result<GrimoireOfflineTransitionDecodedPayload> decoded =
            _registry.DecodeAuthenticated(
                publication.Envelope.Kind,
                publication.Envelope.PayloadVersion,
                publication.PayloadBytes,
                publication.Envelope.OperationId,
                publication.Envelope.SlotEpoch);

        return decoded.IsSuccess
            ? new GrimoireOfflineTransitionTypedPublication(
                publication,
                decoded.Value.Handler,
                decoded.Value.Payload,
                decoded.Value.Outcome)
            : RecoveryRequired<GrimoireOfflineTransitionTypedPublication>();

    }

    private static Result RecoveryRequired() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition payload cannot be recovered by this build.");

    private static Result<T> RecoveryRequired<T>() => Result<T>.Failure(
        RecoveryRequired().Error);

}
