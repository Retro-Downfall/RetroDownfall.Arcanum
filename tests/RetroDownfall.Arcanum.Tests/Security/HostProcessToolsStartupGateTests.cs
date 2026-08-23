using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The startup classification that decides whether this process may open Covenant at all.
/// </summary>
/// <remarks>
/// The gate is the only place the two markers are compared before anything else in the host exists,
/// so these tests assert the ordering as well as the outcomes: an unreadable operating-system marker
/// has to block before the database is opened, and no disposition other than a proven clean pair may
/// leave Covenant permitted.
/// </remarks>
public sealed class HostProcessToolsStartupGateTests
{

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    [Fact]
    public async Task A_clean_installation_with_no_marker_starts_normally()
    {

        Harness harness = Harness.Create();

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.Clean, result.Value.Disposition);

        Assert.True(harness.Policy.CovenantPermitted);

        Assert.False(harness.Policy.HostProcessToolsPermitted);

    }

    [Fact]
    public async Task The_escape_hatch_environment_without_a_completed_transition_blocks_startup()
    {

        Harness harness = Harness.Create();

        harness.Environment.EscapeHatchOptIn = true;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Contains(HostProcessToolsStartupGate.OfflineCommand, result.Error.Message, StringComparison.Ordinal);

        Assert.False(harness.Policy.CovenantPermitted);

        // The blocker is what lets the host tell "clean but armed" apart from "evidence disagrees":
        // only the first is survivable, and only until the offline enable command ships.
        Assert.Equal(
            HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition,
            harness.Policy.Blocker);

    }

    [Fact]
    public async Task Every_block_that_evidence_could_not_produce_on_its_own_is_distinguishable()
    {

        Harness pending = Harness.Create();

        _ = await pending.Authority.CommitPendingAsync(
            pending.Authority.Row,
            Transition,
            CancellationToken.None);

        _ = await pending.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.PendingTransition, pending.Policy.Blocker);

        Harness stray = Harness.Create();

        stray.Markers.SeedForeignMarker();

        _ = await stray.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.MarkerMismatch, stray.Policy.Blocker);

        Harness unreadable = Harness.Create();

        unreadable.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        _ = await unreadable.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.MarkerMismatch, unreadable.Policy.Blocker);

    }

    [Fact]
    public async Task A_matching_tainted_pair_starts_in_permanent_no_covenant_mode()
    {

        Harness harness = Harness.Create();

        harness.Taint();

        harness.Environment.EscapeHatchOptIn = true;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.TaintedMatched, result.Value.Disposition);

        Assert.False(harness.Policy.CovenantPermitted);

        Assert.True(harness.Policy.HostProcessToolsPermitted);

    }

    [Fact]
    public async Task Removing_the_opt_in_suppresses_the_tool_and_never_restores_covenant()
    {

        Harness harness = Harness.Create();

        harness.Taint();

        harness.Environment.EscapeHatchOptIn = false;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(harness.Policy.HostProcessToolsPermitted);

        Assert.False(harness.Policy.CovenantPermitted);

        harness.Environment.Edition = ArcanumEdition.Local;

        Result<HostProcessToolsStartupDecision> again = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(again.IsSuccess);

        Assert.False(harness.Policy.CovenantPermitted);

    }

    [Fact]
    public async Task A_pending_transition_always_blocks()
    {

        Harness harness = Harness.Create();

        _ = await harness.Authority.CommitPendingAsync(
            harness.Authority.Row,
            Transition,
            CancellationToken.None);

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.PendingBlocked, harness.Policy.Disposition);

    }

    [Fact]
    public async Task A_tainted_row_without_a_marker_and_a_clean_row_with_one_both_block()
    {

        Harness taintedWithoutMarker = Harness.Create();

        taintedWithoutMarker.Taint();

        _ = taintedWithoutMarker.Markers.ClearStoredForTest(TakeMarker(taintedWithoutMarker));

        Assert.True((await taintedWithoutMarker.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Harness cleanWithMarker = Harness.Create();

        cleanWithMarker.Markers.SeedForeignMarker();

        Assert.True((await cleanWithMarker.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Assert.False(cleanWithMarker.Policy.CovenantPermitted);

    }

    [Fact]
    public async Task A_malformed_marker_blocks_before_the_database_is_opened()
    {

        Harness harness = Harness.Create();

        harness.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Malformed;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Equal(0, harness.Authority.ReadCount);

    }

    [Fact]
    public async Task An_unreachable_credential_backend_is_never_read_as_an_absent_marker()
    {

        Harness harness = Harness.Create();

        harness.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        Assert.True((await harness.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Assert.False(harness.Policy.CovenantPermitted);

    }

    [Fact]
    public void An_unpublished_policy_permits_nothing()
    {

        HostProcessToolsRuntimePolicy policy = new();

        Assert.False(policy.IsPublished);

        Assert.False(policy.CovenantPermitted);

        Assert.False(policy.HostProcessToolsPermitted);

        Assert.Null(policy.Disposition);

    }

    [Fact]
    public void A_tainted_policy_refuses_to_be_republished_as_covenant_permitting()
    {

        HostProcessToolsRuntimePolicy policy = new();

        Assert.True(policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.TaintedMatched,
            CovenantPermitted: false,
            HostProcessToolsPermitted: true)).IsSuccess);

        Result restored = policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.Clean,
            CovenantPermitted: true,
            HostProcessToolsPermitted: false));

        Assert.True(restored.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, restored.Error.Code);

        Assert.False(policy.CovenantPermitted);

    }

    private static HostProcessToolsOsMarkerEvidence TakeMarker(Harness harness) =>
        harness.Markers.Read().Marker!;

    private sealed class Harness
    {

        private Harness(
            HostProcessToolsStartupGate gate,
            CountingHostProcessToolsAuthorityStore authority,
            FakeHostProcessToolsMarkerStore markers,
            FakeHostProcessToolsEnvironmentProbe environment,
            HostProcessToolsRuntimePolicy policy)
        {

            Gate = gate;

            Authority = authority;

            Markers = markers;

            Environment = environment;

            Policy = policy;

        }

        internal HostProcessToolsStartupGate Gate { get; }

        internal CountingHostProcessToolsAuthorityStore Authority { get; }

        internal FakeHostProcessToolsMarkerStore Markers { get; }

        internal FakeHostProcessToolsEnvironmentProbe Environment { get; }

        internal HostProcessToolsRuntimePolicy Policy { get; }

        internal static Harness Create()
        {

            CountingHostProcessToolsAuthorityStore authority = new();

            FakeHostProcessToolsMarkerStore markers = new();

            FakeHostProcessToolsEnvironmentProbe environment = new()
            {
                Edition = ArcanumEdition.Development,

                EscapeHatchOptIn = false,
            };

            HostProcessToolsRuntimePolicy policy = new();

            HostProcessToolsStartupGate gate = new(
                markers,
                authority,
                environment,
                new HostProcessToolsMarkerPairJoiner(),
                policy);

            return new Harness(gate, authority, markers, environment, policy);

        }

        /// <summary>Drives the real transition so the two markers agree the way production makes them.</summary>
        internal void Taint()
        {

            HostProcessToolsTransitionService service = new(
                Authority,
                Markers,
                new FakeHostProcessToolsEnvironmentProbe(),
                new FakeHostProcessToolsInstallationLockSource(),
                new HostProcessToolsMarkerPairJoiner(),
                HostProcessToolsTestGate.Shared);

            Result<HostProcessToolsTransitionResult> result = service
                .EnableAsync(new HostProcessToolsTransitionRequest(Transition), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(HostProcessToolsTransitionOutcome.Completed, result.Value.Outcome);

            Authority.ReadCount = 0;

        }

    }

    /// <summary>The authority fake plus a read counter, for the ordering assertions.</summary>
    private sealed class CountingHostProcessToolsAuthorityStore : IHostProcessToolsAuthorityStore
    {

        private readonly FakeHostProcessToolsAuthorityStore _inner = new();

        internal int ReadCount { get; set; }

        internal HostProcessToolsAuthorityRow Row => _inner.Row;

        public Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken)
        {

            ReadCount++;

            return _inner.ReadAsync(cancellationToken);

        }

        public Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken)
        {

            ReadCount++;

            return _inner.TryReadAsync(cancellationToken);

        }

        public Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
            CancellationToken cancellationToken) =>
            _inner.InventoryProtectedStateAsync(cancellationToken);

        public Task<Result> CommitPendingAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CommitPendingAsync(expected, transitionId, cancellationToken);

        public Task<Result> CommitTaintedAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CommitTaintedAsync(expected, transitionId, cancellationToken);

        public Task<Result> CompensateToCleanAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CompensateToCleanAsync(expected, transitionId, cancellationToken);

    }

}
