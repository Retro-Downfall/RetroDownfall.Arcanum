using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The persisted coverage of one ordinary Covenant operation: the whole installation's Global row
/// set, or exactly one Campaign.
/// </summary>
/// <remarks>
/// Deliberately not serializable. A scope that could be round-tripped through JSON could be
/// reconstructed by a caller that never validated it, and the whole value of this type is that a
/// <see cref="CovenantScope.Campaign"/> value cannot exist without a nonempty Campaign identity.
/// Recovery journals persist the closed <see cref="CovenantScope"/> code and Campaign ID separately
/// and rebuild the value through <see cref="ForCampaign"/>.
/// </remarks>
public readonly record struct CovenantOperationScope
{

    private readonly bool _initialized;

    private readonly CovenantScope _kind;

    private readonly Guid? _campaignId;

    private CovenantOperationScope(CovenantScope kind, Guid? campaignId)
    {

        _initialized = true;

        _kind = kind;

        _campaignId = campaignId;

    }

    public static CovenantOperationScope Global => new(CovenantScope.Global, null);

    public static CovenantOperationScope ForCampaign(Guid campaignId) =>
        new(CovenantScope.Campaign, CovenantValidation.RequireNonEmpty(campaignId, nameof(campaignId)));

    public CovenantScope Kind
    {

        get
        {

            EnsureInitialized();

            return _kind;

        }

    }

    public Guid? CampaignId
    {

        get
        {

            EnsureInitialized();

            return _campaignId;

        }

    }

    /// <summary>
    /// Whether this value was produced by a factory rather than default-initialized.
    /// </summary>
    public bool IsInitialized => _initialized;

    private void EnsureInitialized()
    {

        if (!_initialized)
        {

            throw new InvalidOperationException("An uninitialized Covenant operation scope has no coverage.");

        }

    }

}

/// <summary>
/// The coverage a protected Session fork or selective import runs under. Structurally identical to
/// <see cref="CovenantOperationScope"/> and deliberately a separate type: protected transfer is the
/// only operation permitted to acquire the compound read-and-exclusive lease, and a distinct type is
/// what stops an ordinary scope from being passed to it by accident.
/// </summary>
public readonly record struct ProtectedTransferScope
{

    private readonly bool _initialized;

    private readonly CovenantScope _kind;

    private readonly Guid? _campaignId;

    private ProtectedTransferScope(CovenantScope kind, Guid? campaignId)
    {

        _initialized = true;

        _kind = kind;

        _campaignId = campaignId;

    }

    public static ProtectedTransferScope Global => new(CovenantScope.Global, null);

    public static ProtectedTransferScope ForCampaign(Guid campaignId) =>
        new(CovenantScope.Campaign, CovenantValidation.RequireNonEmpty(campaignId, nameof(campaignId)));

    public CovenantScope Kind
    {

        get
        {

            EnsureInitialized();

            return _kind;

        }

    }

    public Guid? CampaignId
    {

        get
        {

            EnsureInitialized();

            return _campaignId;

        }

    }

    public bool IsInitialized => _initialized;

    /// <summary>
    /// The equivalent ordinary scope, for the drain set a transfer close has to compute.
    /// </summary>
    public CovenantOperationScope ToOperationScope() =>
        Kind == CovenantScope.Global
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(CampaignId!.Value);

    private void EnsureInitialized()
    {

        if (!_initialized)
        {

            throw new InvalidOperationException("An uninitialized protected-transfer scope has no coverage.");

        }

    }

}

/// <summary>
/// Whether a lease covers every scope on the installation or exactly one persisted scope.
/// </summary>
/// <remarks>
/// Installation coverage is not a third persisted Covenant scope. It is a capability a caller either
/// has or does not, which is why it is a separate axis from <see cref="CovenantOperationScope"/>
/// rather than another member of it.
/// </remarks>
public enum CovenantLeaseCoverage : byte
{

    Installation = 1,

    Scoped = 2,

}

/// <summary>
/// Which concrete lease a snapshot belongs to. Present so a consumer that is handed an interface can
/// still assert the exact capability it was given.
/// </summary>
public enum CovenantLeaseKind : byte
{

    InstallationRead = 1,

    Read = 2,

    Write = 3,

    Turn = 4,

