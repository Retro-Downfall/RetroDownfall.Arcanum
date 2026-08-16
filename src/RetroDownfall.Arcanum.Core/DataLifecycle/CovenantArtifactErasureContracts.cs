using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// Which capability an erasure is running under.
/// </summary>
/// <remarks>
/// Closed and numbered because it selects the connection-local SQL authorization the effect borrows,
/// and a value that could be defaulted would select one by accident.
/// </remarks>
public enum CovenantArtifactErasureAuthorityKind : byte
{

    /// <summary>Retention purge: a live write lease plus the operator's purge authority.</summary>
    Ordinary = 1,

    /// <summary>Reinitialize, reset, or healthy-catalog factory erasure: a live exclusive lease.</summary>
    Exclusive = 2,

}

/// <summary>
/// Why an erasure stopped short of removing everything it examined.
/// </summary>
/// <remarks>
/// Content-free and closed. The code travels into progress records, operator surfaces, and durable
/// evidence, so it names a class of obstruction and never the artifact, the path, or the mismatch it
/// found. <see cref="None"/> is zero because "nothing blocked this" is genuinely the absence of a
/// blocker rather than a fourth kind of one.
/// </remarks>
public enum CovenantErasureBlocker : byte
{

    None = 0,

    /// <summary>
    /// Ownership, identity, or content did not match, so the artifact was left exactly as found.
    /// </summary>
    ManualOwnershipMismatch = 1,

    /// <summary>The authority or its lease stopped being current before the effect.</summary>
    AuthorityStale = 2,

    /// <summary>Durable state disagreed with itself and no safe deletion could be derived.</summary>
    IntegrityFailure = 3,

    /// <summary>The database or the filesystem could not answer, so absence is not provable.</summary>
    StorageUnavailable = 4,

}

/// <summary>
/// The content-free outcome of one erasure step.
/// </summary>
/// <remarks>
/// Counts are checked <c>u64</c>: a purge that silently wrapped a counter would understate what it
/// touched in exactly the report an operator uses to decide whether erasure is complete.
/// </remarks>
public sealed record CovenantArtifactErasureProgress(
    ulong ExaminedCount,
    ulong ErasedCount,
    ulong PreservedEvidenceCount,
    CovenantErasureBlocker Blocker)
{

    /// <summary>A step that examined nothing and was not blocked.</summary>
    public static CovenantArtifactErasureProgress Empty { get; } =
        new(0, 0, 0, CovenantErasureBlocker.None);

    /// <summary>Whether this step reported an obstruction.</summary>
    public bool IsBlocked => Blocker != CovenantErasureBlocker.None;

    /// <summary>
    /// Folds another step into this one, keeping the first blocker observed.
    /// </summary>
    /// <remarks>
    /// The first blocker wins rather than the last: a coordinator that overwrote an earlier manual
    /// mismatch with a later clean step would report a completed erasure over a file it never removed.
    /// </remarks>
    public CovenantArtifactErasureProgress Add(CovenantArtifactErasureProgress other)
    {

        ArgumentNullException.ThrowIfNull(other);

        checked
        {

            return new CovenantArtifactErasureProgress(
                ExaminedCount + other.ExaminedCount,
                ErasedCount + other.ErasedCount,
                PreservedEvidenceCount + other.PreservedEvidenceCount,
                IsBlocked ? Blocker : other.Blocker);

        }

    }

}

/// <summary>
/// One database-owned artifact an erasure page intends to remove.
/// </summary>
/// <remarks>
/// The expected label, digest, and revision travel with the item so the kernel can reread the current
/// row and refuse a mismatch. They are expectations, never authority: the kernel derives the owner
/// scope from the rows it reads inside its own transaction and never from these values (§10.17).
/// </remarks>
public sealed record CovenantProtectedArtifactErasureItem
{

    public CovenantProtectedArtifactErasureItem(
        Guid artifactId,
        SensitiveArtifactKind kind,
        Guid? sessionId,
        Guid sensitivityLabelId,
        ArtifactSensitivityLabel expectedLabel,
        CovenantDigest expectedArtifactDigest,
        ulong expectedRevision)
    {

        ArgumentNullException.ThrowIfNull(expectedLabel);

        ArtifactId = CovenantValidation.RequireNonEmpty(artifactId, nameof(artifactId));

        Kind = CovenantSensitiveArtifactPurgePolicy.IsCovered(kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind));

        SessionId = sessionId == Guid.Empty ? throw new ArgumentException(
            "An erasure item cannot name the empty Session identity.",
            nameof(sessionId)) : sessionId;

