using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// One physical attempt at a Covenant tool effect, and everything its receipt has to record.
/// </summary>
/// <remarks>
/// <paramref name="PhysicalAttemptOrdinal"/> is the field that keeps the accounting honest. A retry
/// after a timeout and a reconnect after a dropped stream are separate physical disclosures even when
/// they carry identical arguments, so each one advances the ordinal and is counted separately. A
/// genuine replay of the same attempt keeps its ordinal and reuses the acknowledged identity. The
/// journal is therefore allowed to overstate and never to understate (§10.14).
/// </remarks>
public sealed record CovenantToolEgressAttempt(
    Guid OriginInstallationId,
    Guid LogicalTurnId,
    ulong PhysicalAttemptOrdinal,
    CovenantDigest ProducingAdmissionDigest,
    CovenantDigest CapabilityNonceDigest,
    string ToolCallId,
    CovenantDigest FrozenToolNameDigest,
    CovenantDigest CanonicalArgumentDigest,
    CovenantEgressDestination Destination,
    CovenantDisclosureRevocability Revocability,
    CovenantDigest OpaqueDestinationIdentityDigest,
    ProviderCallSensitivity Sensitivity,
    CovenantDigest? WardEvidenceDigest,
    long Timestamp);

/// <summary>
/// Commits the disclosure receipt for a Covenant tool effect, then runs the effect.
/// </summary>
/// <remarks>
/// The ordering is the whole contract, so the guard owns both halves rather than offering an
/// "acknowledge" method a caller could forget to call. A journal written after the fact cannot record
/// the one case it exists for: an effect that left the process and then crashed, reconnected, or lost
/// its answer. The operator would be told nothing was disclosed while the payload was already gone.
///
/// <para>A journal that cannot commit stops the effect entirely. Proceeding without evidence would be
/// a disclosure Arcanum could never account for afterwards.</para>
/// </remarks>
public sealed class CovenantToolEgressGuard(ICovenantDisclosureJournal journal)
{

    private readonly ICovenantDisclosureJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));

    public async ValueTask<Result<T>> DiscloseThenAsync<T>(
        CovenantToolEgressAttempt attempt,
        Func<CovenantDisclosureReceipt, CancellationToken, ValueTask<Result<T>>> effect,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(attempt);

        ArgumentNullException.ThrowIfNull(effect);

        Result<CovenantDisclosureReceipt> acknowledged = await _journal
            .AcknowledgeAsync(
                BuildDraft(attempt),
                CovenantDisclosureEffectCategory.McpToolUse,
                attempt.Sensitivity,
                cancellationToken)
            .ConfigureAwait(false);

        if (acknowledged.IsFailure)
        {
            return Result<T>.Failure(acknowledged.Error);
        }

        return await effect(acknowledged.Value, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>The effect identity this attempt will be accounted under.</summary>
    public static CovenantDigest EffectIdentity(CovenantToolEgressAttempt attempt)
    {

        ArgumentNullException.ThrowIfNull(attempt);

        return CovenantDigests.ToolEgressEffect(new ToolEgressEffectDigestInput(
            attempt.LogicalTurnId,
            attempt.PhysicalAttemptOrdinal,
            attempt.ProducingAdmissionDigest,
            attempt.CapabilityNonceDigest,
            attempt.ToolCallId,
            attempt.FrozenToolNameDigest,
            attempt.CanonicalArgumentDigest,
            attempt.Destination,
            attempt.OpaqueDestinationIdentityDigest));

    }

    private static CovenantDisclosureDraft BuildDraft(CovenantToolEgressAttempt attempt) =>
        new(
            attempt.OriginInstallationId,
            CovenantDisclosureSubjectKind.Turn,
            attempt.LogicalTurnId,
            EffectIdentity(attempt),
            attempt.Destination,
            attempt.Revocability,
            attempt.OpaqueDestinationIdentityDigest,
            attempt.Sensitivity.Digest,
            attempt.WardEvidenceDigest,
            attempt.ProducingAdmissionDigest,
            backupEvidenceDigest: null,
            attempt.Timestamp);

}
