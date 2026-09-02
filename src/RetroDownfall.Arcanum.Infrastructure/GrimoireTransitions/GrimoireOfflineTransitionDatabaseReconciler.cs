using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// The two terminal states an offline transition may write to its own operation row.
/// </summary>
/// <remarks>
/// Deliberately narrower than the durable operation lifecycle. A transition that reached its ending
/// completed; a transition the journal can prove performed no effect failed. Everything between
/// those two is a state the journal is still holding, and a row written from it would announce an
/// answer the transition has not reached.
/// </remarks>
internal enum GrimoireOfflineTransitionTerminalDisposition : byte
{

    Completed = 1,

    FailedBeforeEffect = 2,

}

/// <summary>
/// What one terminal reconciliation attempt found, and whether the journal may retire on it.
/// </summary>
/// <remarks>
/// Every arm is separate because every arm has a different remedy, and because a reconciler whose
/// refusals were one value could be replaced by a constant without a test noticing. Only
/// <see cref="Terminalized"/> and <see cref="AlreadyTerminal"/> are answers; the rest leave the
/// journal in database reconciliation and ordinary admission closed.
/// </remarks>
internal enum GrimoireOfflineTransitionDatabaseOutcome : byte
{

    /// <summary>This attempt won the compare-exchange and reread the exact winner.</summary>
    Terminalized = 1,

    /// <summary>The row already carried the exact terminal state this transition intends.</summary>
    AlreadyTerminal = 2,

    /// <summary>No row exists under the operation the journal names. Nothing was created.</summary>
    RowMissing = 3,

    /// <summary>The row exists but is not the launch this journal is bound to.</summary>
    RowConflicting = 4,

    /// <summary>The row moved since the journal bound it, so this is not the state it described.</summary>
    RevisionMismatch = 5,

    /// <summary>The row is already terminal under a different disposition, which is not ours to replace.</summary>
    TerminalConflict = 6,

    /// <summary>The journal cannot prove no effect was performed, so a failure may not be recorded.</summary>
    EffectNotProvenAbsent = 7,

    /// <summary>The compare-exchange reported success the reread does not support.</summary>
    WinnerUnproven = 8,

    /// <summary>The journal payload carries no launch binding this reconciliation could use.</summary>
    JournalUnusable = 9,

}

/// <summary>
/// The result of one terminal reconciliation, and the evidence a journal may publish from it.
/// </summary>
/// <remarks>
/// <see cref="TerminalWinnerDigest"/> is present exactly when the row is provably terminal under
/// this transition's own launch. It is what the journal's reconciliation suffix records, so it must
/// be recomputable: a process that crashed between the write and the publication comes back, finds
/// the same row, and derives the same value.
/// </remarks>
internal sealed record GrimoireOfflineTransitionDatabaseReconciliation(
    GrimoireOfflineTransitionDatabaseOutcome Outcome,
    CovenantDigest? TerminalWinnerDigest)
{

    /// <summary>Whether this outcome lets the transition proceed toward journal retirement.</summary>
    internal bool PermitsRetirement =>
        Outcome is GrimoireOfflineTransitionDatabaseOutcome.Terminalized
            or GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal;

}