        SensitivityLabelId = CovenantValidation.RequireNonEmpty(sensitivityLabelId, nameof(sensitivityLabelId));

        ExpectedArtifactDigest = CovenantValidation.RequireDigest(expectedArtifactDigest, nameof(expectedArtifactDigest));

        ExpectedRevision = expectedRevision;

        // The label is the authority on what this artifact is. An item whose own fields disagree with
        // the label it carries describes two different artifacts, and there is no safe way to pick.
        if (expectedLabel.LabelId != sensitivityLabelId
            || expectedLabel.ArtifactId != artifactId
            || expectedLabel.ArtifactKind != kind
            || expectedLabel.SessionId != sessionId
            || expectedLabel.ArtifactRevision != expectedRevision
            || expectedLabel.ArtifactContentDigest != expectedArtifactDigest)
        {

            throw new ArgumentException(
                "The erasure item does not describe the same artifact as the label it carries.",
                nameof(expectedLabel));

        }

        ExpectedLabel = expectedLabel;

    }

    public Guid ArtifactId { get; }

    public SensitiveArtifactKind Kind { get; }

    public Guid? SessionId { get; }

    public Guid SensitivityLabelId { get; }

    public ArtifactSensitivityLabel ExpectedLabel { get; }

    public CovenantDigest ExpectedArtifactDigest { get; }

    public ulong ExpectedRevision { get; }

}

/// <summary>
/// A bounded, immutable batch of database-owned artifacts to erase in one dataset generation.
/// </summary>
/// <remarks>
/// The generation is part of the page rather than the call so the kernel can refuse a batch computed
/// against a dataset that has since been replaced. The item list is deep-copied on construction and
/// exposed only as a read-only view, because a caller that could mutate the page after validation
/// would be handing the kernel one set of artifacts and the checks another.
/// </remarks>
public sealed class CovenantProtectedArtifactErasurePage
{

    /// <summary>The largest number of artifacts one page may carry.</summary>
    public const int MaxItems = 256;

    private readonly CovenantProtectedArtifactErasureItem[] _items;

    public CovenantProtectedArtifactErasurePage(
        Guid expectedDatasetGeneration,
        IReadOnlyList<CovenantProtectedArtifactErasureItem> items)
    {

        ArgumentNullException.ThrowIfNull(items);

        ExpectedDatasetGeneration = CovenantValidation.RequireNonEmpty(
            expectedDatasetGeneration,
            nameof(expectedDatasetGeneration));

        if (items.Count is 0 or > MaxItems)
        {

            throw new ArgumentOutOfRangeException(
                nameof(items),
                $"An erasure page carries between 1 and {MaxItems} artifacts.");

        }

        CovenantProtectedArtifactErasureItem[] copied = new CovenantProtectedArtifactErasureItem[items.Count];

        HashSet<Guid> artifactIds = new(items.Count);

        HashSet<Guid> labelIds = new(items.Count);

        for (int index = 0; index < items.Count; index++)
        {

            CovenantProtectedArtifactErasureItem item = items[index]
                ?? throw new ArgumentException("An erasure page cannot carry a null artifact.", nameof(items));

            // Two items for one artifact would delete it twice: the second delete would find the row
            // already gone and could not tell that from a row somebody else removed.
            if (!artifactIds.Add(item.ArtifactId) || !labelIds.Add(item.SensitivityLabelId))
            {

                throw new ArgumentException(
                    "An erasure page cannot name the same artifact or label twice.",
                    nameof(items));

            }

            // Delegation is enforced by the type rather than by the kernel that would otherwise have
            // to remember it. A managed workspace file's deletion is a durable, crash-recoverable
            // operation with its own work item, and a database page has nowhere to put one.
            if (CovenantSensitiveArtifactPurgePolicy.Resolve(item.Kind).Value.Executor
                != CovenantArtifactPurgeExecutor.DatabaseTransaction)
            {

                throw new ArgumentException(
                    "A managed workspace file is erased through the managed-file kernel, never through a database page.",
                    nameof(items));

            }

            copied[index] = item;

        }

        _items = copied;

    }

    /// <summary>The dataset generation this page was computed against.</summary>
    public Guid ExpectedDatasetGeneration { get; }

    /// <summary>The artifacts, in the order the caller supplied them.</summary>
    public IReadOnlyList<CovenantProtectedArtifactErasureItem> Items => _items;

}

/// <summary>
/// One managed workspace file to erase, named only by durable identities.
/// </summary>
/// <remarks>
/// Deliberately carries no path, root identity, path revision, parent segment, parent physical
/// identity, leaf, ownership evidence, content hash, or owner scope. Every one of those is copied
/// from the producer's own durable row inside the kernel: a caller-supplied location is exactly how a
/// confused or hostile request points a deleter at a file Arcanum never created (§10.17).
/// </remarks>
public sealed record CovenantManagedFileErasureRequest
{

