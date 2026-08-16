using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Test doubles for the three facts the Covenant operation gate compares against, plus the builders
/// that make an exclusive acquisition readable at a call site.
/// </summary>
/// <remarks>
/// The gate's whole job is to notice when one of those facts moves underneath a live lease, so each
/// double is deliberately mutable: a test changes a dataset generation or an authority epoch between
/// acquisition and revalidation and asserts the gate refuses.
/// </remarks>
internal static class CovenantOperationGateFixture
{

    internal static readonly Guid DatasetGeneration = new("11111111-1111-4111-8111-111111111111");

    internal static readonly Guid CampaignOne = new("22222222-2222-4222-8222-222222222222");

    internal static readonly Guid CampaignTwo = new("33333333-3333-4333-8333-333333333333");

    internal static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[CovenantLimits.DigestBytes];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = unchecked((byte)(seed + index));

        }

        return new CovenantDigest(bytes);

    }

    internal static CovenantExclusiveRecoveryOwner Owner(
        CovenantExclusiveOperation operation,
        Guid? operationId = null,
        byte effectSeed = 7) =>
        new(operationId ?? new Guid("44444444-4444-4444-8444-444444444444"), operation, Digest(effectSeed));

    internal static CanonicalCampaignContext CampaignContext(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 5,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 9,
            rootIdentityDigest: Digest(21));

    internal static CovenantOperationGate CreateGate(
        FakeCovenantAvailability? availability = null,
        FakeCovenantAuthorityProvider? authority = null,
        FakeCovenantCampaignScopeProbe? campaigns = null,
        TimeSpan? drainTimeout = null) =>
        new(
            availability ?? new FakeCovenantAvailability(),
            authority ?? new FakeCovenantAuthorityProvider(),
            campaigns ?? new FakeCovenantCampaignScopeProbe(),
            drainTimeout ?? TimeSpan.FromSeconds(5));

}

internal sealed class FakeCovenantAvailability : ICovenantAvailability
{

    private CovenantAvailabilitySnapshot _current = new(
        Generation: 1,
        FeatureEnabled: true,
        Canonical: CovenantCapabilityState.Healthy,
        CanonicalSchemaVersion: 1,
        CanonicalInstalledFingerprint: "fingerprint",
        Accelerator: CovenantCapabilityState.Healthy,
        AcceleratorSchemaVersion: 1,
        AcceleratorInstalledFingerprint: "fingerprint",
        DatasetGeneration: CovenantOperationGateFixture.DatasetGeneration,
        CanonicalSequence: 12,
        CoreCampaignDeletionSequence: 3,
        AppliedDatasetGeneration: CovenantOperationGateFixture.DatasetGeneration,
        AppliedSequence: 12,
        AppliedCampaignDeletionSequence: 3,
        AcceleratorEpoch: 4,
        FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
        RebuildRequired: false,
        LastHealthTransition: CovenantHealthTransition.Bootstrap,
        CanonicalDiagnosticCode: null,
        AcceleratorDiagnosticCode: null);

    public CovenantAvailabilitySnapshot Current => Volatile.Read(ref _current);

    internal void Mutate(Func<CovenantAvailabilitySnapshot, CovenantAvailabilitySnapshot> change)
    {

        CovenantAvailabilitySnapshot current = Volatile.Read(ref _current);

        Volatile.Write(ref _current, change(current) with { Generation = current.Generation + 1 });

    }

}

internal sealed class FakeCovenantAuthorityProvider : ICovenantAuthoritySnapshotProvider
{

    private CovenantAuthoritySnapshot? _current = new(
        InstallationIdentity: "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
        AuthorityEpoch: 1,
        MasterKeyVersion: 1,
        RecoveryEnvelopeEpoch: 1,
        HostToolsState: CovenantHostToolsState.Clean,
        TransitionId: null);

    public CovenantAuthoritySnapshot? Current => Volatile.Read(ref _current);

    internal void Advance() =>
        Volatile.Write(
            ref _current,
            Current! with { AuthorityEpoch = Current!.AuthorityEpoch + 1 });

    internal void Clear() => Volatile.Write(ref _current, null);

}

internal sealed class FakeCovenantCampaignScopeProbe : ICovenantCampaignScopeProbe
{

    private readonly Dictionary<Guid, CovenantCampaignScopeState> _states = new()
    {
        [CovenantOperationGateFixture.CampaignOne] = CovenantCampaignScopeState.Live,

        [CovenantOperationGateFixture.CampaignTwo] = CovenantCampaignScopeState.Live,
    };

    internal void Set(Guid campaignId, CovenantCampaignScopeState state)
    {

        lock (_states)
        {

            _states[campaignId] = state;

        }

    }

    public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        lock (_states)
        {

            return ValueTask.FromResult(
                Result<CovenantCampaignScopeState>.Success(
                    _states.TryGetValue(campaignId, out CovenantCampaignScopeState state)
                        ? state
                        : CovenantCampaignScopeState.Deleted));

        }

    }

}

/// <summary>
/// A one-shot finalizer that records whether the gate invoked it, and can be told to fail.
/// </summary>
internal sealed class RecordingPostDispositionFinalizer(bool succeed = true)
    : ICovenantExclusivePostDispositionFinalizer
{

    private int _invocations;

    internal int Invocations => Volatile.Read(ref _invocations);

    internal CovenantExclusiveLeaseDisposition? ObservedDisposition { get; private set; }

    public ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken)
    {

        _ = Interlocked.Increment(ref _invocations);

        ObservedDisposition = disposition;

        return ValueTask.FromResult(
            succeed
                ? Result.Success()
                : Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "The durable journal did not advance.")));

    }

}