    Mcp = 5,

    Accelerator = 6,

    Cleanup = 7,

    CampaignExclusive = 8,

    ProtectedTransfer = 9,

    Exclusive = 10,

}

/// <summary>
/// The eight operations that may close Covenant admission.
/// </summary>
/// <remarks>
/// Closed and numbered because the code is persisted in a recovery journal and compared after a
/// restart. <see cref="CampaignPathMutation"/> and <see cref="CampaignDelete"/> are valid only for a
/// Campaign-exclusive acquisition, <see cref="ProtectedSessionTransfer"/> only for the compound
/// transfer acquisition, and the remaining five only for a global exclusive acquisition.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantExclusiveOperation>))]
public enum CovenantExclusiveOperation : byte
{

    CampaignPathMutation = 1,

    CampaignDelete = 2,

    ProtectedSessionTransfer = 3,

    SchemaRepair = 4,

    BackupRestore = 5,

    CovenantFamilyReinitialize = 6,

    CovenantReset = 7,

    HealthyCatalogFactoryErasure = 8,

}

/// <summary>
/// How a completed exclusive operation leaves admission.
/// </summary>
/// <remarks>
/// <see cref="RollbackAndReopen"/> may be chosen only before a durable transition and
/// <see cref="CommitAndReopen"/> only after the caller has published the matching health and
/// generation transition. <see cref="KeepClosed"/> releases the live registration but preserves the
/// recovery owner, so the operation can be resumed rather than restarted.
/// </remarks>
public enum CovenantExclusiveLeaseDisposition : byte
{

    RollbackAndReopen = 1,

    CommitAndReopen = 2,

    KeepClosed = 3,

}

/// <summary>
/// The exact identity of the operation that closed a scope.
/// </summary>
/// <remarks>
/// Recovery compares all three fields. A different operation ID, operation code, or effect digest is
/// a different operation and can never adopt this one's closed scope, which is what stops a retry
/// with a changed plan from resuming a half-finished destructive sequence.
/// </remarks>
public readonly record struct CovenantExclusiveRecoveryOwner
{

    public CovenantExclusiveRecoveryOwner(
        Guid operationId,
        CovenantExclusiveOperation operation,
        CovenantDigest effectDigest)
    {

        OperationId = CovenantValidation.RequireNonEmpty(operationId, nameof(operationId));

        Operation = operation is >= CovenantExclusiveOperation.CampaignPathMutation
            and <= CovenantExclusiveOperation.HealthyCatalogFactoryErasure
            ? operation
            : throw new ArgumentOutOfRangeException(nameof(operation));

        EffectDigest = CovenantValidation.RequireDigest(effectDigest, nameof(effectDigest));

    }

    public Guid OperationId { get; }

    public CovenantExclusiveOperation Operation { get; }

    public CovenantDigest EffectDigest { get; }

    /// <summary>
    /// Whether this value carries a real owner rather than being default-initialized.
    /// </summary>
    public bool IsValid => OperationId != Guid.Empty && EffectDigest.IsValid;

}

/// <summary>
/// Every generation and identity one lease captured at acquisition.
/// </summary>
/// <remarks>
/// Content-free by construction: it reaches diagnostics and evidence records, so it carries
/// identities, counters, and closed codes and never a key, authored content, or a path.
/// </remarks>
public sealed record CovenantOperationLeaseSnapshot(
    Guid RegistrationId,
    CovenantLeaseKind Kind,
    CovenantLeaseCoverage Coverage,
    CovenantOperationScope? Scope,
    Guid? DatasetGeneration,
    long CapabilityGeneration,
    long AuthorityEpoch,
    long CanonicalSequence,
    long? CampaignAvailabilityGeneration,
    long? CampaignPathRevision,
    ulong? AcceleratorEpoch,
    long? AppliedCampaignDeletionSequence,
    CovenantExclusiveRecoveryOwner? RecoveryOwner,
    bool CleanupOnlyHistoricalCampaign);

/// <summary>
/// One live registration inside the operation gate, as the Core lease types see it.
/// </summary>
/// <remarks>
/// This is the seam that lets the concrete lease classes live in Core beside the contracts that
/// describe them while the gate that owns their lifetime lives in Infrastructure. A lease cannot be
/// constructed without one, so a caller cannot fabricate a capability it did not acquire.
/// </remarks>
public interface ICovenantLeaseRegistration
{