/// <summary>
/// The one exact terminal write an offline transition makes to its own operation row.
/// </summary>
/// <remarks>
/// The row is reconciliation evidence rather than competing phase authority. By the time this runs
/// the journal has already decided what happened; all that is left is to record it once, against the
/// exact row the launch created, at the exact revision the journal bound itself to.
///
/// <para>Nothing here repairs. A missing row, a row belonging to another launch, and a row somebody
/// else already terminalized are three different situations, and the safe action in all three is the
/// same: write nothing, keep ordinary admission closed, and leave the journal in database
/// reconciliation for startup or an operator to resolve. Overwriting a conflicting row would destroy
/// the only record of the answer that is already there.</para>
///
/// <para>The compare-exchange passes no owner. A closed period releases the process-local ownership
/// this operation was launched under and renews no lease, so a lease owner is exactly the thing that
/// is no longer meaningful here. The revision the journal recorded, together with the immutable
/// launch fields revalidated first, is the scoping.</para>
/// </remarks>
internal sealed class GrimoireOfflineTransitionDatabaseReconciler(
    ILongRunningOperationStore operations,
    TimeProvider timeProvider)
{

    /// <summary>
    /// The terminal code a transition records when the journal proves it performed no effect.
    /// </summary>
    /// <remarks>
    /// Its own code rather than an ordinary retention one: a reader has to be able to tell a
    /// transition that never touched storage from a retention mutation that failed part way, because
    /// only the first one is safe to simply run again.
    /// </remarks>
    internal const string PreEffectFailureCode = "grimoire.offline_transition_not_applied";

    private const string TerminalWinnerDomain = "arcanum.grimoire.offline-transition.terminal-winner.v1";

    private readonly ILongRunningOperationStore _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal async Task<GrimoireOfflineTransitionDatabaseReconciliation> ReconcileAsync(
        IGrimoireOfflineTransitionPayload journal,
        GrimoireOfflineTransitionTerminalDisposition disposition,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(journal);

        if (journal.Binding is not { } binding
            || binding.OperationId == Guid.Empty
            || !binding.DatabaseOperationLaunchBindingDigest.IsValid
            || binding.ExpectedDatabaseOperationRevision == 0
            || binding.ExpectedDatabaseOperationRevision > long.MaxValue
            || !Enum.IsDefined(binding.Kind)
            || !Enum.IsDefined(disposition))
        {

            return Outcome(GrimoireOfflineTransitionDatabaseOutcome.JournalUnusable);

        }

        LongRunningOperation? current = await _operations
            .GetAsync(binding.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {

            return Outcome(GrimoireOfflineTransitionDatabaseOutcome.RowMissing);

        }

        if (!DescribesTheSameLaunch(current, binding))
        {

            return Outcome(GrimoireOfflineTransitionDatabaseOutcome.RowConflicting);

        }

        LongRunningOperationState intended = disposition switch
        {

            GrimoireOfflineTransitionTerminalDisposition.Completed =>
                LongRunningOperationState.Completed,

            _ => LongRunningOperationState.Failed,

        };

        string? terminalErrorCode = disposition switch
        {

            GrimoireOfflineTransitionTerminalDisposition.Completed => null,

            _ => PreEffectFailureCode,

        };

        // Classified ahead of the revision check on purpose. A row that already reached the intended
        // terminal has a revision past the one the journal recorded, and treating that as a mismatch
        // would turn the idempotent case — a crash between the write and the publication recording
        // it — into a permanent refusal.
        if (IsTerminal(current))
        {

            return IsExactly(current, intended, terminalErrorCode)
                ? new GrimoireOfflineTransitionDatabaseReconciliation(
                    GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal,
                    TerminalWinnerDigest(binding, current))
                : Outcome(GrimoireOfflineTransitionDatabaseOutcome.TerminalConflict);

        }

        if (current.Revision != (long)binding.ExpectedDatabaseOperationRevision)
        {

            return Outcome(GrimoireOfflineTransitionDatabaseOutcome.RevisionMismatch);

        }

        // The row cannot prove a pre-effect failure. An offline phase never rewrites the launch
        // checkpoint, so a row whose family was replaced is byte-identical to one that was never
        // touched; only the journal knows, and it knows by never having recorded a phase past the
        // one that precedes every effect.
        if (disposition is GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect
            && (journal.LastCompletedPhase != CovenantResetPhaseMachine.First
                || journal.InFlightPhase is not null))
        {

            return Outcome(GrimoireOfflineTransitionDatabaseOutcome.EffectNotProvenAbsent);

        }

        bool won = await _operations.TryTransitionAsync(
            binding.OperationId,
            (long)binding.ExpectedDatabaseOperationRevision,
            ownerId: null,
            intended,
            _timeProvider.GetUtcNow(),
            terminalErrorCode,
            cancellationToken).ConfigureAwait(false);

        // A successful compare-exchange is not the proof. Reread so the same rules cover our own
        // write and an indistinguishable competing winner.
        LongRunningOperation? winner = await _operations
            .GetAsync(binding.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (winner is null || !DescribesTheSameLaunch(winner, binding))
        {

            return Outcome(
                won
                    ? GrimoireOfflineTransitionDatabaseOutcome.WinnerUnproven
                    : GrimoireOfflineTransitionDatabaseOutcome.RowConflicting);

        }

        if (!IsExactly(winner, intended, terminalErrorCode))
        {

            return Outcome(
                IsTerminal(winner)
                    ? GrimoireOfflineTransitionDatabaseOutcome.TerminalConflict
                    : won
                        ? GrimoireOfflineTransitionDatabaseOutcome.WinnerUnproven
                        : GrimoireOfflineTransitionDatabaseOutcome.RevisionMismatch);

        }

        return new GrimoireOfflineTransitionDatabaseReconciliation(
            won
                ? GrimoireOfflineTransitionDatabaseOutcome.Terminalized
                : GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal,
            TerminalWinnerDigest(binding, winner));

    }

    private static bool IsTerminal(LongRunningOperation operation) =>
        operation.State is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed
            or LongRunningOperationState.Abandoned;

    private static bool IsExactly(
        LongRunningOperation operation,
        LongRunningOperationState state,
        string? terminalErrorCode) =>
        operation.State == state
        && string.Equals(operation.TerminalErrorCode, terminalErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// Whether this row is the launch the journal is bound to, in every immutable field.
    /// </summary>
    /// <remarks>
    /// The kind, the recovery policy, and the checkpoint version and reference are compared before
    /// the payload is decoded, so a row of an entirely different shape is refused without parsing
    /// anything it carries. The launch digest is the last check and the decisive one: it is a
    /// function of every field the launch recorded, so a row whose source and target were transposed
    /// cannot match a journal bound to the original.
    /// </remarks>
    private static bool DescribesTheSameLaunch(
        LongRunningOperation operation,
        GrimoireOfflineTransitionBinding binding) =>
        operation.Id == binding.OperationId
        && string.Equals(operation.Kind, KindOf(binding.Kind), StringComparison.Ordinal)
        && LongRunningOperationPolicyCatalog.IsRegistered(operation.Kind, operation.RecoveryPolicy)
        && operation.CheckpointVersion == LaunchVersionOf(binding.Kind)
        && string.Equals(
            operation.CheckpointReference,
            CovenantResetCheckpointInitiator.CheckpointReference(operation.Kind, operation.Id),
            StringComparison.Ordinal)
        && operation.CheckpointPayload is { } payload
        && Launch(binding.Kind, payload) is { IsSuccess: true } launch
        && launch.Value.Digest == binding.DatabaseOperationLaunchBindingDigest;

    private static Result<GrimoireOfflineTransitionLaunchBinding> Launch(
        GrimoireOfflineTransitionKind kind,
        ReadOnlySpan<byte> payload)
    {

        if (kind is GrimoireOfflineTransitionKind.CovenantReset)
        {

            Result<CovenantOfflineTransitionLaunchV4> decoded =
                CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(payload);

            return decoded.IsSuccess
                ? GrimoireOfflineTransitionLaunch.FromLaunch(decoded.Value)
                : Result<GrimoireOfflineTransitionLaunchBinding>.Failure(decoded.Error);

        }

        Result<DataRetentionFactoryTransitionLaunchV2> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(payload);

        return factory.IsSuccess
            ? GrimoireOfflineTransitionLaunch.FromLaunch(factory.Value)
            : Result<GrimoireOfflineTransitionLaunchBinding>.Failure(factory.Error);

    }

    private static string KindOf(GrimoireOfflineTransitionKind kind) =>
        kind is GrimoireOfflineTransitionKind.CovenantReset
            ? LongRunningOperationKinds.DataRetentionMutation
            : LongRunningOperationKinds.DataRetentionFactoryReset;

    private static int LaunchVersionOf(GrimoireOfflineTransitionKind kind) =>
        kind is GrimoireOfflineTransitionKind.CovenantReset
            ? CovenantOfflineTransitionLaunchV4.CurrentVersion
            : DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

    /// <summary>
    /// The content-free digest of one terminal winner.
    /// </summary>
    /// <remarks>
    /// Derived from the launch binding and the exact row that won, and from nothing that only the
    /// writing process knows, so the process that comes back after a crash recomputes the identical
    /// value from the row it rereads.
    /// </remarks>
    private static CovenantDigest TerminalWinnerDigest(
        GrimoireOfflineTransitionBinding binding,
        LongRunningOperation winner)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(TerminalWinnerDomain));

        hash.AppendData([0]);

        hash.AppendData(binding.DatabaseOperationLaunchBindingDigest.Bytes);

        hash.AppendData(winner.Id.ToByteArray(bigEndian: true));

        hash.AppendData([(byte)winner.State]);

        byte[] encoded = Encoding.UTF8.GetBytes(winner.TerminalErrorCode ?? string.Empty);

        byte[] length = new byte[sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)encoded.Length));

        hash.AppendData(length);

        hash.AppendData(encoded);

        byte[] revision = new byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(revision, winner.Revision);

        hash.AppendData(revision);

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static GrimoireOfflineTransitionDatabaseReconciliation Outcome(
        GrimoireOfflineTransitionDatabaseOutcome outcome) =>
        new(outcome, TerminalWinnerDigest: null);

}
