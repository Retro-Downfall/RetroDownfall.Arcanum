using System.Collections.Immutable;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// How one acquisition relates to the claim it returned.
/// </summary>
/// <remarks>
/// A caller must be able to tell "you own this turn" from "here is the answer it already produced"
/// without inspecting state codes. A replayed acquisition that was mistaken for a fresh one would run
/// a second provider turn for a request that already has a durable answer, and bill for it.
/// </remarks>
public enum SessionTurnClaimDisposition
{

    /// <summary>
    /// This request minted the claim. Nothing has run for it yet.
    /// </summary>
    Created = 1,

    /// <summary>
    /// The boot that owns the claim re-entered it — a retry of a request still in flight here.
    /// </summary>
    Resumed = 2,

    /// <summary>
    /// A claim left live by an earlier boot was taken over after full revalidation.
    /// </summary>
    Adopted = 3,

    /// <summary>
    /// The claim is terminal. The recorded outcome is the answer; nothing may execute.
    /// </summary>
    Replayed = 4,

}

/// <summary>
/// The stable client facts one turn claim is authenticated against.
/// </summary>
/// <remarks>
/// The client turn ID is the caller's Idempotency-Key, scoped by the installation that accepted it,
/// because two installations may legitimately mint the same UUID. Everything else on this record is
/// evidence a retry is checked against rather than input the turn consumes.
///
/// <para><see cref="RequestDigest"/> and <see cref="DependencyDigest"/> are deliberately separate.
/// The request digest is the stable client request — a terminal replay validates only that one, so a
/// provider, model, Prompt, Spell, attachment, path, or tool policy that moved after the answer was
/// recorded cannot turn a durable answer into a conflict. The dependency digest freezes what the turn
/// was <em>planned</em> under, so resuming or adopting nonterminal work whose plan changed fails
/// closed instead of continuing against a different world.</para>
///
/// <para>The three revision fields are the frozen pre-request evidence. They are recorded once and
/// never rewritten, which is what lets assistant begin prove that history did not move underneath the
/// turn between admission and the first durable write.</para>
/// </remarks>
public sealed record SessionTurnRequestIdentity(
    Guid OriginInstallationId,
    long OriginRestoreEpoch,
    Guid ClientTurnId,
    Guid SessionId,
    SessionTurnSurface Surface,
    CovenantDigest RequestDigest,
    CovenantDigest DependencyDigest,
    DateTimeOffset? PreRequestHistoryWatermarkUtc,
    long PreRequestHistoryRevision,
    long InputSensitivityRevision);

/// <summary>
/// The durable answer a terminal claim replays, with no response body behind it.
/// </summary>
/// <remarks>
/// A claim records what a retry must be told, not what the turn said. Caching the assistant's bytes
/// here would make the claim a second copy of the transcript that retention, erasure, and sensitivity
/// labelling would each have to learn about; the reply itself already lives in its Entry.
///
/// <para>Exactly one representation is valid per terminal state, and the schema enforces it.
/// <see cref="SessionTurnClaimState.Discarded"/> and
/// <see cref="SessionTurnClaimState.RestoredInterrupted"/> reconstruct a typed error envelope from
/// these immutable public fields. <see cref="SessionTurnClaimState.Committed"/> replays through its
/// finalization guard and <see cref="SessionTurnClaimState.Erased"/> through its erasure receipt, so
/// neither may also carry a stored error a replay could return <em>instead</em> of the real one.</para>
/// </remarks>
public sealed record SessionTurnClaimOutcome
{

    private SessionTurnClaimOutcome(
        SessionTurnClaimState state,
        string? terminalErrorCode,
        int? terminalHttpStatus,
        ImmutableArray<byte> terminalParameterBytes)
    {

        State = state;

        TerminalErrorCode = terminalErrorCode;

        TerminalHttpStatus = terminalHttpStatus;

        TerminalParameterBytes = terminalParameterBytes;

        TerminalParameterDigest = terminalParameterBytes.IsDefault
            ? null
            : new CovenantDigest(SHA256.HashData([.. terminalParameterBytes]));

    }

    /// <summary>
    /// The maximum stored terminal parameter payload. The schema refuses anything larger.
    /// </summary>
    public const int MaxTerminalParameterBytes = 4096;

