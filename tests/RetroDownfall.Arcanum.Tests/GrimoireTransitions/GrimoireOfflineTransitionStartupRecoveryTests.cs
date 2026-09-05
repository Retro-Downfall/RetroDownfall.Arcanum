using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// Turning the pair matrix's three journal-active answers into a resumption rather than a refusal.
/// </summary>
/// <remarks>
/// The order is the content. Nothing may open the catalog for ordinary use before the authority a
/// crashed transition held is back and the gate is closed around it, and the validate-only handle the
/// authority was read through has to be physically gone before the handler takes its own maintenance
/// lane. Every step short-circuits, because a pass that carried on past a refusal would be adopting
/// authority for a run nobody has established.
/// </remarks>
[Collection("WorkspacePathPolicy")]
public sealed class GrimoireOfflineTransitionStartupRecoveryTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Theory]

    [InlineData(0)]

    [InlineData(1)]

    [InlineData(2)]

    [InlineData(3)]
    public async Task An_installation_with_no_active_journal_is_left_completely_alone(int arm)
    {

        InstallationResetNestedTransitionEvidenceOutcome? evidence = arm == 0
            ? null
            : (InstallationResetNestedTransitionEvidenceOutcome)arm;

        using Harness harness = Create("quiet-" + arm);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(harness.Lock, harness.Root, harness.DatabasePath, evidence, journal: null, Token);

        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.Message : null);

        Assert.Equal(
            GrimoireOfflineTransitionStartupRecoveryOutcome.NoActiveJournal,
            recovered.Value);

        Assert.Empty(harness.Steps);

    }

    [Theory]

    [InlineData(4)]

    [InlineData(5)]

    [InlineData(6)]
    public async Task Every_journal_active_answer_resumes_in_the_one_order(int arm)
    {

        InstallationResetNestedTransitionEvidenceOutcome evidence =
            (InstallationResetNestedTransitionEvidenceOutcome)arm;

        using Harness harness = Create("resume-" + arm);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                evidence,
                harness.Journal,
                Token);

        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.Message : null);

        Assert.Equal(GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed, recovered.Value);

        Assert.Equal(
            ["unlock", "load", "consume", "close", "dispatch"],
            harness.Steps);

    }

    /// <summary>
    /// The validate-only handle is gone before the handler starts, and the test can tell.
    /// </summary>
    /// <remarks>
    /// The handler closes the Grimoire and waits for every enrolled handle to close physically. A
    /// recovery pass still holding its own probe would be waiting for itself, and the failure mode is
    /// a startup that hangs rather than one that refuses.
    /// </remarks>
    [Fact]
    public async Task The_recovery_probe_is_closed_before_the_handler_is_dispatched()
    {

        using Harness harness = Create("closed-first");

        _ = await harness.Recovery.RecoverBeforeBootstrapAsync(
            harness.Lock,
            harness.Root,
            harness.DatabasePath,
            InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition,
            harness.Journal,
            Token);

        Assert.True(harness.Steps.IndexOf("close") < harness.Steps.IndexOf("dispatch"));

        Assert.True(harness.Unlock.Disposed);

    }

    [Fact]
    public async Task A_journal_active_answer_with_no_journal_evidence_refuses()
    {

        using Harness harness = Create("no-journal");

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                InstallationResetNestedTransitionEvidenceOutcome.NestedBound,
                journal: null,
                Token);

        AssertRefused(recovered);

        Assert.Empty(harness.Steps);

    }

    [Fact]
    public async Task A_recovery_required_answer_refuses_without_touching_the_catalog()
    {

        using Harness harness = Create("recovery-required");

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
                harness.Journal,
                Token);

        AssertRefused(recovered);

        Assert.Empty(harness.Steps);

    }

    [Theory]

    [InlineData("unlock", new[] { "unlock" })]

    [InlineData("load", new[] { "unlock", "load", "close" })]

    [InlineData("consume", new[] { "unlock", "load", "consume", "close" })]

    [InlineData("dispatch", new[] { "unlock", "load", "consume", "close", "dispatch" })]
    public async Task A_refusal_at_any_step_stops_the_pass_there(string failing, string[] expected)
    {

        using Harness harness = Create("short-circuit-" + failing, failAt: failing);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                InstallationResetNestedTransitionEvidenceOutcome.NestedBound,
                harness.Journal,
                Token);

        AssertRefused(recovered);

        // The probe is closed on every path that opened it, including the failing ones. A refusal that
        // leaked the handle would leave the sidecars a later attempt has to prove absent.
        Assert.Equal(expected, harness.Steps);

    }

    /// <summary>
    /// A handler that did not reach a durable verdict is a refusal, not a resumption.
    /// </summary>
    /// <remarks>
    /// A parked transition has closed admission behind it. Reporting it as resumed would let the host
    /// go on to bootstrap and publish readiness over a catalog that is still part way through being
    /// remade.
    /// </remarks>
    [Theory]

    [InlineData(LongRunningOperationSettlementOutcome.RequiresAttention)]

    [InlineData(LongRunningOperationSettlementOutcome.ConcurrencyLost)]

    [InlineData(LongRunningOperationSettlementOutcome.OwnedInProcess)]

    [InlineData(LongRunningOperationSettlementOutcome.NotFound)]
    public async Task A_settlement_short_of_a_durable_verdict_refuses(
        LongRunningOperationSettlementOutcome settlement)
    {

        using Harness harness = Create("settlement-" + settlement, settlement: settlement);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                InstallationResetNestedTransitionEvidenceOutcome.NestedBound,
                harness.Journal,
                Token);

        AssertRefused(recovered);

    }

    [Theory]

    [InlineData(LongRunningOperationSettlementOutcome.Completed)]

    [InlineData(LongRunningOperationSettlementOutcome.Failed)]

    [InlineData(LongRunningOperationSettlementOutcome.Abandoned)]
    public async Task Every_durable_verdict_reads_as_resumed(
        LongRunningOperationSettlementOutcome settlement)
    {

        using Harness harness = Create("verdict-" + settlement, settlement: settlement);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered = await harness.Recovery
            .RecoverBeforeBootstrapAsync(
                harness.Lock,
                harness.Root,
                harness.DatabasePath,
                InstallationResetNestedTransitionEvidenceOutcome.NestedBound,
                harness.Journal,
                Token);

        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.Message : null);

        Assert.Equal(GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed, recovered.Value);

    }

    private static void AssertRefused(Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered)
    {

        Assert.True(recovered.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

    }

    private Harness Create(
        string name,
        string? failAt = null,
        LongRunningOperationSettlementOutcome settlement =
            LongRunningOperationSettlementOutcome.Completed)
    {

        string root = _workspace.CreateSubdir("transition-startup-" + name);

        ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(root));

        List<string> steps = [];

        RecordingRecoveryDispatchSeam seam = new(steps, failAt, settlement);

        return new Harness(
            held,
            root,
            Path.Combine(root, "arcanum.db"),
            steps,
            seam,
            Journal(),
            new GrimoireOfflineTransitionStartupRecovery(seam, seam, seam));

    }

    private static GrimoireOfflineTransitionRecoveryEvidence Journal()
    {

        CovenantDigest digest = new(Convert.FromHexString(new string('a', 64)));

        return new GrimoireOfflineTransitionRecoveryEvidence(
            new GrimoireOfflineTransitionBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                GrimoireOfflineTransitionKind.CovenantReset,
                PayloadVersion: 1,
                SlotEpoch: 1,
                digest,
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                new GrimoireOfflineTransitionEpochTuple(1, 1, 1),
                new GrimoireOfflineTransitionEpochTuple(2, 2, 2),
                digest,
                ExpectedDatabaseOperationRevision: 2,
                ParentReceiptBindingDigest: null),
            SlotEpoch: 1,
            Revision: 3,
            digest);

    }

    private sealed record Harness(
        ArcanumMaintenanceLock Lock,
        string Root,
        string DatabasePath,
        List<string> Steps,
        RecordingRecoveryDispatchSeam Unlock,
        GrimoireOfflineTransitionRecoveryEvidence Journal,
        GrimoireOfflineTransitionStartupRecovery Recovery) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

}
