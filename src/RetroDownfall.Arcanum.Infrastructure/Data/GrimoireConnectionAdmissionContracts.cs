using System.Collections.Concurrent;
using System.Data.Common;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal interface IGrimoireOrdinaryConnectionFactory
{

    Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken);

}

internal interface IGrimoireOrdinaryConnectionLease : IDisposable, IAsyncDisposable
{

    SqliteConnection Connection { get; }

}

internal interface IGrimoireMaintenanceConnectionFactory
{

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCanonicalErasureAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalWalTruncationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    /// <summary>Opens the ordinary read-write handle that puts write-ahead logging back after a replace.</summary>
    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalPostReplaceRestoreAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCompactionAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalExportVerificationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalAcceleratorInitializationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCandidateReopenAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalInventorySnapshotAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

}

internal interface IGrimoireMaintenanceConnectionLease : IAsyncDisposable
{

    SqliteConnection Connection { get; }

}

internal static class GrimoireScopedConsumerTestSeam
{

    private static readonly ConcurrentDictionary<
        string,
        Func<GrimoireScopedConsumerFinalUse, CancellationToken, ValueTask>> Checkpoints =
        new(StringComparer.Ordinal);

    internal static IDisposable Override(
        string checkpoint,
        Func<GrimoireScopedConsumerFinalUse, CancellationToken, ValueTask> callback)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);

        ArgumentNullException.ThrowIfNull(callback);

        if (!Checkpoints.TryAdd(checkpoint, callback))
        {

            throw new InvalidOperationException($"The scoped consumer checkpoint '{checkpoint}' is already overridden.");

        }

        return new OverrideScope(checkpoint, callback);

    }

    internal static ValueTask PauseAsync(
        string checkpoint,
        GrimoireScopedConsumerFinalUseKind kind,
        int observation,
        CancellationToken cancellationToken) =>
        Checkpoints.TryGetValue(
            checkpoint,
            out Func<GrimoireScopedConsumerFinalUse, CancellationToken, ValueTask>? callback)
            ? callback(
                new GrimoireScopedConsumerFinalUse(checkpoint, kind, observation),
                cancellationToken)
            : ValueTask.CompletedTask;

    private sealed class OverrideScope(
        string checkpoint,
        Func<GrimoireScopedConsumerFinalUse, CancellationToken, ValueTask> callback) : IDisposable
    {

        private int _disposed;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                if (Checkpoints.TryGetValue(
                        checkpoint,
                        out Func<GrimoireScopedConsumerFinalUse, CancellationToken, ValueTask>? registered)
                    && ReferenceEquals(registered, callback))
                {

                    _ = Checkpoints.TryRemove(checkpoint, out _);

                }

            }

        }

    }

}

internal enum GrimoireScopedConsumerFinalUseKind : byte
{

    ScalarConverted = 1,

    ReaderMaterialized = 2,

    TransactionCommitted = 3,

    TransactionRolledBack = 4,

}

internal readonly record struct GrimoireScopedConsumerFinalUse(
    string Checkpoint,
    GrimoireScopedConsumerFinalUseKind Kind,
    int Observation);

internal enum GrimoireOrdinaryFreshConnectionKind : byte
{

    ReadOnly = 1,

    ReadWrite = 2,

    IsolatedHeartbeat = 3,

}

internal interface IGrimoireOrdinaryConnectionFactoryTestSeam
{

    void BeforeProviderConstruction();

    ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken);

    void AfterExactPoolClear(SqliteConnection connection);

}

/// <summary>
/// Process-local authority over ordinary physical Grimoire opens and exclusive maintenance closure.
/// </summary>
internal interface IGrimoireConnectionAdmissionGate
{

    long CurrentGeneration { get; }

    bool TryAcquireRequestLease(
        GrimoireRequestKind kind,
        out IGrimoireRequestLease? lease);

    bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease);

    IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection);

    Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null);

    ValueTask<Result> DrainRequestAndWorkAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken);

    ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken);

    ValueTask<Result> AbortClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
        CancellationToken cancellationToken);

    Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken);

    ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(
        CovenantExclusiveRecoveryOwner candidateOwner,
        Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

}

/// <summary>
/// The ordinary request lifetime policy applied before endpoint execution.
/// </summary>
internal enum GrimoireRequestKind : byte
{

    Finite = 1,

    QuiesceableStream = 2,

}

/// <summary>
/// The complete background unit whose scope and durable disposition drain together.
/// </summary>
internal enum GrimoireWorkKind : byte
{

    SessionAttachmentIndexing = 1,

