using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The gate that decides whether an attested full reset ends or only reports itself admitted.
/// </summary>
/// <remarks>
/// One question, asked from the service's side: does an externally authorized reset continue into the
/// ordinary ending — the sweep that deletes the Grimoire, the accepted credentials, verification and
/// retirement — and does it continue only when the durable record says every managed file this
/// installation recorded has been accounted for.
///
/// <para>A partial of the service suite so it can use the fakes every other test there is written
/// against. A filter derived from this file's name would match nothing — filter on
/// <c>InstallationResetServiceTests</c> instead.</para>
/// </remarks>
public sealed partial class InstallationResetServiceTests
{

    [Fact]
    public async Task An_attested_apply_ends_the_reset_once_the_managed_file_inventory_is_verified()
    {

        Guid operationId = Guid.Parse("5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a");

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        RecordingTerminalContinuation terminal = new() { Store = active };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            cleanup,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: new FakeRemediationVerifier(Authorization(operationId)),
            terminalContinuation: () => terminal);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        // The durable record the marker-pair coordinator would have left: markers gone, Campaign
        // receipt terminal, and every managed file accounted for.
        active.OnWrite = record => active.Record = record with
        {
            HostToolsMarkerPairReset = record.HostToolsMarkerPairReset
                ?? TerminalMarkerCheckpoint(record),
        };

        Result<InstallationResetResult> applied = await ApplyFullUnderTestLockAsync(
            service,
            FullRequest(operationId, plan.PlanId, planRequest),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        // It continued: the sweep that deletes the Grimoire ran, and the last authorized step ran
        // after it.
        Assert.True(cleanup.Executed);

        Assert.Equal(1, terminal.Calls);

        Assert.Equal(InstallationResetPhase.Completed, applied.Value.Phase);

        Assert.False(applied.Value.ResumeRequired);

        Assert.True(applied.Value.Verification.Succeeded);

        Assert.Contains(operationId, active.RetiredOperationIds);

        // The evidence the terminal step published survived every later checkpoint. A continuation
        // that carried its own older record forward would have written this straight back to null,
        // leaving an installation whose credentials are gone and whose record says they never were.
        Assert.Equal(
            InstallationResetRestoreCredentialCleanupPhase.VerifiedAbsent,
            active.Writes[^1].HostToolsMarkerPairReset?.RestoreCredentialCleanup);

    }

    [Fact]
    public async Task An_attested_apply_short_of_a_verified_inventory_reports_admitted_and_deletes_nothing()
    {

        Guid operationId = Guid.Parse("5b5b5b5b-5b5b-4b5b-8b5b-5b5b5b5b5b5b");

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        RecordingTerminalContinuation terminal = new() { Store = active };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            cleanup,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: new FakeRemediationVerifier(Authorization(operationId)),
            terminalContinuation: () => terminal);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        // No managed-file reconciliation at all: the installation still records files nothing has
        // accounted for, and deleting the database that describes them would strand them.
        Result<InstallationResetResult> applied = await ApplyFullUnderTestLockAsync(
            service,
            FullRequest(operationId, plan.PlanId, planRequest),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.False(cleanup.Executed);

        Assert.Equal(0, terminal.Calls);

        Assert.True(applied.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Value.ErrorCode);

        Assert.Empty(active.RetiredOperationIds);

    }