    public CovenantManagedFileErasureRequest(
        Guid workItemId,
        Guid operationId,
        Guid sourceManagedWriteOperationId,
        Guid artifactId,
        Guid sensitivityLabelId,
        ulong expectedSourceWriteRevision)
    {

        WorkItemId = CovenantValidation.RequireNonEmpty(workItemId, nameof(workItemId));

        OperationId = CovenantValidation.RequireNonEmpty(operationId, nameof(operationId));

        SourceManagedWriteOperationId = CovenantValidation.RequireNonEmpty(
            sourceManagedWriteOperationId,
            nameof(sourceManagedWriteOperationId));

        ArtifactId = CovenantValidation.RequireNonEmpty(artifactId, nameof(artifactId));

        SensitivityLabelId = CovenantValidation.RequireNonEmpty(sensitivityLabelId, nameof(sensitivityLabelId));

        ExpectedSourceWriteRevision = expectedSourceWriteRevision;

    }

    public Guid WorkItemId { get; }

    public Guid OperationId { get; }

    public Guid SourceManagedWriteOperationId { get; }

    public Guid ArtifactId { get; }

    public Guid SensitivityLabelId { get; }

    public ulong ExpectedSourceWriteRevision { get; }

}

/// <summary>
/// The borrowed capability an erasure kernel performs one effect under.
/// </summary>
/// <remarks>
/// A closed two-arm union, nonserializable, and a borrower only. It exposes revalidation and the
/// lease's coverage and nothing else: it cannot acquire, complete, dispose, serialize, or persist a
/// lease, so a kernel handed one can never take ownership of the caller's lifecycle or reopen
/// admission on the caller's behalf.
///
/// <para>The two arms exist because the two callers are genuinely different. Retention purge runs
/// under an ordinary write lease plus the operator's <see cref="CovenantAuthorityRequirement"/>, and
/// revalidates both before every effect. Reinitialize, reset, and factory erasure run under a global
/// exclusive lease that has already closed admission, and there is no operator context to compare
/// because there is no concurrent traffic left to protect from (§10.17).</para>
/// </remarks>
public sealed class CovenantArtifactErasureAuthority
{

    private readonly ICovenantOperationLease _lease;

    private readonly IOperatorAuthorityContextIssuer? _issuer;

    private readonly OperatorAuthorityContext? _operatorContext;

    private CovenantArtifactErasureAuthority(
        CovenantArtifactErasureAuthorityKind kind,
        ICovenantOperationLease lease,
        IOperatorAuthorityContextIssuer? issuer,
        OperatorAuthorityContext? operatorContext,
        CovenantExclusiveOperation? exclusiveOperation)
    {

        Kind = kind;

        _lease = lease;

        _issuer = issuer;

        _operatorContext = operatorContext;

        ExclusiveOperation = exclusiveOperation;

    }

    /// <summary>Which arm this authority is.</summary>
    public CovenantArtifactErasureAuthorityKind Kind { get; }

    /// <summary>The exclusive operation, present only on the exclusive arm.</summary>
    public CovenantExclusiveOperation? ExclusiveOperation { get; }

    /// <summary>The borrowed lease's captured generations and coverage.</summary>
    public CovenantOperationLeaseSnapshot Snapshot => _lease.Snapshot;

    /// <summary>The borrowed lease's revocation signal.</summary>
    public CancellationToken Revocation => _lease.Revocation;

    /// <summary>
    /// The three exclusive operations that may erase protected artifacts.
    /// </summary>
    public static IReadOnlyList<CovenantExclusiveOperation> ErasureOperations { get; } =
    [
        CovenantExclusiveOperation.CovenantFamilyReinitialize,
        CovenantExclusiveOperation.CovenantReset,
        CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
    ];

    /// <summary>
    /// Borrows a live retention-purge capability.
    /// </summary>
    /// <remarks>
    /// The operator context must have been issued for
    /// <see cref="CovenantAuthorityRequirement.SensitivityRetentionPurge"/> and must agree with the
    /// lease about the authority epoch. That is the only fact the two values actually share: the
    /// snapshot exposes no installation identity and no master-key version, and aliasing a different
    /// generation field to manufacture a second comparison would be a check that proves nothing.
    /// </remarks>
    public static Result<CovenantArtifactErasureAuthority> ForOrdinary(
        CovenantWriteLease lease,
        OperatorAuthorityContext operatorContext,
        IOperatorAuthorityContextIssuer issuer)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(operatorContext);

