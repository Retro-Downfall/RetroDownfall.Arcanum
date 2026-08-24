using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The one authorized route to the marker-pair coordinator, and what the routing may not cost.
/// </summary>
/// <remarks>
/// The admission outcome is identical whether the coordinator succeeds or refuses, so nothing here
/// reads the returned result to decide whether routing happened. What it asserts instead is which
/// method was called, what it was handed, and — for the paths that must never reach it — that it was
/// never resolved at all.
/// </remarks>
public sealed partial class InstallationResetServiceTests
{

    [Fact]
    public async Task Full_admission_hands_the_durable_publication_to_the_marker_pair_coordinator()
    {

        Guid operationId = Guid.Parse("51515151-5151-4151-8151-515151515151");

        FakeActiveStore active = new();

        RecordingMarkerPairCoordinator coordinator = new();

        (InstallationResetService service, FullInstallationResetRequest request) =
            await CreateAdmittableFullResetAsync(operationId, active, () => coordinator);

        Result<InstallationResetResult> result =
            await ApplyFullUnderTestLockAsync(service, request);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, coordinator.BeginCalls);

        Assert.Equal(0, coordinator.ResumeCalls);

        // The publication it is handed is the one the claim was just written into, not a
        // reconstruction. Everything the coordinator acts on it authenticates again from that
        // record, so handing it anything else would simply be refused.
        Assert.NotNull(coordinator.Publication);

        Assert.Equal(
            operationId,
            coordinator.Publication.Payload.OperationId);

        Assert.NotNull(
            coordinator.Publication.Payload.FullInstallationResetRemediationClaim);

        // Only the authorization the caller already holds: the operator's own signed statement,
        // unaltered.
        Assert.Same(request.ExternalRemediation, coordinator.Attestation);

        Assert.NotNull(coordinator.HeldLock);

