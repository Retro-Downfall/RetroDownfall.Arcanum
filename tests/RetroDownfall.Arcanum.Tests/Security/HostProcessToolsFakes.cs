using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// An in-memory <c>covenant_authority_state</c> singleton with the same compare-and-swap rules the
/// real row's CHECK constraints enforce.
/// </summary>
/// <remarks>
/// The transition's interesting behaviour is all in the ordering between two independent durable
/// stores, so both are faked here and the SQL and keychain implementations are asserted separately
/// against real storage. A fake that accepted a state the schema refuses would let a test pass for a
/// row that could never exist.
/// </remarks>
internal sealed class FakeHostProcessToolsAuthorityStore : IHostProcessToolsAuthorityStore
{

    internal const string Installation = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    internal FakeHostProcessToolsAuthorityStore() =>
        Row = new HostProcessToolsAuthorityRow(
            Installation,
            AuthorityEpoch: 1,
            CurrentMasterKeyVersion: 4,
            CurrentMasterKeyFingerprint: Digest(7),
            RecoveryEnvelopeEpoch: 1,
            CovenantHostToolsState.Clean,
            TransitionId: null,
            TaintMasterKeyVersion: null,
            TaintFingerprint: null);

    internal HostProcessToolsAuthorityRow Row { get; private set; }

    internal long CanonicalRowCount { get; set; }

    internal long ProtectedArtifactCount { get; set; }

    internal bool FailTaintCommit { get; set; }

    public Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<HostProcessToolsAuthorityRow>.Success(Row));

    public Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<HostProcessToolsAuthorityRow?>.Success(Row));

    public Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(Result<HostProcessToolsProtectedInventory>.Success(
            new HostProcessToolsProtectedInventory(CanonicalRowCount, ProtectedArtifactCount)));

    public Task<Result> CommitPendingAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        if (Row != expected || Row.State is not CovenantHostToolsState.Clean)
        {

            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "The authority row moved.")));

        }

        Row = Row with
        {
            State = CovenantHostToolsState.PendingHostToolsTaint,

            TransitionId = transitionId,

            TaintMasterKeyVersion = Row.CurrentMasterKeyVersion,

            TaintFingerprint = Row.CurrentMasterKeyFingerprint,
        };

        return Task.FromResult(Result.Success());

    }

    public Task<Result> CommitTaintedAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        if (FailTaintCommit)
        {

            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.Grimoire.WriteFailed,
                "The taint commit failed.")));

        }

        if (Row.State is not CovenantHostToolsState.PendingHostToolsTaint || Row.TransitionId != transitionId)
        {

            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "The authority row moved.")));

        }

        Row = Row with
        {
            State = CovenantHostToolsState.HostToolsTainted,

            AuthorityEpoch = Row.AuthorityEpoch + 1,

            RecoveryEnvelopeEpoch = Row.RecoveryEnvelopeEpoch + 1,
        };

        return Task.FromResult(Result.Success());

    }

    public Task<Result> CompensateToCleanAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        if (Row.State is not CovenantHostToolsState.PendingHostToolsTaint || Row.TransitionId != transitionId)
        {

            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "The authority row moved.")));

        }

        Row = Row with
        {
            State = CovenantHostToolsState.Clean,

            TransitionId = null,

            TaintMasterKeyVersion = null,

            TaintFingerprint = null,
        };

        return Task.FromResult(Result.Success());

    }

    internal static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

}

/// <summary>An in-memory taint slot whose write outcome the test chooses.</summary>
/// <remarks>
/// <see cref="WriteStatus"/> models the three things a real credential backend can prove, including
/// the uncertain write that is the whole reason compensation is restricted.
/// </remarks>
internal sealed class FakeHostProcessToolsMarkerStore : IHostProcessToolsMarkerStore
{

    private static readonly Guid ForeignTransition = Guid.Parse("99998888-7777-6666-5555-444433332222");

    internal byte[]? Stored { get; private set; }

    internal HostProcessToolsMarkerWriteStatus WriteStatus { get; set; } =
        HostProcessToolsMarkerWriteStatus.Written;

    internal HostProcessToolsMarkerReadStatus? ReadStatusOverride { get; set; }

    internal bool CorruptOnReadback { get; set; }

    internal int WriteCount { get; private set; }

    internal int CompareDeleteCount { get; private set; }

    internal void SeedForeignMarker() =>
        Stored = HostProcessToolsMarkerPayload.Encode(
            FakeHostProcessToolsAuthorityStore.Installation,
            ForeignTransition,
            taintMasterKeyVersion: 4,
            FakeHostProcessToolsAuthorityStore.Digest(7));

    public HostProcessToolsMarkerReadResult Read()
    {

        if (ReadStatusOverride is { } forced)
        {

            return new HostProcessToolsMarkerReadResult(forced, null);

        }

        if (Stored is not { } payload)
        {

            return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Absent, null);

        }

        if (!HostProcessToolsMarkerPayload.TryDecode(payload, out HostProcessToolsMarkerFields fields))
        {

            return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Malformed, null);

        }

        return new HostProcessToolsMarkerReadResult(
            HostProcessToolsMarkerReadStatus.Present,
            new HostProcessToolsOsMarkerEvidence(
                fields.InstallationIdentity,
                fields.TransitionId,
                fields.TaintMasterKeyVersion,
                fields.TaintFingerprint,
                HostProcessToolsMarkerPayload.DigestOf(payload),
                FakeHostProcessToolsAuthorityStore.Digest(200)));

    }

    public HostProcessToolsMarkerWriteStatus Write(
        string installationIdentity,
        Guid transitionId,
        ulong taintMasterKeyVersion,
        CovenantDigest taintFingerprint)
    {

        WriteCount++;

        if (WriteStatus is HostProcessToolsMarkerWriteStatus.Refused)
        {

            return WriteStatus;

        }

        // An uncertain write still stores the payload: that is exactly what makes it uncertain
        // rather than failed, and what compensation must never assume away.
        Stored = HostProcessToolsMarkerPayload.Encode(
            installationIdentity,
            CorruptOnReadback ? ForeignTransition : transitionId,
            taintMasterKeyVersion,
            taintFingerprint);

        return WriteStatus;

    }

    public bool CompareDelete(HostProcessToolsOsMarkerEvidence expected)
    {

        CompareDeleteCount++;

        if (Stored is not { } payload
            || !HostProcessToolsMarkerPayload.TryDecode(payload, out HostProcessToolsMarkerFields fields)
            || fields.TransitionId != expected.TransitionId)
        {

            return false;

        }

        Stored = null;

        return true;

    }

}

/// <summary>Trusted process facts the test sets directly.</summary>
internal sealed class FakeHostProcessToolsEnvironmentProbe : IHostProcessToolsEnvironmentProbe
{

    internal ArcanumEdition Edition { get; set; } = ArcanumEdition.Development;

    internal bool EscapeHatchOptIn { get; set; } = true;

    internal bool CovenantOpenedInThisProcess { get; set; }

    public HostProcessToolsTransitionEnvironment Read() =>
        new(Edition, EscapeHatchOptIn, CovenantOpenedInThisProcess);

}

/// <summary>An installation lock that is either free or held by somebody else.</summary>
internal sealed class FakeHostProcessToolsInstallationLockSource : IHostProcessToolsInstallationLockSource
{

    internal bool Available { get; set; } = true;

    internal bool Released { get; private set; }

    public IDisposable? TryAcquire() => Available ? new Handle(this) : null;

    private sealed class Handle(FakeHostProcessToolsInstallationLockSource owner) : IDisposable
    {

        public void Dispose() => owner.Released = true;

    }

}