        ArgumentNullException.ThrowIfNull(issuer);

        if (!operatorContext.Satisfies(CovenantAuthorityRequirement.SensitivityRetentionPurge))
        {

            return Result<CovenantArtifactErasureAuthority>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "Protected-artifact erasure requires operator authority issued for a sensitivity retention purge."));

        }

        if (lease.Snapshot.AuthorityEpoch != operatorContext.AuthorityEpoch)
        {

            return Result<CovenantArtifactErasureAuthority>.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The write lease and the operator authority were issued under different authority epochs."));

        }

        return Result<CovenantArtifactErasureAuthority>.Success(
            new CovenantArtifactErasureAuthority(
                CovenantArtifactErasureAuthorityKind.Ordinary,
                lease,
                issuer,
                operatorContext,
                exclusiveOperation: null));

    }

    /// <summary>
    /// Borrows a live exclusive erasure capability.
    /// </summary>
    /// <remarks>
    /// Only the three erasure operations are accepted, and the lease must already be registered under
    /// exactly that recovery owner. A path mutation, Campaign delete, protected transfer, schema
    /// repair, or restore lease has closed admission for a completely different reason, and letting
    /// one of them erase artifacts would mean an operation an operator authorized as a repair could
    /// delete their memory.
    /// </remarks>
    public static Result<CovenantArtifactErasureAuthority> ForExclusive(
        ICovenantExclusiveOperationLease lease,
        CovenantExclusiveOperation operation)
    {

        ArgumentNullException.ThrowIfNull(lease);

        if (!ErasureOperations.Contains(operation))
        {

            return Result<CovenantArtifactErasureAuthority>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "Only reinitialize, reset, and healthy-catalog factory erasure may erase protected artifacts."));

        }

        if (lease.Snapshot.RecoveryOwner is not { } owner || !owner.IsValid || owner.Operation != operation)
        {

            return Result<CovenantArtifactErasureAuthority>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "The exclusive lease was not registered under the erasure operation it is being borrowed for."));

        }

        return Result<CovenantArtifactErasureAuthority>.Success(
            new CovenantArtifactErasureAuthority(
                CovenantArtifactErasureAuthorityKind.Exclusive,
                lease,
                issuer: null,
                operatorContext: null,
                exclusiveOperation: operation));

    }

    /// <summary>
    /// Reconfirms every fact this authority borrowed, before scope is read or an effect is applied.
    /// </summary>
    public async ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
    {

        if (_issuer is not null && _operatorContext is not null)
        {

            Result operatorStillCurrent = _issuer.Revalidate(_operatorContext);

            if (operatorStillCurrent.IsFailure)
            {

                return operatorStillCurrent;

            }

        }

        return await _lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Whether an owner scope derived from current rows falls inside this authority's coverage.
    /// </summary>
    /// <remarks>
    /// An installation-coverage lease covers every owner. A Global-only lease covers Global owners
    /// alone, because a Campaign's rows are not "part of" the Global scope even though they belong to
    /// the same installation, and treating them as though they were is how a Global purge quietly
    /// deletes one Campaign's memory.
    /// </remarks>
    public bool Covers(CovenantOperationScope owner)
    {

        if (!owner.IsInitialized)
        {

            return false;

        }

        if (Snapshot.Coverage == CovenantLeaseCoverage.Installation)
        {

            return true;

        }

        if (Snapshot.Scope is not { IsInitialized: true } scope)
        {

            return false;

        }

        return scope.Kind == owner.Kind
            && scope.CampaignId == owner.CampaignId;

    }

}

/// <summary>
/// The one kernel that deletes database-owned Covenant-derived artifacts.
/// </summary>
/// <remarks>
/// One implementation, two callers, thirteen policies. Every consumer compiles against this exact
/// method: an erasure path that reached SQLite directly would be a fourteenth policy nobody wrote
/// down.
/// </remarks>
public interface ICovenantProtectedArtifactErasureKernel
{

    ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
        CovenantProtectedArtifactErasurePage page,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// The one kernel that deletes Arcanum-owned files living outside SQLite.
/// </summary>
/// <remarks>
/// Deletion crosses a boundary the database cannot roll back, so exactly one implementation owns the
/// whole sequence: durable work item, no-follow open, same-handle ownership verification,
/// compare-delete, parent fsync, and label completion. A second implementation would be a second
/// opinion about which file is Arcanum's, and only one of them can be right.
/// </remarks>
public interface ICovenantManagedFileErasureKernel
{

    ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
        CovenantManagedFileErasureRequest request,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default);

}