        // After the claim is durable, never before. A coordinator that ran first would be
        // authenticating against a record that does not exist yet.
        Assert.True(active.Written);

    }

    [Fact]
    public async Task Full_admission_retry_resumes_from_a_persisted_pair_checkpoint()
    {

        Guid operationId = Guid.Parse("52525252-5252-4252-8252-525252525252");

        FakeActiveStore active = new();

        RecordingMarkerPairCoordinator coordinator = new();

        (InstallationResetService service, FullInstallationResetRequest request) =
            await CreateAdmittableFullResetAsync(operationId, active, () => coordinator);

        _ = await ApplyFullUnderTestLockAsync(service, request);

        Assert.Equal(1, coordinator.BeginCalls);

        active.Record = active.Record! with
        {
            HostToolsMarkerPairReset = PairCheckpoint(operationId),
        };

        Result<InstallationResetResult> resumed =
            await ApplyFullUnderTestLockAsync(service, request);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        // Beginning a record that already carries a checkpoint would be refused by the coordinator
        // anyway; asking for the wrong one would turn a resumable operation into a refusal that
        // reads like corruption.
        Assert.Equal(1, coordinator.BeginCalls);

        Assert.Equal(1, coordinator.ResumeCalls);

    }

    [Fact]
    public async Task Full_admission_retry_begins_again_while_no_checkpoint_exists()
    {

        Guid operationId = Guid.Parse("53535353-5353-4353-8353-535353535353");

        FakeActiveStore active = new();

        RecordingMarkerPairCoordinator coordinator = new();

        (InstallationResetService service, FullInstallationResetRequest request) =
            await CreateAdmittableFullResetAsync(operationId, active, () => coordinator);

        _ = await ApplyFullUnderTestLockAsync(service, request);

        _ = await ApplyFullUnderTestLockAsync(service, request);

        Assert.Equal(2, coordinator.BeginCalls);

        Assert.Equal(0, coordinator.ResumeCalls);

    }

    [Fact]
    public async Task Marker_pair_refusal_leaves_the_admission_outcome_unchanged()
    {

        Guid operationId = Guid.Parse("54545454-5454-4454-8454-545454545454");

        RecordingMarkerPairCoordinator refusing = new()
        {
            Refuses = true,
        };

        FakeActiveStore active = new();

        (InstallationResetService service, FullInstallationResetRequest request) =
            await CreateAdmittableFullResetAsync(operationId, active, () => refusing);

        Result<InstallationResetResult> result =
            await ApplyFullUnderTestLockAsync(service, request);

        // The reset is incomplete either way, and the operator is told so either way. Letting a
        // marker-pair refusal replace the admission would report an operation as never admitted
        // while its claim sits durable on disk.
        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.Equal(1, refusing.BeginCalls);

        Assert.True(active.Written);

    }

    [Fact]
    public async Task Ordinary_locked_apply_never_resolves_the_marker_pair_coordinator()
    {

        FakeActiveStore active = new();

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            stateRoots: new FixedStateRoots(["/state"]),
            markerPairReset: static () => throw new InvalidOperationException(
                "An ordinary reset must never reach the marker-pair coordinator."));

        Result<InstallationResetPlan> planned = await service.PlanAsync(
            new InstallationResetPlanRequest(InstallationResetScope.Global, "/invocation"),
            CancellationToken.None);

        Assert.True(planned.IsSuccess, planned.Error.Message);

        Result<InstallationResetResult> applied = await ApplyUnderTestLockAsync(
            service,
            new InstallationResetApplyRequest(
                new InstallationResetPlanRequest(
                    InstallationResetScope.Global,
                    "/invocation"),
                planned.Value.PlanId));

        // The factory throwing is the assertion. Planning and the ordinary apply have to work on an
        // installation whose Grimoire is absent or locked, and the coordinator's graph reaches the
        // encrypted database — so a resolution here would be a database requirement smuggled into
        // paths built specifically not to need one.
        Assert.True(applied.IsSuccess || applied.IsFailure);

    }

    [Fact]
    public void Reset_composition_registers_one_marker_pair_coordinator_and_one_port_each()
    {

        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumCliClientStack();

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

        services.AddArcanumInstallationReset(new ArcanumSettings());

        services.AddArcanumInstallationReset(new ArcanumSettings());

        // One implementation per port, and adding the composition twice does not make two. A port
        // with a second implementation is a coordinator that can be handed a second opinion about
        // which marker it is deleting.
        foreach (Type port in new[]
                 {
                     typeof(IHostToolsMarkerPairResetCoordinator),
                     typeof(IHostToolsMarkerPairResetDatabase),
                     typeof(IHostToolsMarkerPairResetOsPort),
                     typeof(IFullInstallationResetCampaignSchemaReadiness),
                     typeof(HostProcessToolsMarkerMutationGate),
                     typeof(Func<IHostToolsMarkerPairResetCoordinator>),
                 })
        {

            Assert.Single(services, descriptor => descriptor.ServiceType == port);

        }

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        using IServiceScope scope = provider.CreateScope();

        // The coordinator itself is not instantiated here on purpose: its graph reaches the
        // encrypted database, and a test that opened one would be asserting the fixture rather than
        // the registration. Validate-on-build already proved the whole call site resolves.
        Assert.IsType<HostToolsMarkerPairResetDatabase>(
            scope.ServiceProvider.GetRequiredService<IHostToolsMarkerPairResetDatabase>());

        Assert.IsType<HostProcessToolsMarkerResetAdapter>(
            scope.ServiceProvider.GetRequiredService<IHostToolsMarkerPairResetOsPort>());

        Assert.IsType<FullInstallationResetCampaignSchemaReadiness>(
            scope.ServiceProvider
                .GetRequiredService<IFullInstallationResetCampaignSchemaReadiness>());

        // The process-wide exclusion the taint transition and this reset share. Two instances
        // exclude nothing from each other.
        Assert.Same(
            provider.GetRequiredService<HostProcessToolsMarkerMutationGate>(),
            scope.ServiceProvider.GetRequiredService<HostProcessToolsMarkerMutationGate>());

        // The adapter refuses a capability minted by any other instance, so a second one would be a
        // second authority over the same credential slot.
        using IServiceScope second = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<IHostToolsMarkerPairResetOsPort>(),
            second.ServiceProvider.GetRequiredService<IHostToolsMarkerPairResetOsPort>());

    }

    /// <summary>
    /// An accepted full-reset request over a resolvable workspace, with the coordinator routed in.
    /// </summary>
    private static async Task<(InstallationResetService Service, FullInstallationResetRequest Request)>
        CreateAdmittableFullResetAsync(
            Guid operationId,
            FakeActiveStore active,
            Func<IHostToolsMarkerPairResetCoordinator> markerPairReset)
    {

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: new FakeRemediationVerifier(Authorization(operationId)),
            markerPairReset: markerPairReset);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        Result<InstallationResetPlan> planned = await service.PlanAsync(
            planRequest,
            CancellationToken.None);

        Assert.True(planned.IsSuccess, planned.Error.Message);

        return (service, FullRequest(operationId, planned.Value.PlanId, planRequest));

    }

    /// <summary>
    /// A shape-valid pair checkpoint. Only its presence matters to the routing decision.
    /// </summary>
    private static HostToolsMarkerPairResetCheckpointV1 PairCheckpoint(Guid operationId)
    {

        CovenantDigest digest = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

        HostProcessToolsDatabaseMarkerEvidence database = new(
            "installation-identity",
            RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            digest);

        HostProcessToolsOsMarkerEvidence os = new(
            "installation-identity",
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            digest,
            digest,
            digest);

        return new HostToolsMarkerPairResetCheckpointV1(
            1,
            HostToolsMarkerPairResetPhase.PairJournaled,
            new FullInstallationResetRestartProofV1(
                1,
                new FullInstallationResetSignedAttestationProjectionV1(
                    1,
                    operationId,
                    Guid.Parse("40404040-4040-4040-8040-404040404040"),
                    database.TransitionId!.Value,
                    7,
                    digest,
                    digest,
                    digest,
                    digest,
                    "AQIDBAUGBwgJCgsMDQ4PEA",
                    "RetroDownfall.Remediation.v1",
                    new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
                    "signature"),
                new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
                digest,
                database,
                os,
                digest),
            ImmutableArray<CampaignMarkerInventoryEntryV1>.Empty,
            digest,
            digest,
            MarkerIntentCount: null,
            OrderedMarkerIntentIds: null,
            MarkerIntentVectorDigest: null,
            DeletedCount: null,
            OrphanCount: null);

    }

    private sealed class RecordingMarkerPairCoordinator : IHostToolsMarkerPairResetCoordinator
    {

        internal int BeginCalls { get; private set; }

        internal int ResumeCalls { get; private set; }

        internal bool Refuses { get; init; }

        internal ArcanumMaintenanceLock? HeldLock { get; private set; }

        internal InstallationResetActivePublication? Publication { get; private set; }

        internal FullInstallationResetExternalRemediationAttestation? Attestation
        {
            get;
            private set;
        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication acceptedClaim,
            FullInstallationResetExternalRemediationAttestation attestation,
            CancellationToken cancellationToken)
        {

            BeginCalls++;

            HeldLock = heldInstallationLock;

            Publication = acceptedClaim;

            Attestation = attestation;

            return Task.FromResult(Answer());

        }

        public Task<Result<InstallationResetActivePublication>> ResumeAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication checkpoint,
            CancellationToken cancellationToken)
        {

            ResumeCalls++;

            HeldLock = heldInstallationLock;

            Publication = checkpoint;

            return Task.FromResult(Answer());

        }

        private Result<InstallationResetActivePublication> Answer() =>
            Refuses || Publication is null
                ? Result<InstallationResetActivePublication>.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The full-installation reset marker-pair operation requires recovery."))
                : Result<InstallationResetActivePublication>.Success(Publication);

    }

}
