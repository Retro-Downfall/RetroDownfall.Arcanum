namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The exact thing one curation change is about.
/// </summary>
/// <remarks>
/// A scoped key and lane, plus the two epochs of the key. It is deliberately not an entry identity:
/// masking a Global key inside a Campaign is exactly the case where that Campaign holds no entry, no
/// head, and no version for the key, and a subject that required one could not name it.
///
/// <para><see cref="KeyDependencyEpoch"/> is the epoch the change was prepared against. It advances on
/// every head change for the key, the request digest binds it, and the receipt records it, so a change
/// prepared before some other write to the key is refused as stale.</para>
///
/// <para><see cref="KeyBindingEpoch"/> is the epoch the curation itself is recorded under. It is fixed
/// for the life of the key's epoch row, so a pin or a mask keeps applying across ordinary writes, while
/// a key that is reclaimed and later re-created is a different key wearing an old name and takes none
/// of its predecessor's curation. Null means the kernel resolves it under the write lock; a caller
/// that read it states it, and the kernel refuses when the two disagree. A receipt's subject always
/// carries the resolved value.</para>
/// </remarks>
public sealed record CovenantCurationSubject(
    CovenantOperationScope Scope,
    CovenantKey NormalizedKey,
    CovenantLane Lane,
    long KeyDependencyEpoch,
    long? KeyBindingEpoch = null);

/// <summary>
/// What one subject's curation head currently says.
/// </summary>
/// <remarks>
/// A subject with no curation row at all reports <see cref="None"/> rather than an absent value. Every
/// reader wants the same answer for "never curated" and "curated back to nothing", and a nullable here
/// would make each of them decide that separately.
/// </remarks>
public sealed record CovenantCurationState(bool IsPinned, bool IsMasked, long Revision)
{

    /// <summary>The state of a subject nobody has curated.</summary>
    public static CovenantCurationState None { get; } = new(false, false, 0);

}

/// <summary>
/// One validated curation change, carrying everything the kernel needs and nothing it re-derives.
/// </summary>
/// <remarks>
/// Validation happens in the constructor so an invalid intent cannot reach a transaction at all. There
/// is no agent-authored counterpart: curation is what an operator does <i>about</i> the agent, and a
/// second factory that could build one would make "the model pinned its own proposal" a rule somebody
/// has to remember rather than a shape nobody can express.
/// </remarks>
public sealed class CovenantCurationIntent
{

