using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The offline, permanently tainting transition from a clean installation to one that may hand
/// model-driven code the operator's operating-system identity.
/// </summary>
/// <remarks>
/// Every assertion here is about what happens when a step does <i>not</i> succeed. The transition
/// writes two independent markers, and the interesting states are the ones between them: a database
/// row that claims a taint the operating system cannot confirm, or an OS marker whose write outcome
/// is unknown. Both leave the installation blocked rather than clean, because a clean answer after
/// an uncertain escape is the one answer that can never be walked back (§10.12).
/// </remarks>
public sealed class HostProcessToolsTransitionServiceTests
{

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    private static readonly Guid OtherTransition = Guid.Parse("11112222-3333-4444-5555-666677778888");

    [Fact]
    public async Task A_stopped_clean_development_installation_persists_both_markers_and_advances_authority()
    {

        Harness harness = Harness.Create();

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HostProcessToolsTransitionOutcome.Completed, result.Value.Outcome);

        Assert.Equal(Transition, result.Value.TransitionId);

        Assert.True(result.Value.RestartRequired);

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, harness.Authority.Row.State);

        Assert.Equal(Transition, harness.Authority.Row.TransitionId);

        // The taint-time version is pinned to the key in force at the transition, not to whatever
        // the installation rotates to afterwards.
        Assert.Equal(harness.Authority.Row.CurrentMasterKeyVersion, harness.Authority.Row.TaintMasterKeyVersion);

        Assert.Equal(2, harness.Authority.Row.AuthorityEpoch);

        Assert.Equal(2, harness.Authority.Row.RecoveryEnvelopeEpoch);

        Assert.NotNull(harness.Markers.Stored);

        Assert.True(harness.Lock.Released);

    }

    [Fact]
    public async Task The_transition_is_refused_outside_development_and_without_the_environment_opt_in()
    {

        Harness local = Harness.Create(edition: ArcanumEdition.Local);

        Result<HostProcessToolsTransitionResult> refusedEdition = await local.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, refusedEdition.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.EditionOrOptInMissing, refusedEdition.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.Clean, local.Authority.Row.State);

        Harness withoutOptIn = Harness.Create(escapeHatchOptIn: false);

        Result<HostProcessToolsTransitionResult> refusedOptIn = await withoutOptIn.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, refusedOptIn.Value.Outcome);

        Assert.Null(withoutOptIn.Markers.Stored);

    }

    [Fact]
    public async Task A_running_host_refuses_the_transition_before_any_marker_is_touched()
    {

        Harness harness = Harness.Create();

        harness.Lock.Available = false;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.HostRunning, result.Value.Blocker);

        Assert.Null(harness.Markers.Stored);

        Assert.Equal(CovenantHostToolsState.Clean, harness.Authority.Row.State);

    }

    [Fact]
    public async Task A_process_that_has_already_opened_covenant_cannot_taint_itself()
    {

        Harness harness = Harness.Create();

        harness.Environment.CovenantOpenedInThisProcess = true;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.CovenantAlreadyOpened, result.Value.Blocker);

    }

    [Fact]
    public async Task Residual_covenant_or_protected_state_refuses_the_transition()
    {

        Harness harness = Harness.Create();

        harness.Authority.CanonicalRowCount = 1;

        Result<HostProcessToolsTransitionResult> canonical = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, canonical.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.ProtectedStatePresent, canonical.Value.Blocker);

        harness.Authority.CanonicalRowCount = 0;

        harness.Authority.ProtectedArtifactCount = 3;

        Result<HostProcessToolsTransitionResult> artifacts = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionBlocker.ProtectedStatePresent, artifacts.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.Clean, harness.Authority.Row.State);

        Assert.Null(harness.Markers.Stored);

    }

    [Fact]
    public async Task An_uncertain_operating_system_write_leaves_the_installation_pending_and_blocked()
    {

        Harness harness = Harness.Create();

        harness.Markers.WriteStatus = HostProcessToolsMarkerWriteStatus.Uncertain;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.PendingManualRemediation, result.Value.Outcome);

        Assert.False(result.Value.RestartRequired);

        Assert.Equal(CovenantHostToolsState.PendingHostToolsTaint, harness.Authority.Row.State);

        // Compensation is forbidden once the operating-system boundary may have been written.
        Assert.Equal(0, harness.Markers.CompareDeleteCount);

    }

    [Fact]
    public async Task A_proven_refused_write_compensates_only_its_own_pending_row()
    {

        Harness harness = Harness.Create();

        harness.Markers.WriteStatus = HostProcessToolsMarkerWriteStatus.Refused;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.MarkerWriteRefused, result.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.Clean, harness.Authority.Row.State);

    }

    [Fact]
    public async Task A_readback_that_does_not_match_leaves_the_installation_pending()
    {

        Harness harness = Harness.Create();

        harness.Markers.CorruptOnReadback = true;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.PendingManualRemediation, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.MarkerReadbackMismatch, result.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.PendingHostToolsTaint, harness.Authority.Row.State);

    }

    [Fact]
    public async Task A_database_failure_after_the_marker_is_written_leaves_the_installation_pending()
    {

        Harness harness = Harness.Create();

        harness.Authority.FailTaintCommit = true;

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.PendingManualRemediation, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.AuthorityCommitFailed, result.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.PendingHostToolsTaint, harness.Authority.Row.State);

        Assert.NotNull(harness.Markers.Stored);

        Assert.Equal(0, harness.Markers.CompareDeleteCount);

    }

    [Fact]
    public async Task The_same_transition_identity_resumes_from_a_pending_row_and_an_existing_marker()
    {

        Harness harness = Harness.Create();

        harness.Authority.FailTaintCommit = true;

        _ = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        harness.Authority.FailTaintCommit = false;

        int writesBeforeResume = harness.Markers.WriteCount;

        Result<HostProcessToolsTransitionResult> resumed = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Completed, resumed.Value.Outcome);

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, harness.Authority.Row.State);

        // The marker already read back exactly, so resuming must not rewrite the slot.
        Assert.Equal(writesBeforeResume, harness.Markers.WriteCount);

    }

    [Fact]
    public async Task A_completed_transition_replays_as_already_completed_for_the_same_identity_only()
    {

        Harness harness = Harness.Create();

        _ = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Result<HostProcessToolsTransitionResult> replay = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.AlreadyCompleted, replay.Value.Outcome);

        Assert.Equal(2, harness.Authority.Row.AuthorityEpoch);

        Result<HostProcessToolsTransitionResult> other = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(OtherTransition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, other.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.ForeignTransitionIdentity, other.Value.Blocker);

    }

    [Fact]
    public async Task A_different_transition_identity_cannot_take_over_a_pending_row()
    {

        Harness harness = Harness.Create();

        harness.Markers.WriteStatus = HostProcessToolsMarkerWriteStatus.Uncertain;

        _ = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        harness.Markers.WriteStatus = HostProcessToolsMarkerWriteStatus.Written;

        Result<HostProcessToolsTransitionResult> takeover = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(OtherTransition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.Refused, takeover.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.ForeignTransitionIdentity, takeover.Value.Blocker);

        Assert.Equal(Transition, harness.Authority.Row.TransitionId);

    }

    [Fact]
    public async Task A_clean_row_beside_a_stray_marker_is_manual_remediation_rather_than_a_fresh_transition()
    {

        Harness harness = Harness.Create();

        harness.Markers.SeedForeignMarker();

        Result<HostProcessToolsTransitionResult> result = await harness.Service.EnableAsync(
            new HostProcessToolsTransitionRequest(Transition),
            CancellationToken.None);

        Assert.Equal(HostProcessToolsTransitionOutcome.PendingManualRemediation, result.Value.Outcome);

        Assert.Equal(HostProcessToolsTransitionBlocker.MarkerPairMismatch, result.Value.Blocker);

        Assert.Equal(CovenantHostToolsState.Clean, harness.Authority.Row.State);

    }

    [Fact]
    public void The_outcome_and_blocker_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(1, (byte)HostProcessToolsTransitionOutcome.Completed);

        Assert.Equal(2, (byte)HostProcessToolsTransitionOutcome.AlreadyCompleted);

        Assert.Equal(3, (byte)HostProcessToolsTransitionOutcome.PendingManualRemediation);

        Assert.Equal(4, (byte)HostProcessToolsTransitionOutcome.Refused);

        Assert.Equal(4, Enum.GetValues<HostProcessToolsTransitionOutcome>().Length);

        Assert.DoesNotContain(
            Enum.GetValues<HostProcessToolsTransitionOutcome>(),
            static value => (byte)value == 0);

    }

    private sealed class Harness
    {

        private Harness(
            HostProcessToolsTransitionService service,
            FakeHostProcessToolsAuthorityStore authority,
            FakeHostProcessToolsMarkerStore markers,
            FakeHostProcessToolsEnvironmentProbe environment,
            FakeHostProcessToolsInstallationLockSource installationLock)
        {

            Service = service;

            Authority = authority;

            Markers = markers;

            Environment = environment;

            Lock = installationLock;

        }

        internal HostProcessToolsTransitionService Service { get; }

        internal FakeHostProcessToolsAuthorityStore Authority { get; }

        internal FakeHostProcessToolsMarkerStore Markers { get; }

        internal FakeHostProcessToolsEnvironmentProbe Environment { get; }

        internal FakeHostProcessToolsInstallationLockSource Lock { get; }

        internal static Harness Create(
            ArcanumEdition edition = ArcanumEdition.Development,
            bool escapeHatchOptIn = true)
        {

            FakeHostProcessToolsAuthorityStore authority = new();

            FakeHostProcessToolsMarkerStore markers = new();

            FakeHostProcessToolsEnvironmentProbe environment = new()
            {
                Edition = edition,

                EscapeHatchOptIn = escapeHatchOptIn,
            };

            FakeHostProcessToolsInstallationLockSource installationLock = new();

            HostProcessToolsTransitionService service = new(
                authority,
                markers,
                environment,
                installationLock,
                new HostProcessToolsMarkerPairJoiner(),
                HostProcessToolsTestGate.Shared);

            return new Harness(service, authority, markers, environment, installationLock);

        }

    }

}