    CovenantOperationLeaseSnapshot Snapshot { get; }

    CancellationToken Revocation { get; }

    ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken);

    ValueTask ReleaseAsync();

}

/// <summary>
/// A registration whose scope is closed and whose reopening is controlled by one disposition.
/// </summary>
public interface ICovenantExclusiveLeaseRegistration : ICovenantLeaseRegistration
{

    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

}

/// <summary>
/// The generation-bound capability every Covenant database access holds.
/// </summary>
public interface ICovenantOperationLease : IAsyncDisposable
{

    CovenantOperationLeaseSnapshot Snapshot { get; }

    CancellationToken Revocation { get; }

    ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken);

}

/// <summary>
/// A lease whose holder may open a bounded SQLite read snapshot.
/// </summary>
public interface ICovenantSnapshotReadLease : ICovenantOperationLease
{
}

/// <summary>
/// A lease that closed a scope and owns exactly one reopening decision.
/// </summary>
public interface ICovenantExclusiveOperationLease : ICovenantOperationLease
{

    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

}

/// <summary>
/// The sole Core hook for a durable lifecycle journal that must remain adoptable until the gate
/// disposition succeeds.
/// </summary>
/// <remarks>
/// One-shot and nonserializable. It runs only after the exact lease returns success from its
/// disposition and before the lease is disposed, so a journal can never advance past a disposition
/// that did not happen. A failure here leaves the journal nonterminal on purpose: the operation is
/// then resumable, which is strictly safer than recording a terminal phase nobody proved.
/// </remarks>
public interface ICovenantExclusivePostDispositionFinalizer
{

    ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

}

/// <summary>
/// The exact no-op finalizer for a lifecycle with no post-disposition durable transition.
/// </summary>
/// <remarks>
/// A sealed singleton with no public constructor rather than a nullable parameter or a delegate. A
/// null finalizer would make "this lifecycle has no journal" and "somebody forgot to pass one"
/// indistinguishable at the call site.
/// </remarks>
public sealed class CovenantNoOpPostDispositionFinalizer : ICovenantExclusivePostDispositionFinalizer
{

    private CovenantNoOpPostDispositionFinalizer()
    {
    }

    public static CovenantNoOpPostDispositionFinalizer Instance { get; } = new();

    public ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());

}

/// <summary>
/// Shared lifetime behaviour for every concrete Covenant lease.
/// </summary>
public abstract class CovenantOperationLease : ICovenantOperationLease
{

    private readonly ICovenantLeaseRegistration _registration;

    private int _disposed;

    private protected CovenantOperationLease(ICovenantLeaseRegistration registration)
    {

        ArgumentNullException.ThrowIfNull(registration);

        _registration = registration;

    }

    public CovenantOperationLeaseSnapshot Snapshot => _registration.Snapshot;

    public CancellationToken Revocation => _registration.Revocation;

    private protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
        IsDisposed
            ? ValueTask.FromResult(
                Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This Covenant lease has already been released.")))
            : _registration.RevalidateAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {

        // Exchange rather than a read-then-write: sixteen concurrent disposals must produce exactly
        // one release, or a delayed cleanup could remove a registration slot another lease now owns.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        await _registration.ReleaseAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);

    }

}

/// <summary>
/// Shared one-shot disposition behaviour for the three exclusive leases.
/// </summary>
public abstract class CovenantExclusiveOperationLease
    : CovenantOperationLease, ICovenantExclusiveOperationLease
{

    private readonly ICovenantExclusiveLeaseRegistration _exclusive;

    private int _dispositionClaimed;

    private protected CovenantExclusiveOperationLease(ICovenantExclusiveLeaseRegistration registration)
        : base(registration) =>
        _exclusive = registration;

    public ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken) =>
        CompleteAsync(disposition, CovenantNoOpPostDispositionFinalizer.Instance, cancellationToken);

    /// <summary>
    /// Completes this lease and, only on success, invokes the caller's durable journal finalizer.
    /// </summary>
    public async ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        ICovenantExclusivePostDispositionFinalizer finalizer,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(finalizer);

