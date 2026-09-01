using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionLifecycleStoreTests : IDisposable
{

    private static readonly Guid Installation =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Operation =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-offline-transition-lifecycle-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly ArcanumMaintenanceLock _lock;

    public GrimoireOfflineTransitionLifecycleStoreTests()
    {

        Directory.CreateDirectory(_root);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its maintenance lock.");

        SeedIdentity();

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Typed_facade_runs_authenticated_begin_advance_recover_and_retire()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        CovenantResetOfflineTransitionPayloadV1 payload = PreparedPayload();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            payload,
            CancellationToken.None));

        Assert.Equal(1UL, current.Raw.Envelope.Revision);

        Assert.Equal(payload, current.Payload);

        int step = 0;

        foreach (CovenantResetOfflineTransitionPayloadV1 next in TerminalSequence(payload))
        {

            step++;

            Result<GrimoireOfflineTransitionTypedPublication> advanced =
                await lifecycle.AdvanceAsync(
                _lock,
                current,
                next,
                CancellationToken.None);

            Assert.True(
                advanced.IsSuccess,
                $"Step {step} ({current.Payload.Lifecycle.State} -> {next.Lifecycle.State}): "
                + advanced.Error.Code + ":" + advanced.Error.Message);

            current = advanced.Value;

        }

        Assert.Equal(GrimoireOfflineTransitionState.RetirementPending,
            current.Payload.Lifecycle.State);

        GrimoireOfflineTransitionLifecycleStore recovering = LifecycleStore();

        GrimoireOfflineTransitionTypedRecoveryState recovered = Value(
            await recovering.RecoverAsync(_lock, _guarded, CancellationToken.None));

        Assert.Equal(
            GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated,
            recovered.Outcome);

        Assert.Equal(current.Payload, recovered.Publication!.Payload);

        Assert.True((await recovering.RetireAsync(
            _lock,
            recovered.Publication,
            CancellationToken.None)).IsSuccess);

        GrimoireOfflineTransitionTypedRecoveryState absent = Value(
            await recovering.RecoverAsync(_lock, _guarded, CancellationToken.None));

        Assert.Equal(GrimoireOfflineTransitionTypedRecoveryOutcome.NoActiveJournal, absent.Outcome);

    }

    [Fact]
    public async Task Illegal_typed_edge_is_refused_before_raw_revision_publication()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            PreparedPayload(),
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 skipped = PreparedPayload() with
        {
            Lifecycle = PreparedPayload().Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
            },
        };

        Result<GrimoireOfflineTransitionTypedPublication> result = await lifecycle.AdvanceAsync(
            _lock,
            current,
            skipped,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        GrimoireOfflineTransitionJournalRecoveryState raw = Value(
            await RawStore().RecoverAsync(_lock, _guarded, CancellationToken.None));

        Assert.Equal(1UL, raw.Publication!.Envelope.Revision);

        Assert.Equal(current.Raw.PayloadBytes, raw.Publication.PayloadBytes);

    }

    [Fact]
    public async Task Authenticated_opaque_payload_is_decoded_and_validated_before_recovery_use()
    {

        GrimoireOfflineTransitionJournalPublication raw = Value(await RawStore().BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            Encoding.UTF8.GetBytes("opaque-but-authenticated"),
            CancellationToken.None));

        Assert.Equal(1UL, raw.Envelope.Revision);

        Result<GrimoireOfflineTransitionTypedRecoveryState> recovered =
            await LifecycleStore().RecoverAsync(_lock, _guarded, CancellationToken.None);

        Assert.True(recovered.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

    }

    [Fact]
    public async Task Facade_refuses_retirement_before_typed_retirement_readiness()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            PreparedPayload(),
            CancellationToken.None));

        Assert.True((await lifecycle.RetireAsync(
            _lock,
            current,
            CancellationToken.None)).IsFailure);

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated,
            Value(await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Outcome);

    }

    [Fact]
    public async Task Publication_carries_no_handler_and_registry_handler_refuses_an_illegal_edge()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            PreparedPayload(),
            CancellationToken.None));

        Assert.DoesNotContain(
            typeof(GrimoireOfflineTransitionTypedPublication).GetProperties(),
            static property => property.PropertyType
                == typeof(IGrimoireOfflineTransitionHandler));

        CovenantResetOfflineTransitionPayloadV1 skipped = PreparedPayload() with
        {
            Lifecycle = PreparedPayload().Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
            },
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            skipped,
            CancellationToken.None)).IsFailure);

        Assert.Equal(1UL, Value(await RawStore().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None)).Publication!.Envelope.Revision);

    }

    [Fact]
    public async Task Wrong_slot_epoch_typed_begin_leaves_no_active_journal()
    {

        CovenantResetOfflineTransitionPayloadV1 stale = PreparedPayload() with
        {
            Binding = PreparedPayload().Binding with { SlotEpoch = 99 },
        };

        Assert.True((await LifecycleStore().BeginAsync(
            _lock,
            _guarded,
            Installation,
            stale,
            CancellationToken.None)).IsFailure);

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
            Value(await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Outcome);

    }

    private IEnumerable<CovenantResetOfflineTransitionPayloadV1> TerminalSequence(
        CovenantResetOfflineTransitionPayloadV1 prepared)
    {

        CovenantResetOfflineTransitionPayloadV1 closing = prepared with
        {
            Lifecycle = prepared.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Closing,
            },
        };

        yield return closing;

        closing = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                ClosingEvidence = new(
                    true,
                    true,
                    true,
                    true,
                    true,
                    prepared.Binding.SourceDatasetGeneration),
            },
        };

        yield return closing;

        CovenantResetOfflineTransitionPayloadV1 applying = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
            },
        };

        yield return applying;

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered
            .Skip(1)
            .TakeWhile(static phase => phase <= CovenantResetPhase.SidecarsVerified))
        {

            applying = applying with
            {
                InFlightPhase = phase,
                InFlightBeforeState = new(
                    Digest((byte)(0x60 + (byte)phase)),
                    Digest(0x70)),
            };

            yield return applying;

            applying = applying with
            {
                LastCompletedPhase = phase,
                InFlightPhase = null,
                InFlightBeforeState = null,
            };

            yield return applying;

        }

        CovenantResetOfflineTransitionPayloadV1 reopen = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.ReopenPrepared,
                TerminalIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            },
        };

        yield return reopen;

        CovenantResetOfflineTransitionPayloadV1 verifying = reopen with
        {
            Lifecycle = reopen.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Verifying,
            },
        };

        yield return verifying;

        verifying = verifying with
        {
            Lifecycle = verifying.Lifecycle with
            {
                VerificationEvidence = new(true, true, true),
            },
        };

        yield return verifying;

        GrimoireOfflineTransitionReconciliationEvidence suffix = new(
            GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
            DatabaseTerminalWinnerDigest: null,
            ParentReceiptNotRequired: false,
            ParentReceiptDigest: null,
            LaneClosed: false,
            CovenantDispositionIntent: null);

        CovenantResetOfflineTransitionPayloadV1 reconciling = verifying with
        {
            Lifecycle = verifying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                ReconciliationEvidence = suffix,
            },
        };

        yield return reconciling;

        suffix = suffix with
        {
            Step = GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner,
            DatabaseTerminalWinnerDigest = Digest(0x51),
        };

        yield return reconciling = reconciling with
        {
            Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = suffix },
        };

        suffix = suffix with
        {
            Step = GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
            ParentReceiptNotRequired = true,
        };

        yield return reconciling = reconciling with
        {
            Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = suffix },
        };

        suffix = suffix with
        {
            Step = GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            LaneClosed = true,
        };

        yield return reconciling = reconciling with
        {
            Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = suffix },
        };

        suffix = suffix with
        {
            Step = GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight,
            CovenantDispositionIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
        };

        yield return reconciling = reconciling with
        {
            Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = suffix },
        };

        suffix = suffix with
        {
            Step = GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
        };

        yield return reconciling = reconciling with
        {
            Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = suffix },
        };

        yield return reconciling with
        {
            Lifecycle = reconciling.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.RetirementPending,
            },
        };

    }

    private CovenantResetOfflineTransitionPayloadV1 PreparedPayload() => new(
        new(
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion: 1,
            SlotEpoch: 1,
            Digest(0x11),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            new(1, 2, 3),
            new(2, 3, 4),
            Digest(0x12),
            ExpectedDatabaseOperationRevision: 4,
            ParentReceiptBindingDigest: null),
        new(
            GrimoireOfflineTransitionState.Prepared,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            new(false, false, false, false, false, null),
            new(false, false, false),
            ReconciliationEvidence: null,
            Blocker: null),
        CovenantResetPhase.InventoryPrepared,
        InFlightPhase: null,
        InFlightBeforeState: null,
        ReplacementEvidence: null);

    private GrimoireOfflineTransitionLifecycleStore LifecycleStore() => new(
        RawStore(),
        GrimoireOfflineTransitionHandlerRegistry.Production);

    private GrimoireOfflineTransitionJournalStore RawStore() => new(_credentials);

    private void SeedIdentity()
    {

        GrimoireOfflineTransitionJournalLocation location = Value(
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guarded));

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        Assert.Equal(
            Installation,
            Value(identities.SeedFromDatabase(
                _lock,
                _guarded,
                location.ProfileNamespace,
                Installation)));

    }

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Code + ":" + result.Error.Message);

        return result.Value;

    }

}