    EntryWeaving = 2,

    SagaExtraction = 3,

}

/// <summary>
/// One admitted request, held through asynchronous disposal of its request scope.
/// </summary>
internal interface IGrimoireRequestLease : IAsyncDisposable
{

    GrimoireRequestKind Kind { get; }

    long Generation { get; }

    CancellationToken MaintenanceRevocation { get; }

}

/// <summary>
/// One admitted background unit, held through its final effect disposition and scope disposal.
/// </summary>
internal interface IGrimoireWorkLease : IAsyncDisposable
{

    GrimoireWorkKind Kind { get; }

    long Generation { get; }

    CancellationToken MaintenanceRevocation { get; }

    bool TryBeginExternalEffectGroup(
        out IGrimoireExternalEffectGroup? effectGroup);

}

/// <summary>
/// Atomic authority for one independently resumable external-effect group.
/// </summary>
internal interface IGrimoireExternalEffectGroup : IAsyncDisposable
{
}

/// <summary>
/// One physical ordinary-open attempt, from before native open through its terminal callback.
/// </summary>
internal interface IGrimoireConnectionOpenTicket : IDisposable
{

    long Generation { get; }

    Result RevalidateAfterNativeOpen();

    Result MarkOpened();

    void MarkFailed();

    void MarkRefusedAfterOpen();

}

/// <summary>
/// The exact owner token for the recoverable transition that precedes closed admission.
/// </summary>
internal interface IGrimoireClosingOwner : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

}

/// <summary>
/// Exclusive authority issued only after every unresolved physical open has terminated.
/// </summary>
internal interface IGrimoireExclusiveClosedLease : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

    Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(
        DbConnection connection);

    Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(
        IGrimoireMaintenanceIoLane lane);

    /// <summary>
    /// Issues one capability for one purpose, with the path and mode that purpose implies.
    /// </summary>
    /// <remarks>
    /// Neither a path nor a mode is a parameter, and that is the point. A caller that could name the
    /// file could name a different one, and the gate's own comparison on the way back in would then
    /// be comparing a caller's value with the same caller's value - which proves nothing. The purpose
    /// is the only thing a caller supplies, and the gate decides everything that follows from it.
    /// </remarks>
    Result<IGrimoireMaintenanceConnectionCapability> IssueMaintenanceConnectionCapability(
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane);

    ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
        Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

}

/// <summary>
/// Reusable authority for physical opens of one exact maintenance-owned connection object.
/// </summary>
internal interface IGrimoireScopedConnectionPermit : IAsyncDisposable
{

