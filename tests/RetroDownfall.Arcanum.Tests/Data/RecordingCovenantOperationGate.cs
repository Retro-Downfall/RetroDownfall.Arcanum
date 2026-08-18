using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// A gate that grants every ordinary read and records exactly which capability was taken, when it was
/// released, and how many were ever held at once.
/// </summary>
/// <remarks>
/// Issue #116's lease criteria are about *shape*, not about drain behaviour: a planner must take one
/// installation read lease for an installation-wide inventory, one scoped read lease for a single
/// Campaign, and must never nest a second one inside the first. Driving that against the real gate
/// would prove the gate's concurrency rules over again and would still not distinguish "took one
/// lease" from "took one, released it, took another" — which is exactly the mistake this records.
///
/// <para>Every acquisition this class does not model is a deliberate refusal rather than a stub
/// success. A planner that reached for a write, turn, accelerator, cleanup, or exclusive capability
/// would be doing something issue #116 does not authorize, and failing closed makes that a test
/// failure instead of a silent pass.</para>
/// </remarks>
internal sealed class RecordingCovenantOperationGate : ICovenantOperationGate
{

    private readonly List<string> _acquisitions = [];

    private readonly Lock _sync = new();

    private int _live;

    private int _peak;

    /// <summary>Every granted capability, in acquisition order.</summary>
    internal IReadOnlyList<string> Acquisitions
    {

        get
        {

            lock (_sync)
            {

                return [.. _acquisitions];

            }

        }

    }

    /// <summary>How many leases are still held right now.</summary>
    internal int LiveLeases
    {

        get
        {

            lock (_sync)
            {

                return _live;

            }

        }

    }

    /// <summary>The greatest number ever held at the same instant. Two means something nested.</summary>
    internal int PeakConcurrentLeases
    {

        get
        {

            lock (_sync)
            {

                return _peak;

            }

        }

    }

    public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            Result<CovenantInstallationReadLease>.Success(
                new CovenantInstallationReadLease(
                    Register("installation-read", CovenantLeaseKind.InstallationRead, null))));

    }

    public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string label = scope.Kind is CovenantScope.Global
            ? "read:global"
            : $"read:{scope.CampaignId!.Value:D}";

        return ValueTask.FromResult(
            Result<CovenantReadLease>.Success(
                new CovenantReadLease(Register(label, CovenantLeaseKind.Read, scope))));

    }

    public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        Refuse<CovenantWriteLease>("write");

    public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken) =>
        Refuse<CovenantTurnLease>("turn");

    public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        Refuse<CovenantMcpLease>("mcp");

    public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
        CancellationToken cancellationToken) =>
        Refuse<CovenantAcceleratorLease>("accelerator");

    public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        Refuse<CovenantCleanupLease>("cleanup");

    public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantCampaignExclusiveLease>("campaign-exclusive");

    public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantProtectedTransferLease>("protected-transfer");

    public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantExclusiveLease>("exclusive");

    public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantCampaignExclusiveLease>("resume-campaign-exclusive");

    public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantProtectedTransferLease>("resume-protected-transfer");

    public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        Refuse<CovenantExclusiveLease>("resume-exclusive");

    private static ValueTask<Result<T>> Refuse<T>(string capability)
        where T : class =>
        ValueTask.FromResult(
            Result<T>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    $"Issue #116 planning must not acquire a {capability} capability.")));

    private RecordingRegistration Register(
        string label,
        CovenantLeaseKind kind,
        CovenantOperationScope? scope)
    {

        lock (_sync)
        {

            _acquisitions.Add(label);

            _live++;

            if (_live > _peak)
            {

                _peak = _live;

            }

        }

        return new RecordingRegistration(this, kind, scope);

    }

    private void Release()
    {

        lock (_sync)
        {

            _live--;

        }

    }

    private sealed class RecordingRegistration(
        RecordingCovenantOperationGate owner,
        CovenantLeaseKind kind,
        CovenantOperationScope? scope) : ICovenantLeaseRegistration
    {

        private int _released;

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.NewGuid(),
            Kind: kind,
            Coverage: scope is null
                ? CovenantLeaseCoverage.Installation
                : CovenantLeaseCoverage.Scoped,
            Scope: scope,
            DatasetGeneration: CovenantOperationGateFixtureDatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 1,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                owner.Release();

            }

            return ValueTask.CompletedTask;

        }

    }

    private static readonly Guid CovenantOperationGateFixtureDatasetGeneration =
        new("11111111-1111-4111-8111-111111111111");

}
