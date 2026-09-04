using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The launch checkpoint and its terminal reconciliation against the real durable ledger.
/// </summary>
/// <remarks>
/// The arithmetic this proves cannot be proven against a stand-in that was seeded with the answer.
/// Committing a launch checkpoint advances the row's revision, so the revision recorded inside the
/// payload is never the revision the terminal compare-exchange must find — and a test that seeded
/// both numbers itself would pass whichever way round the production code had them.
///
/// <para>Every column carries a distinct value for the same reason: the payload is projected by
/// position, and two transposed blobs read identically to a suite that seeded them equal.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class CovenantOfflineTransitionLaunchTerminalStoreTests : IAsyncLifetime
{

    private static readonly Guid Source = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid Target = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private const string Effect = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly GrimoireFixture _fixture;

    private readonly FakeTimeProvider _time = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public CovenantOfflineTransitionLaunchTerminalStoreTests(GrimoireFixture fixture) =>
        _fixture = fixture;

    public Task InitializeAsync()
    {

        if (!GrimoireFixture.SqlCipherAvailable)
        {

            return Task.CompletedTask;

        }

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (_dbPath.Length > 0 && File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    /// <summary>
    /// The whole cutover, against the real store: launch, bind, terminalize, and come back to it.
    /// </summary>
    [SkippableFact]
    public async Task A_launched_transition_terminalizes_its_real_row_once_and_stays_terminal()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store();

        LongRunningOperation launched = await LaunchAsync(store);

        // The checkpoint commit advanced the row, so the revision the launch recorded is already
        // history by the time the journal binds one.
        LongRunningOperation committed = (await store.GetAsync(launched.Id))!;

        Assert.Equal(launched.Revision + 1, committed.Revision);

        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, committed.CheckpointVersion);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await ReconcileAsync(
            store,
            committed,
            launched.Revision,
            GrimoireOfflineTransitionTerminalDisposition.Completed,
            CovenantResetPhaseMachine.Last);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.Terminalized, reconciled.Outcome);

        LongRunningOperation terminal = (await store.GetAsync(launched.Id))!;

        Assert.Equal(LongRunningOperationState.Completed, terminal.State);

        Assert.Null(terminal.TerminalErrorCode);

        Assert.Equal(committed.Revision + 1, terminal.Revision);

        Assert.NotNull(terminal.CompletedAt);

        // The launch fields are immutable: terminalization changed status metadata and the revision,
        // and nothing the transition was bound to.
        Assert.Equal(committed.CheckpointVersion, terminal.CheckpointVersion);

        Assert.Equal(committed.CheckpointPayload, terminal.CheckpointPayload);

        Assert.Equal(committed.CheckpointReference, terminal.CheckpointReference);

        Assert.Equal(committed.Kind, terminal.Kind);

        Assert.Equal(committed.RecoveryPolicy, terminal.RecoveryPolicy);

        GrimoireOfflineTransitionDatabaseReconciliation again = await ReconcileAsync(
            store,
            committed,
            launched.Revision,
            GrimoireOfflineTransitionTerminalDisposition.Completed,
            CovenantResetPhaseMachine.Last);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal, again.Outcome);

        Assert.Equal(reconciled.TerminalWinnerDigest, again.TerminalWinnerDigest);

        Assert.Equal(terminal.Revision, (await store.GetAsync(launched.Id))!.Revision);

    }

    /// <summary>
    /// The stored payload is read back column by column, with a distinct value in every field, so a
    /// projection that transposed two of them cannot pass.
    /// </summary>
    [SkippableFact]
    public async Task The_stored_launch_payload_reads_back_field_for_field()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store();

        LongRunningOperation launched = await LaunchAsync(store);

        LongRunningOperation committed = (await store.GetAsync(launched.Id))!;

        Result<GrimoireOfflineTransitionLaunchBinding> projected =
            GrimoireOfflineTransitionLaunch.FromLaunch(
                CovenantRecoveryCheckpointCodec
                    .DecodeCovenantOfflineTransitionLaunch(committed.CheckpointPayload!)
                    .Value);

        Assert.True(projected.IsSuccess);

        Assert.Equal(launched.Id, projected.Value.OperationId);

        Assert.Equal(Source, projected.Value.SourceDatasetGeneration);

        Assert.Equal(Target, projected.Value.TargetDatasetGeneration);

        Assert.Equal(11UL, projected.Value.SourceEpochs.AcceleratorEpoch);

        Assert.Equal(22UL, projected.Value.SourceEpochs.KeyReclamationEpoch);

        Assert.Equal(33UL, projected.Value.SourceEpochs.EnvelopeKeyEpoch);

        Assert.Equal(12UL, projected.Value.TargetEpochs.AcceleratorEpoch);

        Assert.Equal(23UL, projected.Value.TargetEpochs.KeyReclamationEpoch);

        Assert.Equal(34UL, projected.Value.TargetEpochs.EnvelopeKeyEpoch);

        Assert.Equal(launched.Revision, projected.Value.StartingRevision);

    }

    /// <summary>
    /// A real row whose revision has moved on since the launch is still the row this journal
    /// terminalizes.
    /// </summary>
    /// <remarks>
    /// Recovery adopts the operation's lease before it resumes a transition, and that adoption moves
    /// the row. The binding is authenticated, so a journal that demanded the exact revision its launch
    /// recorded could never catch up - the first legitimate recovery would lock it out of the one row
    /// it exists to terminalize, for good.
    /// </remarks>
    [SkippableFact]
    public async Task A_real_row_that_moved_forward_since_the_journal_bound_it_is_still_terminalized()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store();

        LongRunningOperation launched = await LaunchAsync(store);

        LongRunningOperation committed = (await store.GetAsync(launched.Id))!;

        Assert.True(
            await store.HeartbeatAsync(
                launched.Id,
                launched.LeaseOwner!,
                _time.GetUtcNow(),
                _time.GetUtcNow().AddMinutes(5)));

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await ReconcileAsync(
            store,
            committed,
            launched.Revision,
            GrimoireOfflineTransitionTerminalDisposition.Completed,
            CovenantResetPhaseMachine.Last);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.Terminalized, reconciled.Outcome);

        Assert.True(reconciled.PermitsRetirement);

        LongRunningOperation after = (await store.GetAsync(launched.Id))!;

        Assert.Equal(LongRunningOperationState.Completed, after.State);

    }

    /// <summary>
    /// A real row standing behind the revision the launch produced is not the row that launch created,
    /// and is left exactly as it was.
    /// </summary>
    [SkippableFact]
    public async Task A_real_row_behind_the_launch_revision_is_not_overwritten()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store();

        LongRunningOperation launched = await LaunchAsync(store);

        LongRunningOperation committed = (await store.GetAsync(launched.Id))!;

        // The journal is bound one revision ahead of the row it names, which is a row that cannot be
        // the one this launch produced - a launch only ever moves a row forward.
        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await ReconcileAsync(
            store,
            committed with { Revision = committed.Revision + 1 },
            launched.Revision,
            GrimoireOfflineTransitionTerminalDisposition.Completed,
            CovenantResetPhaseMachine.Last);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.RevisionMismatch, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        LongRunningOperation after = (await store.GetAsync(launched.Id))!;

        Assert.Equal(LongRunningOperationState.Running, after.State);

        Assert.Null(after.CompletedAt);

    }

    /// <summary>
    /// A pre-effect failure records its own terminal code on the real row, so an operator can tell a
    /// transition that never touched storage from one that stopped part way through.
    /// </summary>
    [SkippableFact]
    public async Task A_pre_effect_failure_records_its_own_terminal_code_on_the_real_row()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store();

        LongRunningOperation launched = await LaunchAsync(store);

        LongRunningOperation committed = (await store.GetAsync(launched.Id))!;

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await ReconcileAsync(
            store,
            committed,
            launched.Revision,
            GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect,
            CovenantResetPhaseMachine.First);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.Terminalized, reconciled.Outcome);

        LongRunningOperation terminal = (await store.GetAsync(launched.Id))!;

        Assert.Equal(LongRunningOperationState.Failed, terminal.State);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseReconciler.PreEffectFailureCode,
            terminal.TerminalErrorCode);

    }

    private LongRunningOperationStore Store() => new(_db!, TestOrdinaryConnectionFactory.For(_db!));

    /// <summary>
    /// Starts the operation and commits its launch checkpoint the way a coordinator would.
    /// </summary>
    private async Task<LongRunningOperation> LaunchAsync(LongRunningOperationStore store)
    {

        string owner = "offline-transition:" + Guid.NewGuid().ToString("N");

        LongRunningOperation started = (await store.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Covenant memory reset",
                _time.GetUtcNow()),
            owner,
            _time.GetUtcNow(),
            _time.GetUtcNow().AddMinutes(5)))!;

        Assert.NotNull(started);

        Assert.True(
            await store.SaveCheckpointAsync(
                started.Id,
                owner,
                expectedCheckpointVersion: 0,
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                CovenantRecoveryCheckpointCodec.Encode(Launch(started.Id, started.Revision)),
                CovenantResetCheckpointInitiator.CheckpointReference(
                    LongRunningOperationKinds.DataRetentionMutation,
                    started.Id),
                started.PublicSummary,
                _time.GetUtcNow()));

        return started;

    }

    private async Task<GrimoireOfflineTransitionDatabaseReconciliation> ReconcileAsync(
        LongRunningOperationStore store,
        LongRunningOperation committed,
        long launchRevision,
        GrimoireOfflineTransitionTerminalDisposition disposition,
        CovenantResetPhase lastCompletedPhase)
    {

        GrimoireOfflineTransitionBinding binding = GrimoireOfflineTransitionLaunch.JournalBinding(
            GrimoireOfflineTransitionLaunch.FromLaunch(Launch(committed.Id, launchRevision)).Value,
            slotEpoch: 1,
            payloadVersion: 1,
            committed.Revision,
            parentReceiptBindingDigest: null).Value;

        return await new GrimoireOfflineTransitionDatabaseReconciler(store, _time).ReconcileAsync(
            new CovenantResetOfflineTransitionPayloadV1(
                binding,
                new GrimoireOfflineTransitionLifecycle(
                    GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                    GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
                    new GrimoireOfflineTransitionClosingEvidence(true, true, true, true, true, Source),
                    new GrimoireOfflineTransitionVerificationEvidence(true, true, true),
                    new GrimoireOfflineTransitionReconciliationEvidence(
                        GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
                        DatabaseTerminalWinnerDigest: null,
                        ParentReceiptNotRequired: false,
                        ParentReceiptDigest: null,
                        LaneClosed: false,
                        CovenantDispositionIntent: null),
                    Blocker: null),
                lastCompletedPhase,
                InFlightPhase: null,
                InFlightBeforeState: null,
                ReplacementEvidence: null),
            disposition,
            CancellationToken.None);

    }

    private static CovenantOfflineTransitionLaunchV4 Launch(Guid operationId, long startingRevision) =>
        new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            operationId,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            Effect,
            Source,
            Target,
            new CovenantOfflineTransitionEpochsV1(11, 22, 33),
            new CovenantOfflineTransitionEpochsV1(12, 23, 34),
            startingRevision);

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

}