    public SessionTurnClaimState State { get; }

    public string? TerminalErrorCode { get; }

    public int? TerminalHttpStatus { get; }

    /// <summary>
    /// The bounded, content-free parameters the typed error envelope is rebuilt from. Default when
    /// the outcome carries no error at all.
    /// </summary>
    public ImmutableArray<byte> TerminalParameterBytes { get; }

    /// <summary>
    /// Binds the stored parameters to the row, so a replay can prove the bytes it read are the bytes
    /// the terminalizing transaction wrote.
    /// </summary>
    public CovenantDigest? TerminalParameterDigest { get; }

    /// <summary>
    /// The turn produced its answer and the assistant Entry is authoritative.
    /// </summary>
    public static SessionTurnClaimOutcome Committed() =>
        new(SessionTurnClaimState.Committed, null, null, default);

    /// <summary>
    /// The turn failed or was cancelled and left no authoritative reply.
    /// </summary>
    public static SessionTurnClaimOutcome Discarded(
        string errorCode,
        int httpStatus,
        ImmutableArray<byte> parameterBytes) =>
        new(SessionTurnClaimState.Discarded, Require(errorCode), RequireStatus(httpStatus), RequireBytes(parameterBytes));

    /// <summary>
    /// Recovery found the turn interrupted with no executable lease or checkpoint authority left.
    /// </summary>
    public static SessionTurnClaimOutcome RestoredInterrupted(
        string errorCode,
        int httpStatus,
        ImmutableArray<byte> parameterBytes) =>
        new(
            SessionTurnClaimState.RestoredInterrupted,
            Require(errorCode),
            RequireStatus(httpStatus),
            RequireBytes(parameterBytes));

    /// <summary>
    /// A committed claim became an erasure tombstone. The only edge out of
    /// <see cref="SessionTurnClaimState.Committed"/>, and never back.
    /// </summary>
    public static SessionTurnClaimOutcome Erased() =>
        new(SessionTurnClaimState.Erased, null, null, default);

    private static string Require(string errorCode) =>
        string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128
            ? throw new ArgumentException(
                "A terminal claim error code must be between 1 and 128 characters.",
                nameof(errorCode))
            : errorCode;

    private static int RequireStatus(int httpStatus) =>
        httpStatus is >= 100 and <= 599
            ? httpStatus
            : throw new ArgumentOutOfRangeException(
                nameof(httpStatus),
                "A terminal claim HTTP status must be a real status code.");

    private static ImmutableArray<byte> RequireBytes(ImmutableArray<byte> parameterBytes) =>
        parameterBytes is { IsDefault: false, Length: <= MaxTerminalParameterBytes }
            ? parameterBytes
            : throw new ArgumentException(
                $"Terminal claim parameters are required and cannot exceed {MaxTerminalParameterBytes} bytes.",
                nameof(parameterBytes));

}

/// <summary>
/// One durable turn claim, projected exactly as it is stored.
/// </summary>
/// <remarks>
/// <see cref="InputSensitivityRevision"/> and <see cref="ExpectedCurrentSensitivityRevision"/> start
/// equal and are allowed to diverge only afterward. The input revision is the frozen evidence the
/// turn was planned against; the expected-current revision is the moving target a guarded maintenance
/// step advances by compare-and-swap. Collapsing them into one value would let a maintenance step
/// overwrite the very evidence assistant begin has to compare against.
/// </remarks>
public sealed record SessionTurnClaim(
    Guid ClaimId,
    Guid OriginInstallationId,
    long OriginRestoreEpoch,
    Guid ClientTurnId,
    Guid SessionId,
    SessionTurnSurface Surface,
    CovenantDigest RequestDigest,
    CovenantDigest DependencyDigest,
    SessionTurnClaimState State,
    DateTimeOffset? PreRequestHistoryWatermarkUtc,
    long PreRequestHistoryRevision,
    long InputSensitivityRevision,
    long ExpectedCurrentSensitivityRevision,
    Guid FinalizationReservationId,
    Guid? UserEntryId,
    Guid? AssistantEntryId,
    Guid? OwnerBootId,
    long CheckpointRevision,
    int CompletedStepMask,
    SessionTurnClaimOutcome? Outcome,
    DateTimeOffset CreatedAtUtc)
{

    /// <summary>
    /// Whether the claim has reached a durable answer. A terminal claim retains no executor authority.
    /// </summary>
    public bool IsTerminal =>
        State is not (SessionTurnClaimState.PendingMaintenance or SessionTurnClaimState.Begun);

}