    public CovenantCurationIntent(
        Guid mutationId,
        CovenantCurationKind kind,
        CovenantCurationSubject subject,
        long expectedRevision,
        CovenantMutationAuthorization authorization)
    {

        ArgumentNullException.ThrowIfNull(subject);

        ArgumentNullException.ThrowIfNull(authorization);

        MutationId = CovenantValidation.RequireNonEmpty(mutationId, nameof(mutationId));

        Kind = kind is >= CovenantCurationKind.Pin and <= CovenantCurationKind.Unmask
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind));

        if (subject.KeyDependencyEpoch < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(subject), "A key dependency epoch is never negative.");

        }

        if (subject.KeyBindingEpoch is < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(subject), "A key binding epoch is never negative.");

        }

        // A mask names a Campaign and the Confirmed lane. A Global mask has no broader scope to fall
        // back from, and the Proposed lane is review-only beside effective Confirmed content, so
        // masking it would change nothing an operator could observe. Refused here as well as by the
        // table, because a CHECK complains inside the commit transaction and this complains before one
        // is opened.
        if (kind is CovenantCurationKind.Mask or CovenantCurationKind.Unmask
            && (subject.Scope.Kind != CovenantScope.Campaign || subject.Lane != CovenantLane.Confirmed))
        {

            throw new ArgumentException(
                "A scope mask names one Campaign's Confirmed lane, because that is the only place a broader scope can be falling through.",
                nameof(subject));

        }

        Subject = subject;

        if (expectedRevision < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        }

        ExpectedRevision = expectedRevision;

        // Curation is operator authority and nothing else. Ward evidence belongs to an approved agent
        // retirement, and a curation intent carrying it would be claiming a lineage it cannot have.
        if (authorization.Mode != CovenantAuthorizationMode.ApiMasterKey
            || authorization.WardReceiptDigest is not null)
        {

            throw new ArgumentException(
                "A curation change is authorized by operator authority alone.",
                nameof(authorization));

        }

        if (authorization.PreflightBodyDigest is null)
        {

            throw new ArgumentException(
                "A curation change commits against the preflight it was measured by.",
                nameof(authorization));

        }

        Authorization = authorization;

    }

    public Guid MutationId { get; }

    public CovenantCurationKind Kind { get; }

    public CovenantCurationSubject Subject { get; }

    /// <summary>The curation revision this change compares and swaps against. Zero opens a chain.</summary>
    public long ExpectedRevision { get; }

    public CovenantMutationAuthorization Authorization { get; }

    /// <summary>The state this change would leave, applied to <paramref name="current"/>.</summary>
    /// <remarks>
    /// Pure, and shared by the preflight that shows an operator the effect and the kernel that writes
    /// it. Two implementations of one transition is how a preview and a commit come to disagree.
    /// </remarks>
    public CovenantCurationState Project(CovenantCurationState current)
    {

        ArgumentNullException.ThrowIfNull(current);

        return Kind switch
        {
            CovenantCurationKind.Pin => current with { IsPinned = true },
            CovenantCurationKind.Unpin => current with { IsPinned = false },
            CovenantCurationKind.Mask => current with { IsMasked = true },
            _ => current with { IsMasked = false },
        };

    }

}

/// <summary>
/// One curation change plus the generation facts it commits against.
/// </summary>
/// <remarks>
/// One change rather than a batch, because curation has no staging path: an operator asks for exactly
/// one thing and a turn can ask for none. The generation and reclamation epoch travel with it for the
/// same reason a mutation batch carries them — they are read inside the write transaction and compared
/// against what the preflight measured, so a change prepared against one installation cannot land on
/// another.
/// </remarks>
public sealed class CovenantCurationCommit
{

    public CovenantCurationCommit(
        Guid datasetGeneration,
        long expectedKeyReclamationEpoch,
        DateTimeOffset committedAtUtc,
        CovenantCurationIntent intent)
    {

        ArgumentNullException.ThrowIfNull(intent);

        DatasetGeneration = CovenantValidation.RequireNonEmpty(datasetGeneration, nameof(datasetGeneration));

        ExpectedKeyReclamationEpoch = CovenantValidation.RequirePositive(
            expectedKeyReclamationEpoch,
            nameof(expectedKeyReclamationEpoch));

        CommittedAtUtc = committedAtUtc;

        Intent = intent;

    }

    public Guid DatasetGeneration { get; }

    public long ExpectedKeyReclamationEpoch { get; }

    public DateTimeOffset CommittedAtUtc { get; }

    public CovenantCurationIntent Intent { get; }

}

/// <summary>
/// The durable outcome of one curation change.
/// </summary>
/// <remarks>
/// A <see cref="CovenantMutationOutcome.NoChange"/> receipt is written and returned exactly like an
/// applied one. Recording only the changes that changed something would make a replay of a deliberate
/// no-op — pinning what is already pinned — indistinguishable from a request that never arrived.
/// </remarks>
public sealed record CovenantCurationReceipt(
    Guid MutationId,
    CovenantMutationOutcome Outcome,
    CovenantCurationKind Kind,
    CovenantCurationSubject Subject,
    CovenantCurationState ResultingState,
    Guid? ResultingVersionId,
    long? ResultingRevision,
    CovenantDigest RequestIdempotencyDigest,
    CovenantDigest FinalMutationDigest,
    CovenantDigest ResponseReceiptDigest,
    bool Replayed);
