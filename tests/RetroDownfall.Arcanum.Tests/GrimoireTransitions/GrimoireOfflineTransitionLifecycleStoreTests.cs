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
    public async Task Keep_closed_can_be_parked_recovered_and_resumed_through_the_store()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current = await BeginApplyingAsync(lifecycle);

        Assert.Equal(4UL, current.Raw.Envelope.Revision);

        CovenantResetOfflineTransitionPayloadV1 applying =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        GrimoireOfflineTransitionBlocker blocker = new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            GrimoireOfflineTransitionState.Applying,
            Digest(0x71),
            Digest(0x71));

        CovenantResetOfflineTransitionPayloadV1 kept = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = blocker,
            },
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            kept,
            CancellationToken.None));

        Assert.Equal(5UL, current.Raw.Envelope.Revision);

        Assert.Equal(kept, current.Payload);

        GrimoireOfflineTransitionJournalPublication rawAfterPark = Value(
            await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Publication!;

        Assert.Equal(5UL, rawAfterPark.Envelope.Revision);

        GrimoireOfflineTransitionLifecycleStore recovering = LifecycleStore();

        GrimoireOfflineTransitionTypedRecoveryState recovered = Value(
            await recovering.RecoverAsync(_lock, _guarded, CancellationToken.None));

        Assert.Equal(
            GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated,
            recovered.Outcome);

        Assert.Equal(5UL, recovered.Publication!.Raw.Envelope.Revision);

        Assert.Equal(kept, recovered.Publication.Payload);

        // The blocker's ResolutionBindingDigest and ExpectedStateDigest are both Digest(0x71)
        // in this fixture, so the resume evidence's CanonicalStateDigest legitimately matches
        // ExpectedStateDigest - the field acceptance actually turns on - even though it is written as the
        // same literal as the binding digest below - the two fields are equal here by fixture
        // choice, not because the comparison still accepts either one.
        CovenantResetOfflineTransitionPayloadV1 resumed = applying with
        {
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x71)),
        };

        GrimoireOfflineTransitionTypedPublication afterResume = Value(
            await recovering.AdvanceAsync(
                _lock,
                recovered.Publication,
                resumed,
                CancellationToken.None));

        Assert.Equal(6UL, afterResume.Raw.Envelope.Revision);

        Assert.Equal(resumed, afterResume.Payload);

        GrimoireOfflineTransitionJournalPublication rawAfterResume = Value(
            await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Publication!;

        Assert.Equal(6UL, rawAfterResume.Envelope.Revision);

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
    public void Encoding_and_decoding_a_resumed_payload_round_trips_resolution_evidence_byte_exact()
    {

        CovenantResetOfflineTransitionPayloadV1 prepared = PreparedPayload();

        CovenantResetOfflineTransitionPayloadV1 resumedReset = prepared with
        {
            Lifecycle = prepared.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
                ClosingEvidence = new(
                    true,
                    true,
                    true,
                    true,
                    true,
                    prepared.Binding.SourceDatasetGeneration),
            },
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x71)),
        };

        AssertByteExactRoundTrip(resumedReset);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 resumedFactory = new(
            prepared.Binding with
            {
                Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            },
            resumedReset.Lifecycle,
            prepared.LastCompletedPhase,
            InFlightPhase: null,
            InFlightBeforeState: null,
            ReplacementEvidence: null,
            OrdinaryFactoryContinuationCompleted: false,
            BlockerResolutionEvidence: new(Digest(0x71), Digest(0x71)));

        AssertByteExactRoundTrip(resumedFactory);

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
    public async Task Begin_with_a_null_lifecycle_is_refused_without_throwing()
    {

        CovenantResetOfflineTransitionPayloadV1 malformed = PreparedPayload() with
        {
            Lifecycle = null!,
        };

        Result<GrimoireOfflineTransitionTypedPublication> begun = await LifecycleStore().BeginAsync(
            _lock,
            _guarded,
            Installation,
            malformed,
            CancellationToken.None);

        Assert.True(begun.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, begun.Error.Code);

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
            Value(await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Outcome);

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

    [Fact]
    public async Task In_flight_begin_cannot_introduce_replacement_evidence_or_publish_raw_revision()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current =
            await BeginApplyingAsync(lifecycle);

        CovenantResetOfflineTransitionPayloadV1 applying =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        CovenantResetOfflineTransitionPayloadV1 invalid = applying with
        {
            InFlightPhase = CovenantResetPhase.CanonicalApplied,
            InFlightBeforeState = new(Digest(0x61), Digest(0x62)),
            ReplacementEvidence = Replacement(),
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            invalid,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

    }

    [Fact]
    public async Task Replacement_base_is_refused_away_from_wal_boundary_without_raw_publication()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current =
            await BeginApplyingAsync(lifecycle);

        current = await AdvanceApplyingThroughAsync(
            lifecycle,
            current,
            CovenantResetPhase.CanonicalApplied);

        CovenantResetOfflineTransitionPayloadV1 applying =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        Assert.Equal(CovenantResetPhase.CanonicalApplied, applying.LastCompletedPhase);

        Assert.Null(applying.InFlightPhase);

        Assert.Null(applying.InFlightBeforeState);

        Assert.Null(applying.ReplacementEvidence);

        CovenantResetOfflineTransitionPayloadV1 invalid = applying with
        {
            ReplacementEvidence = ReplacementBase(),
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            invalid,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

    }

    [Fact]
    public async Task In_flight_begin_cannot_advance_replacement_evidence_or_publish_raw_revision()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current =
            await BeginApplyingAsync(lifecycle);

        current = await AdvanceApplyingThroughAsync(
            lifecycle,
            current,
            CovenantResetPhase.WalTruncated);

        CovenantResetOfflineTransitionPayloadV1 applying =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        foreach (GrimoireOfflineTransitionReplacementEvidence replacement in
            (GrimoireOfflineTransitionReplacementEvidence[])
            [
                ReplacementBase(),
                ReplacementStagingOwned(),
                ReplacementContentProved(),
            ])
        {

            applying = applying with { ReplacementEvidence = replacement };

            current = Value(await lifecycle.AdvanceAsync(
                _lock,
                current,
                applying,
                CancellationToken.None));

        }

        CovenantResetOfflineTransitionPayloadV1 invalid = applying with
        {
            InFlightPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightBeforeState = new(Digest(0x63), Digest(0x64)),
            ReplacementEvidence = applying.ReplacementEvidence! with
            {
                StagingPhysicalIdentityDigest = Digest(0x99),
            },
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            invalid,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

    }

    [Fact]
    public async Task Wrong_closed_generation_is_refused_without_raw_publication()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        CovenantResetOfflineTransitionPayloadV1 prepared = PreparedPayload();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            prepared,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 closing = prepared with
        {
            Lifecycle = prepared.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Closing,
            },
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            closing,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 wrong = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                ClosingEvidence = new(true, true, true, true, true, Guid.NewGuid()),
            },
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            wrong,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

        CovenantResetOfflineTransitionPayloadV1 matching = wrong with
        {
            Lifecycle = wrong.Lifecycle with
            {
                ClosingEvidence = wrong.Lifecycle.ClosingEvidence with
                {
                    ClosedDatasetGeneration = prepared.Binding.SourceDatasetGeneration,
                },
            },
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            matching,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Replacement_sequence_is_exact_and_refusals_leave_raw_bytes_unchanged()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        GrimoireOfflineTransitionTypedPublication current =
            await BeginApplyingAsync(lifecycle);

        current = await AdvanceApplyingThroughAsync(
            lifecycle,
            current,
            CovenantResetPhase.WalTruncated);

        CovenantResetOfflineTransitionPayloadV1 none =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        CovenantResetOfflineTransitionPayloadV1 baseEvidence = none with
        {
            ReplacementEvidence = ReplacementBase(),
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            baseEvidence,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 skipped = baseEvidence with
        {
            ReplacementEvidence = ReplacementContentProved(),
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            skipped,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

        CovenantResetOfflineTransitionPayloadV1 stagingOwned = baseEvidence with
        {
            ReplacementEvidence = ReplacementStagingOwned(),
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            stagingOwned,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 mutated = stagingOwned with
        {
            ReplacementEvidence = stagingOwned.ReplacementEvidence! with
            {
                SourcePhysicalIdentityDigest = Digest(0x99),
            },
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            mutated,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

        CovenantResetOfflineTransitionPayloadV1 contentProved = stagingOwned with
        {
            ReplacementEvidence = ReplacementContentProved(),
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            contentProved,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 partialCompacting = contentProved with
        {
            InFlightPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightBeforeState = new(Digest(0x61), Digest(0x62)),
            ReplacementEvidence = ReplacementStagingOwned(),
        };

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            partialCompacting,
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

        CovenantResetOfflineTransitionPayloadV1 compacting = contentProved with
        {
            InFlightPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightBeforeState = new(Digest(0x61), Digest(0x62)),
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            compacting,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 compacted = compacting with
        {
            LastCompletedPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightPhase = null,
            InFlightBeforeState = null,
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            compacted,
            CancellationToken.None));

        Assert.True((await lifecycle.AdvanceAsync(
            _lock,
            current,
            compacted with { ReplacementEvidence = null },
            CancellationToken.None)).IsFailure);

        await AssertRawUnchangedAsync(current);

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

    private async Task<GrimoireOfflineTransitionTypedPublication> BeginApplyingAsync(
        GrimoireOfflineTransitionLifecycleStore lifecycle)
    {

        CovenantResetOfflineTransitionPayloadV1 prepared = PreparedPayload();

        GrimoireOfflineTransitionTypedPublication current = Value(await lifecycle.BeginAsync(
            _lock,
            _guarded,
            Installation,
            prepared,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 closing = prepared with
        {
            Lifecycle = prepared.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Closing,
            },
        };

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            closing,
            CancellationToken.None));

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

        current = Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            closing,
            CancellationToken.None));

        CovenantResetOfflineTransitionPayloadV1 applying = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
            },
        };

        return Value(await lifecycle.AdvanceAsync(
            _lock,
            current,
            applying,
            CancellationToken.None));

    }

    private async Task<GrimoireOfflineTransitionTypedPublication> AdvanceApplyingThroughAsync(
        GrimoireOfflineTransitionLifecycleStore lifecycle,
        GrimoireOfflineTransitionTypedPublication current,
        CovenantResetPhase target)
    {

        CovenantResetOfflineTransitionPayloadV1 applying =
            Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(current.Payload);

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered
            .Where(phase => phase > applying.LastCompletedPhase && phase <= target))
        {

            applying = applying with
            {
                InFlightPhase = phase,
                InFlightBeforeState = new(
                    Digest((byte)(0x60 + (byte)phase)),
                    Digest(0x70)),
            };

            current = Value(await lifecycle.AdvanceAsync(
                _lock,
                current,
                applying,
                CancellationToken.None));

            applying = applying with
            {
                LastCompletedPhase = phase,
                InFlightPhase = null,
                InFlightBeforeState = null,
            };

            current = Value(await lifecycle.AdvanceAsync(
                _lock,
                current,
                applying,
                CancellationToken.None));

        }

        return current;

    }

    private async Task AssertRawUnchangedAsync(
        GrimoireOfflineTransitionTypedPublication current)
    {

        GrimoireOfflineTransitionJournalPublication raw = Value(
            await RawStore().RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Publication!;

        Assert.Equal(current.Raw.Envelope.Revision, raw.Envelope.Revision);

        Assert.Equal(current.Raw.PayloadBytes, raw.PayloadBytes);

    }

    private static GrimoireOfflineTransitionReplacementEvidence Replacement() => new(
        "staging.db",
        Digest(0x81),
        StagingPhysicalIdentityDigest: null,
        Digest(0x82),
        Digest(0x83),
        StagedContentDigest: null);

    private static GrimoireOfflineTransitionReplacementEvidence ReplacementBase() =>
        Replacement();

    private static GrimoireOfflineTransitionReplacementEvidence ReplacementStagingOwned() =>
        ReplacementBase() with { StagingPhysicalIdentityDigest = Digest(0x84) };

    private static GrimoireOfflineTransitionReplacementEvidence ReplacementContentProved() =>
        ReplacementStagingOwned() with { StagedContentDigest = Digest(0x85) };

    /// <summary>
    /// The opening payload is built against the epoch the slot actually allocated.
    /// </summary>
    /// <remarks>
    /// The epoch is the successor of whatever closed anchor the raw store finds, and only the raw
    /// store is in a position to know it. A caller that computed the number itself would be holding a
    /// second copy of that arithmetic, correct right up until the first time the two disagreed — and
    /// the disagreement would surface as a refused publication on an installation that had done
    /// nothing wrong.
    /// </remarks>
    [Fact]
    public async Task Bound_begin_hands_the_allocated_slot_epoch_to_the_payload_factory()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        ulong observed = 0;

        GrimoireOfflineTransitionTypedPublication opened = Value(await lifecycle.BeginBoundAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            slotEpoch =>
            {

                observed = slotEpoch;

                return Result<IGrimoireOfflineTransitionPayload>.Success(
                    PreparedPayload() with
                    {
                        Binding = PreparedPayload().Binding with { SlotEpoch = slotEpoch },
                    });

            },
            CancellationToken.None));

        Assert.Equal(1UL, observed);

        Assert.Equal(1UL, opened.Raw.Envelope.SlotEpoch);

        Assert.Equal(1UL, opened.Raw.Envelope.Revision);

    }

    /// <summary>
    /// A factory that ignores the epoch it was handed publishes nothing at all.
    /// </summary>
    /// <remarks>
    /// The refusal has to land before the canonical file exists, because an active anchor with no
    /// file behind it is the one shape recovery cannot tell from a deletion and therefore has to fail
    /// closed on forever. Refusing at the encode leaves the slot exactly as it was found.
    /// </remarks>
    [Fact]
    public async Task Bound_begin_refuses_a_payload_bound_to_a_different_epoch_and_leaves_no_journal()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        Result<GrimoireOfflineTransitionTypedPublication> opened = await lifecycle.BeginBoundAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            _ => Result<IGrimoireOfflineTransitionPayload>.Success(
                PreparedPayload() with
                {
                    Binding = PreparedPayload().Binding with { SlotEpoch = 99 },
                }),
            CancellationToken.None);

        Assert.True(opened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, opened.Error.Code);

        Assert.False(
            File.Exists(
                Value(new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guarded)).JournalPath));

    }

    /// <summary>
    /// A slot cannot be opened part-way through a lifecycle it has not started.
    /// </summary>
    /// <remarks>
    /// The opening publication is the one revision with no predecessor to be checked against, so
    /// every later edge is only as trustworthy as the state this one established. A factory that
    /// returned an <c>Applying</c> payload would mint a journal claiming phases had run that nothing
    /// had published, and the graph would accept it because the graph only ever compares a revision
    /// against the one before it.
    /// </remarks>
    [Fact]
    public async Task Bound_begin_refuses_an_opening_payload_that_is_not_prepared()
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = LifecycleStore();

        foreach (GrimoireOfflineTransitionState state in Enum.GetValues<GrimoireOfflineTransitionState>())
        {

            if (state is GrimoireOfflineTransitionState.Prepared)
            {

                continue;

            }

            await AssertOpeningRefusedAsync(lifecycle, state);

        }

    }

    private async Task AssertOpeningRefusedAsync(
        GrimoireOfflineTransitionLifecycleStore lifecycle,
        GrimoireOfflineTransitionState state)
    {

        Result<GrimoireOfflineTransitionTypedPublication> opened = await lifecycle.BeginBoundAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            slotEpoch => Result<IGrimoireOfflineTransitionPayload>.Success(
                PreparedPayload() with
                {
                    Binding = PreparedPayload().Binding with { SlotEpoch = slotEpoch },
                    Lifecycle = PreparedPayload().Lifecycle with { State = state },
                }),
            CancellationToken.None);

        Assert.True(opened.IsFailure, state.ToString());

        Assert.False(
            File.Exists(
                Value(new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guarded)).JournalPath));

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

    private static void AssertByteExactRoundTrip<TPayload>(TPayload payload)
        where TPayload : class, IGrimoireOfflineTransitionPayload
    {

        byte[] encoded = Value(GrimoireOfflineTransitionHandlerRegistry.Production.Encode(payload));

        GrimoireOfflineTransitionDecodedPayload decoded = Value(
            GrimoireOfflineTransitionHandlerRegistry.Production.DecodeAuthenticated(
                payload.Binding.Kind,
                payload.Binding.PayloadVersion,
                encoded,
                payload.Binding.OperationId,
                payload.Binding.SlotEpoch));

        Assert.Equal(payload, decoded.Payload);

        byte[] reEncoded = Value(GrimoireOfflineTransitionHandlerRegistry.Production.Encode(decoded.Payload));

        Assert.Equal(encoded, reEncoded);

    }

}