    [Fact]
    public async Task A_terminal_step_that_refuses_leaves_the_reset_resumable_rather_than_verified()
    {

        Guid operationId = Guid.Parse("5c5c5c5c-5c5c-4c5c-8c5c-5c5c5c5c5c5c");

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        RecordingTerminalContinuation terminal = new()
        {
            Outcome = Result<FullInstallationResetTerminalOutcome>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "This profile's restore state is not provably terminal.")),
        };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            cleanup,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: new FakeRemediationVerifier(Authorization(operationId)),
            terminalContinuation: () => terminal);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        active.OnWrite = record => active.Record = record with
        {
            HostToolsMarkerPairReset = record.HostToolsMarkerPairReset
                ?? TerminalMarkerCheckpoint(record),
        };

        Result<InstallationResetResult> applied = await ApplyFullUnderTestLockAsync(
            service,
            FullRequest(operationId, plan.PlanId, planRequest),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, terminal.Calls);

        // The sweep already ran, so the installation is past its point of no return — but a refusal
        // here must never be reported as a finished reset.
        Assert.True(applied.Value.ResumeRequired);

        Assert.NotEqual(InstallationResetPhase.Completed, applied.Value.Phase);

        Assert.Empty(active.RetiredOperationIds);

    }

    /// <summary>
    /// The marker-pair checkpoint an attested reset carries once its managed files are accounted for.
    /// </summary>
    private static HostToolsMarkerPairResetCheckpointV1 TerminalMarkerCheckpoint(
        InstallationResetActiveRecord record)
    {

        ImmutableArray<Guid> empty = [];

        FullInstallationResetRemediationClaimV1 claim =
            record.FullInstallationResetRemediationClaim!;

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            claim.OperationId,
            claim.InstallationId,
            HostToolsTransitionId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
            TaintMasterKeyVersion: 7,
            AuthorityFingerprint: Fixed(0x5A),
            DatabaseMarkerDigest: Fixed(0x5B),
            OsMarkerDigest: Fixed(0x5C),
            RemediationActionDigest: Fixed(0x5D),
            NonceBase64Url: "nonce",
            Issuer: "issuer",
            IssuedAtUtc: claim.AcceptedAtUtc,
            ExpiresAtUtc: claim.AcceptedAtUtc.AddHours(1),
            SignatureBase64Url: "signature");

        return new HostToolsMarkerPairResetCheckpointV1(
            Version: 1,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            new FullInstallationResetRestartProofV1(
                Version: 1,
                FullInstallationResetSignedAttestationProjectionV1.FromAttestation(attestation),
                claim.AcceptedAtUtc,
                claim.AttestationDigest,
                new HostProcessToolsDatabaseMarkerEvidence(
                    claim.InstallationId.ToString(),
                    RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState
                        .HostToolsTainted,
                    attestation.HostToolsTransitionId,
                    attestation.TaintMasterKeyVersion,
                    attestation.AuthorityFingerprint),
                new HostProcessToolsOsMarkerEvidence(
                    claim.InstallationId.ToString(),
                    attestation.HostToolsTransitionId,
                    attestation.TaintMasterKeyVersion,
                    attestation.AuthorityFingerprint,
                    attestation.OsMarkerDigest,
                    Fixed(0x5E)),
                Fixed(0x61)),
            CampaignInventory: [],
            Fixed(0x62),
            Fixed(0x63),
            MarkerIntentCount: 0,
            empty,
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(empty).Value,
            DeletedCount: 0,
            OrphanCount: 0,
            new FullInstallationResetManagedFileCheckpointV1(
                Version: 1,
                FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
                SourceCount: 0,
                empty,
                FullInstallationResetManagedFileDigests.SourceWriteIntentVector(empty).Value,
                LocalErasureWorkItemCount: 0,
                empty,
                FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(empty).Value,
                SafeTerminalWriteIntentCount: 0,
                ManualWriteOrphanCount: 0,
                CompletedWorkItemCount: 0,
                ManualWorkItemOrphanCount: 0,
                FullInstallationResetManagedFileDigests.TerminalClassification([], []).Value));

    }

    private static CovenantDigest Fixed(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    /// <summary>
    /// A terminal continuation that records whether it ran and what it answered.
    /// </summary>
    /// <remarks>
    /// Deliberately a double rather than the real thing. What this suite is testing is the service's
    /// decision to reach the step at all and what it does with the answer; whether the step itself
    /// removes the right credentials in the right order is proven where that logic lives, against a
    /// real credential store.
    /// </remarks>
    private sealed class RecordingTerminalContinuation : IFullInstallationResetTerminalContinuation
    {

        internal int Calls { get; private set; }

        /// <summary>The store this step publishes into, exactly as the real one does.</summary>
        internal FakeActiveStore? Store { get; init; }

        internal Result<FullInstallationResetTerminalOutcome>? Outcome { get; init; }

        public Task<Result<FullInstallationResetTerminalOutcome>> CompleteAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            CancellationToken cancellationToken)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            ArgumentNullException.ThrowIfNull(publication);

            Calls++;

            if (Outcome is { } fixedOutcome)
            {

                return Task.FromResult(fixedOutcome);

            }

            // The real step publishes each irreversible removal before it returns, so what it hands
            // back is a record the caller has never seen. Echoing the input back would let a caller
            // that ignored the handback still look correct, and the handback exists precisely because
            // ignoring it silently erases the removals from the durable record.
            InstallationResetActiveRecord published =
                publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset =
                        publication.Payload.HostToolsMarkerPairReset! with
                        {
                            RestoreCredentialCleanup =
                                InstallationResetRestoreCredentialCleanupPhase.VerifiedAbsent,
                        },
                };

            Store?.Publish(published);

            return Task.FromResult(
                Result<FullInstallationResetTerminalOutcome>.Success(
                    new FullInstallationResetTerminalOutcome(
                        InstallationResetRestoreCredentialCleanupPhase.VerifiedAbsent,
                        publication with
                        {
                            Payload = InstallationResetActivePayloadV3.FromRecord(published),
                        })));

        }

    }

}