    Result<IGrimoireTrackedMaintenanceHandle> AcquireOpen(
        DbConnection connection,
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One-shot authority for one independent unpooled durable-owner renewal.
/// </summary>
internal interface IGrimoireMaintenanceRenewalTicket : IAsyncDisposable
{

    Result<IGrimoireTrackedMaintenanceHandle> Consume(
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One-shot authority for one purpose-, mode-, and canonical-path-bound maintenance open.
/// </summary>
internal interface IGrimoireMaintenanceConnectionCapability : IAsyncDisposable
{

    /// <summary>The path the gate bound to this capability's purpose, which no caller chose.</summary>
    string CanonicalPath { get; }

    /// <summary>The mode the gate bound to this capability's purpose.</summary>
    CovenantMaintenanceConnectionMode Mode { get; }

    /// <summary>The one purpose this capability may be spent on.</summary>
    CovenantMaintenanceConnectionPurpose Purpose { get; }

    Result<IGrimoireTrackedMaintenanceHandle> Consume(
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One maintenance physical-open lifetime that ends only after open failure or physical closure.
/// </summary>
/// <remarks>
/// The lifetime is disposable so that a phase can hold it under <c>await using</c> like every other
/// authority the closed lease issues. Without that, a phase that throws between consuming its
/// capability and reporting the outcome leaks the lifetime by construction rather than by mistake,
/// and a leaked lifetime blocks both the closed lease's disposition and its lane's release.
///
/// Disposal is the guard, not a second way to report, and it may only say what it knows. A lifetime
/// that already reported is left alone. One whose native open never started did not open, and
/// disposal may complete it. One whose open had started is recorded as abandoned and stays live:
/// disposal did not inspect the connection, so it may not claim a closure, and a closure it claimed
/// falsely would let ordinary admission reopen over a connection that is possibly still open. The
/// owner's disposition stays refused until something that did observe the connection reports it.
///
/// The member is required rather than defaulted, because a lifetime that owns a physical open and
/// silently inherits a do-nothing disposal is the leak it exists to close.
/// </remarks>
internal interface IGrimoireTrackedMaintenanceHandle : IAsyncDisposable
{

    Result ReportOpenStarted();

    Result ReportNotOpened();

    Result ReportPhysicallyClosed();

}

/// <summary>
/// Exclusive connection-sensitive phase ownership over the shared adoption interlock.
/// </summary>
internal interface IGrimoireMaintenanceIoLane : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

    ValueTask<Result> RevalidateDurableOwnerAsync(
        Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

}

/// <summary>
/// Exclusive expired-owner adoption ownership over the shared maintenance interlock.
/// </summary>
internal interface IGrimoireExpiredLeaseAdoptionInterlock : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner CandidateOwner { get; }

}

/// <summary>
/// SQLite access requested by a maintenance phase.
/// </summary>
internal enum CovenantMaintenanceConnectionMode : byte
{

    ReadOnly = 1,

    ReadWrite = 2,

}

/// <summary>
/// The closed phase that is authorized to construct one fresh maintenance connection.
/// </summary>
internal enum CovenantMaintenanceConnectionPurpose : byte
{

    CanonicalErasure = 1,

    Compaction = 2,

    IntegrityVerification = 3,

    SidecarProof = 4,

    ReopenVerification = 5,

    WalTruncation = 6,

    AcceleratorInitialization = 7,

    InventorySnapshot = 8,

    /// <summary>
    /// The one reopen that puts write-ahead logging back on a database an export just replaced.
    /// </summary>
    /// <remarks>
    /// A purpose of its own rather than a second use of <see cref="Compaction"/>, because the two
    /// need opposite engine configurations and the purpose is what decides it. Compaction opens an
    /// exclusive maintenance handle, and the initializer applies a journal mode only to the ordinary
    /// read-write one — so restoring the mode through the compaction purpose sets nothing and proves
    /// nothing, which is exactly the silence a step whose whole job is a read-back must not have.
    /// </remarks>
    PostReplaceJournalRestore = 9,

}

/// <summary>
/// Where a maintenance purpose's connection actually points, resolved once for the process.
/// </summary>
/// <remarks>
/// An injected authority rather than a literal read of <c>ArcanumPaths</c> inside the gate, and the
/// reason is a safety one rather than a stylistic one. A suite that exercises a real erasure against
/// a scratch database has to be able to say which file that is; a gate that resolved the
/// installation's own path from a static would point every one of those tests at the developer's
/// real Grimoire and then prove the bytes were gone. The composition root supplies the real one, and
/// nothing else can.
///
/// <para>It answers for the process, not for a caller. A capability still carries no path a caller
/// chose - the purpose decides which of these two the gate binds, and the caller has no way to say
/// otherwise.</para>
/// </remarks>
internal interface IGrimoireMaintenancePathAuthority
{

    /// <summary>The installation's canonical Grimoire database.</summary>
    string CanonicalDatabasePath { get; }

    /// <summary>
    /// The candidate database one transition's verified export is written to and proven on.
    /// </summary>
    /// <remarks>
    /// Bound to the operation rather than fixed, so the file a resumed run finds is one it can claim
    /// or disown by name before it opens anything. It is derived here rather than accepted from a
    /// caller for the same reason the canonical path is: a caller that could name the file could name
    /// a different one, and the comparison made on the way back in would be comparing a caller's value
    /// with the same caller's value.
    /// </remarks>
    string ExportStagingDatabasePath(Guid operationId);

}

/// <summary>
/// Expected refusal raised before ordinary code reaches SQLite while admission is closed.
/// </summary>
internal sealed class GrimoireMaintenanceUnavailableException : InvalidOperationException
{

    /// <summary>
    /// The code a caller reports when it turns this refusal into a <c>Result</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from the generic unavailable code, because a closed admission gate is a deliberate,
    /// temporary refusal rather than a product failure. Without a code of its own, the one thing a
    /// caller can say about it is the same thing it says about an invalid connection string.
    ///
    /// <para>It reads the Core constant rather than repeating its text. The same code now reaches a
    /// caller three ways — this exception, a <c>Result</c> the connection factory returns, and the
    /// HTTP refusal admission writes — and a wire-stable code spelled in more than one place is one
    /// that can be corrected in one place and left wrong in the others.</para>
    /// </remarks>
    internal const string Code = ErrorCodes.Grimoire.MaintenanceUnavailable;

    internal GrimoireMaintenanceUnavailableException()
        : base("The Grimoire is temporarily unavailable while maintenance owns connection admission.")
    {
    }

}