        if (disposition is not CovenantExclusiveLeaseDisposition.RollbackAndReopen
            and not CovenantExclusiveLeaseDisposition.CommitAndReopen
            and not CovenantExclusiveLeaseDisposition.KeepClosed)
        {

            throw new ArgumentOutOfRangeException(nameof(disposition));

        }

        // Claimed before the attempt, not after it. A disposition that fails must not be retried
        // with a different code: the operation's durable state is then unknown, and guessing again
        // is how a rollback silently becomes a commit.
        if (Interlocked.Exchange(ref _dispositionClaimed, 1) != 0)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This exclusive Covenant lease has already used its one disposition."));

        }

        Result disposed = await _exclusive.CompleteAsync(disposition, cancellationToken).ConfigureAwait(false);

        if (disposed.IsFailure)
        {

            return disposed;

        }

        return await finalizer
            .FinalizeAfterSuccessfulDispositionAsync(disposition, cancellationToken)
            .ConfigureAwait(false);

    }

}

/// <summary>
/// The sole all-scopes snapshot-read capability, used by installation-wide status, list, query, and
/// full-backup snapshots.
/// </summary>
public sealed class CovenantInstallationReadLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration), ICovenantSnapshotReadLease
{
}

/// <summary>
/// A bounded read over exactly one persisted scope.
/// </summary>
public sealed class CovenantReadLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration), ICovenantSnapshotReadLease
{
}

/// <summary>
/// A canonical mutation capability. Deliberately not a snapshot-read lease: a writer that could also
/// satisfy the store's read ports would let a mutation path serve prompt content from its own
/// half-applied transaction.
/// </summary>
public sealed class CovenantWriteLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration)
{
}

/// <summary>
/// The single lease one Covenant-bearing logical turn retains from snapshot creation through
/// finalization or discard.
/// </summary>
public sealed class CovenantTurnLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration), ICovenantSnapshotReadLease
{
}

/// <summary>
/// The capability an internal MCP mutation tool stages under.
/// </summary>
public sealed class CovenantMcpLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration)
{
}

/// <summary>
/// The single-writer accelerator capability, bound to an accelerator epoch and applied
/// Campaign-deletion sequence as well as the dataset generation.
/// </summary>
public sealed class CovenantAcceleratorLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration)
{
}

/// <summary>
/// The owner-journal cleanup capability for one scope.
/// </summary>
public sealed class CovenantCleanupLease(ICovenantLeaseRegistration registration)
    : CovenantOperationLease(registration)
{
}

/// <summary>
/// The exclusive lease for Campaign path mutation and Campaign deletion.
/// </summary>
public sealed class CovenantCampaignExclusiveLease(ICovenantExclusiveLeaseRegistration registration)
    : CovenantExclusiveOperationLease(registration)
{
}

/// <summary>
/// The one compound lease protected Session fork and selective import run under.
/// </summary>
/// <remarks>
/// Read and exclusive in one object on purpose. The alternative — acquire a read lease, then close
/// the same scope exclusively — deadlocks against its own drain set.
/// </remarks>
public sealed class CovenantProtectedTransferLease(ICovenantExclusiveLeaseRegistration registration)
    : CovenantExclusiveOperationLease(registration), ICovenantSnapshotReadLease
{
}

/// <summary>
/// The installation-wide exclusive lease for schema repair, restore, family reinitialize, reset, and
/// healthy-catalog factory erasure.
/// </summary>
public sealed class CovenantExclusiveLease(ICovenantExclusiveLeaseRegistration registration)
    : CovenantExclusiveOperationLease(registration)
{
}

/// <summary>
/// Whether a Campaign identity is still live, has been deleted, or was never registered.
/// </summary>
public enum CovenantCampaignScopeState : byte
{

    Live = 1,

    Deleted = 2,

    Unknown = 3,

}

/// <summary>
/// The gate's only question about a Campaign: does this identity still exist?
/// </summary>
/// <remarks>
/// Separate from the Campaign repository so the gate does not take a dependency on the whole Forge
/// surface, and so the answer can be supplied from the cleanup journal during pre-readiness recovery
/// when the Campaign row itself is already gone.
/// </remarks>
public interface ICovenantCampaignScopeProbe
{

    ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
        Guid campaignId,
        CancellationToken cancellationToken);

}