/// <summary>
/// Executable authority over one live claim, or the content-free proof that there is none.
/// </summary>
/// <remarks>
/// <see cref="ExecutorId"/> and the deadline are present only while the claim is live, because the
/// schema refuses to keep either on a terminal one: leaving a lease on a finished claim would let an
/// expired owner renew it and resume a turn that already has an answer.
///
/// <para><see cref="FutureAssistantEntryId"/> is minted once, at acquisition, and stored on the
/// reserved finalization capacity rather than invented per attempt. Assistant begin inserts the
/// placeholder with exactly this ID, so a retry, resume, or adoption of the same claim cannot produce
/// a second assistant Entry for one turn.</para>
/// </remarks>
public sealed record SessionTurnClaimLease(
    SessionTurnClaim Claim,
    SessionTurnClaimDisposition Disposition,
    Guid FutureAssistantEntryId,
    Guid? ExecutorId,
    Guid? OwnerBootId,
    DateTimeOffset? LeaseDeadlineUtc)
{

    /// <summary>
    /// Whether the caller may run the turn. False for a replay, which already has its answer.
    /// </summary>
    public bool IsExecutable => Disposition is not SessionTurnClaimDisposition.Replayed;

}

/// <summary>
/// The durable idempotency identity of every public session-backed turn.
/// </summary>
/// <remarks>
/// This port exists so that a buffered, streaming, disconnected, or transport-level retry lands on
/// the claim the first request created instead of starting a second turn. It deliberately caches no
/// response body: the claim records the state a retry has to be told about, and the reply itself
/// stays in its Entry under one retention and erasure story.
///
/// <para>Acquisition happens before any provider disclosure. A per-Session or installation-wide
/// capacity refusal therefore creates no claim, no reservation, no placeholder, and no provider
/// effect — the request is refused while it is still free to refuse.</para>
///
/// <para>This port does not consume the finalization capacity it reserves. The transaction that
/// inserts the assistant placeholder consumes it, in the same transaction as the row it guards, so a
/// guard can never be spent without the Entry it protects. Terminalizing a claim that never began
/// releases the reservation here, because in that case no such transaction will ever run.</para>
/// </remarks>
public interface ISessionTurnClaimCoordinator
{

    /// <summary>
    /// Acquires, resumes, adopts, or replays the exact claim for one client turn identity.
    /// </summary>
    /// <remarks>
    /// A same-identity request with a different request digest is
    /// <c>Security.IdempotencyConflict</c> and claims nothing at all. A different identity while
    /// another claim is live on the Session is <c>Hub.SessionTurnBusy</c>, which is what stops a
    /// competing client from overtaking maintenance or Entry order.
    /// </remarks>
    ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(
        SessionTurnRequestIdentity request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that the turn's Entries now exist, moving the claim out of pending maintenance.
    /// </summary>
    /// <remarks>
    /// The receipt must name the reserved future assistant Entry identity. Accepting any other ID
    /// would bind the claim to a placeholder its finalization guard does not cover, which is the
    /// state where one turn can produce two assistant Entries.
    /// </remarks>
    ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(
        SessionTurnClaimLease lease,
        AssistantReplyBeginReceipt begin,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the claim's one durable answer.
    /// </summary>
    /// <remarks>
    /// Terminalization is one-shot. Repeating the identical outcome returns the recorded claim, but a
    /// different terminal answer for a claim that already has one is <c>Covenant.LifecycleConflict</c>
    /// — a claim whose answer could be rewritten is not an idempotency record.
    /// </remarks>
    ValueTask<Result<SessionTurnClaim>> CompleteAsync(
        SessionTurnClaimLease lease,
        SessionTurnClaimOutcome outcome,
        CancellationToken cancellationToken);

}
