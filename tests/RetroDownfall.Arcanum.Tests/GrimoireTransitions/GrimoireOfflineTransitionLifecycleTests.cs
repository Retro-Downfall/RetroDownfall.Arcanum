using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionLifecycleTests
{

    private static readonly Guid Operation =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void Shared_states_and_terminal_intents_are_literal_and_nonzero()
    {

        Assert.Equal(
            (byte[])[1, 2, 3, 4, 5, 6, 7, 8],
            Enum.GetValues<GrimoireOfflineTransitionState>()
                .Select(static value => (byte)value));

        Assert.Equal(
            (byte[])[1, 2, 3],
            Enum.GetValues<GrimoireOfflineTransitionTerminalIntent>()
                .Select(static value => (byte)value));

    }

    [Fact]
    public void Closed_graph_accepts_every_legal_edge_and_refuses_every_other_pair()
    {

        HashSet<(GrimoireOfflineTransitionState From, GrimoireOfflineTransitionState To)> legal =
        [
            (GrimoireOfflineTransitionState.Prepared, GrimoireOfflineTransitionState.Closing),
            (GrimoireOfflineTransitionState.Closing, GrimoireOfflineTransitionState.Closing),
            (GrimoireOfflineTransitionState.Closing, GrimoireOfflineTransitionState.Applying),
            (GrimoireOfflineTransitionState.Closing, GrimoireOfflineTransitionState.ReopenPrepared),
            (GrimoireOfflineTransitionState.Closing, GrimoireOfflineTransitionState.KeepClosed),
            (GrimoireOfflineTransitionState.Applying, GrimoireOfflineTransitionState.Applying),
            (GrimoireOfflineTransitionState.Applying, GrimoireOfflineTransitionState.ReopenPrepared),
            (GrimoireOfflineTransitionState.Applying, GrimoireOfflineTransitionState.KeepClosed),
            (GrimoireOfflineTransitionState.ReopenPrepared, GrimoireOfflineTransitionState.Verifying),
            (GrimoireOfflineTransitionState.ReopenPrepared, GrimoireOfflineTransitionState.KeepClosed),
            (GrimoireOfflineTransitionState.Verifying, GrimoireOfflineTransitionState.Verifying),
            (GrimoireOfflineTransitionState.Verifying, GrimoireOfflineTransitionState.DatabaseReconciliationPending),
            (GrimoireOfflineTransitionState.Verifying, GrimoireOfflineTransitionState.KeepClosed),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending, GrimoireOfflineTransitionState.DatabaseReconciliationPending),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending, GrimoireOfflineTransitionState.KeepClosed),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending, GrimoireOfflineTransitionState.RetirementPending),
            (GrimoireOfflineTransitionState.KeepClosed, GrimoireOfflineTransitionState.Closing),
            (GrimoireOfflineTransitionState.KeepClosed, GrimoireOfflineTransitionState.Applying),
            (GrimoireOfflineTransitionState.KeepClosed, GrimoireOfflineTransitionState.ReopenPrepared),
            (GrimoireOfflineTransitionState.KeepClosed, GrimoireOfflineTransitionState.Verifying),
            (GrimoireOfflineTransitionState.KeepClosed, GrimoireOfflineTransitionState.DatabaseReconciliationPending),
        ];

        foreach (GrimoireOfflineTransitionState from in Enum.GetValues<GrimoireOfflineTransitionState>())
        {

            foreach (GrimoireOfflineTransitionState to in Enum.GetValues<GrimoireOfflineTransitionState>())
            {

                CovenantResetOfflineTransitionPayloadV1 current = PayloadForEdge(from, to, current: true);

                CovenantResetOfflineTransitionPayloadV1 next = PayloadForEdge(from, to, current: false);

                bool accepted = Handler().ValidateAdvance(current, next).IsSuccess;

                Assert.True(
                    legal.Contains((from, to)) == accepted,
                    $"{from} -> {to}: expected {legal.Contains((from, to))}, actual {accepted}; "
                    + $"current-valid={GrimoireOfflineTransitionLifecycleValidator.ValidPayload(current)}, "
                    + $"next-valid={GrimoireOfflineTransitionLifecycleValidator.ValidPayload(next)}.");

            }

        }

    }

    [Fact]
    public void Lifecycle_refuses_binding_changes_and_terminal_intent_changes_off_designated_edges()
    {

        CovenantResetOfflineTransitionPayloadV1 current = Payload(
            GrimoireOfflineTransitionState.Applying,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            inFlight: CovenantResetPhase.CanonicalApplied);

        CovenantResetOfflineTransitionPayloadV1 changedBinding = current with
        {
            Binding = current.Binding with { EffectDigest = Digest(0x91) },
        };

        CovenantResetOfflineTransitionPayloadV1 changedIntent = current with
        {
            Lifecycle = current.Lifecycle with
            {
                TerminalIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            },
        };

        Assert.True(Handler().ValidateAdvance(current, changedBinding).IsFailure);

        Assert.True(Handler().ValidateAdvance(current, changedIntent).IsFailure);

        CovenantResetOfflineTransitionPayloadV1 completed = current with
        {
            LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            InFlightPhase = null,
            InFlightBeforeState = null,
        };

        CovenantResetOfflineTransitionPayloadV1 reopen = completed with
        {
            Lifecycle = completed.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.ReopenPrepared,
                TerminalIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            },
        };

        Assert.True(Handler().ValidateAdvance(completed, reopen).IsSuccess);

        Assert.True(Handler().ValidateAdvance(
            reopen,
            reopen with
            {
                Lifecycle = reopen.Lifecycle with
                {
                    TerminalIntent = GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
                },
            }).IsFailure);

    }

    [Fact]
    public void Same_state_evidence_must_advance_monotonically()
    {

        CovenantResetOfflineTransitionPayloadV1 closing = Payload(
            GrimoireOfflineTransitionState.Closing) with
        {
            Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Closing) with
            {
                ClosingEvidence = new(true, false, false, false, false, null),
            },
        };

        CovenantResetOfflineTransitionPayloadV1 advanced = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                ClosingEvidence = new(true, true, false, false, false, null),
            },
        };

        Assert.True(Handler().ValidateAdvance(closing, advanced).IsSuccess);

        Assert.True(Handler().ValidateAdvance(advanced, closing).IsFailure);

        Assert.True(Handler().ValidateAdvance(advanced, advanced).IsFailure);

    }

    [Fact]
    public void Keep_closed_resumes_only_exact_recorded_state_after_bound_resolution()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying,
            inFlight: CovenantResetPhase.CanonicalApplied);

        GrimoireOfflineTransitionBlocker blocker = new(
            "Covenant.ManualRecoveryRequired",
            GrimoireOfflineTransitionState.Applying,
            Digest(0x71),
            ResolutionProved: false);

        CovenantResetOfflineTransitionPayloadV1 kept = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = blocker,
            },
        };

        Assert.True(Handler().ValidateAdvance(applying, kept).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1 resumed = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                Blocker = blocker with { ResolutionProved = true },
            },
        };

        Assert.True(Handler().ValidateAdvance(kept, resumed).IsSuccess);

        Assert.True(Handler().ValidateAdvance(kept, resumed with
        {
            Lifecycle = resumed.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Verifying,
            },
        }).IsFailure);

        Assert.True(Handler().ValidateAdvance(kept, resumed with
        {
            InFlightBeforeState = new(Digest(0x42), Digest(0x43)),
        }).IsFailure);

        Assert.True(Handler().ValidateAdvance(kept, resumed with
        {
            Lifecycle = resumed.Lifecycle with
            {
                Blocker = blocker,
            },
        }).IsFailure);

    }

    [Fact]
    public void Handler_exposes_only_typed_lifecycle_outcomes()
    {

        Assert.Equal(
            GrimoireOfflineTransitionHandlerOutcome.NotApplied,
            Handler().ResolveOutcome(Payload(GrimoireOfflineTransitionState.Applying)));

        Assert.Equal(
            GrimoireOfflineTransitionHandlerOutcome.AppliedAndVerified,
            Handler().ResolveOutcome(Payload(GrimoireOfflineTransitionState.Verifying) with
            {
                Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Verifying) with
                {
                    VerificationEvidence = new(true, true, true),
                },
            }));

        Assert.Equal(
            GrimoireOfflineTransitionHandlerOutcome.ReconciliationPending,
            Handler().ResolveOutcome(Payload(
                GrimoireOfflineTransitionState.DatabaseReconciliationPending)));

        Assert.Equal(
            GrimoireOfflineTransitionHandlerOutcome.KeepClosed,
            Handler().ResolveOutcome(Payload(GrimoireOfflineTransitionState.KeepClosed) with
            {
                Lifecycle = Lifecycle(GrimoireOfflineTransitionState.KeepClosed) with
                {
                    Blocker = new(
                        "Covenant.ManualRecoveryRequired",
                        GrimoireOfflineTransitionState.Applying,
                        Digest(0x72),
                        ResolutionProved: false),
                },
            }));

        Assert.Equal(
            (byte[])[1, 2, 3, 4],
            Enum.GetValues<GrimoireOfflineTransitionHandlerOutcome>()
                .Select(static value => (byte)value));

    }

    private static CovenantResetOfflineTransitionHandlerV1 Handler() => new();

    private static CovenantResetOfflineTransitionPayloadV1 PayloadForEdge(
        GrimoireOfflineTransitionState from,
        GrimoireOfflineTransitionState to,
        bool current)
    {

        GrimoireOfflineTransitionState state = current ? from : to;

        GrimoireOfflineTransitionTerminalIntent intent =
            from is GrimoireOfflineTransitionState.Closing
                && to is GrimoireOfflineTransitionState.ReopenPrepared
                ? current
                    ? GrimoireOfflineTransitionTerminalIntent.Undecided
                    : GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen
                : from is GrimoireOfflineTransitionState.Applying
                    && to is GrimoireOfflineTransitionState.ReopenPrepared
                    ? current
                        ? GrimoireOfflineTransitionTerminalIntent.Undecided
                        : GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                    : GrimoireOfflineTransitionTerminalIntent.Undecided;

        CovenantResetOfflineTransitionPayloadV1 payload = Payload(state, intent);

        if (intent is GrimoireOfflineTransitionTerminalIntent.Undecided
            && state is GrimoireOfflineTransitionState.ReopenPrepared
                or GrimoireOfflineTransitionState.Verifying
                or GrimoireOfflineTransitionState.DatabaseReconciliationPending
                or GrimoireOfflineTransitionState.RetirementPending)
        {

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    TerminalIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
                },
            };

        }

        if (state is GrimoireOfflineTransitionState.KeepClosed)
        {

            GrimoireOfflineTransitionState resumeState = current ? to : from;

            if (resumeState is GrimoireOfflineTransitionState.ReopenPrepared
                or GrimoireOfflineTransitionState.Verifying
                or GrimoireOfflineTransitionState.DatabaseReconciliationPending)
            {

                payload = payload with
                {
                    Lifecycle = payload.Lifecycle with
                    {
                        TerminalIntent = GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
                    },
                };

            }

        }

        if (current
            && from is GrimoireOfflineTransitionState.Closing
            && to is not GrimoireOfflineTransitionState.Closing)
        {

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    ClosingEvidence = new(
                        true,
                        true,
                        true,
                        true,
                        true,
                        Guid.Parse("22222222-2222-4222-8222-222222222222")),
                },
            };

        }

        if (current
            && from is GrimoireOfflineTransitionState.Verifying
            && to is not GrimoireOfflineTransitionState.Verifying)
        {

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    VerificationEvidence = new(true, true, true),
                },
            };

        }

        if (current
            && from is GrimoireOfflineTransitionState.DatabaseReconciliationPending
            && to is GrimoireOfflineTransitionState.RetirementPending)
        {

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    ReconciliationEvidence = Reconciliation(
                        GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified),
                },
            };

        }

        if (current
            && from is GrimoireOfflineTransitionState.Applying
            && to is GrimoireOfflineTransitionState.ReopenPrepared)
        {

            payload = payload with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
                InFlightPhase = null,
                InFlightBeforeState = null,
            };

        }

        if (from is GrimoireOfflineTransitionState.KeepClosed)
        {

            GrimoireOfflineTransitionBlocker blocker = new(
                "Covenant.ManualRecoveryRequired",
                to,
                Digest(0x73),
                ResolutionProved: !current);

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with { Blocker = blocker },
            };

        }

        if (!current && to is GrimoireOfflineTransitionState.KeepClosed)
        {

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    Blocker = new(
                        "Covenant.ManualRecoveryRequired",
                        from,
                        Digest(0x74),
                        ResolutionProved: false),
                },
            };

        }

        if (from == to)
        {

            payload = AdvanceSameStateEvidence(payload, current);

        }

        return payload;

    }

    private static CovenantResetOfflineTransitionPayloadV1 AdvanceSameStateEvidence(
        CovenantResetOfflineTransitionPayloadV1 payload,
        bool current) => payload.Lifecycle.State switch
        {
            GrimoireOfflineTransitionState.Closing => payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    ClosingEvidence = current
                        ? new(true, false, false, false, false, null)
                        : new(true, true, false, false, false, null),
                },
            },
            GrimoireOfflineTransitionState.Applying => payload with
            {
                LastCompletedPhase = current
                    ? CovenantResetPhase.InventoryPrepared
                    : CovenantResetPhase.CanonicalApplied,
            },
            GrimoireOfflineTransitionState.Verifying => payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    VerificationEvidence = current
                        ? new(true, false, false)
                        : new(true, true, false),
                },
            },
            GrimoireOfflineTransitionState.DatabaseReconciliationPending => payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    ReconciliationEvidence = current
                        ? Reconciliation(GrimoireOfflineTransitionReconciliationStep.CandidateVerified)
                        : Reconciliation(GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner),
                },
            },
            _ => payload,
        };

    private static CovenantResetOfflineTransitionPayloadV1 Payload(
        GrimoireOfflineTransitionState state,
        GrimoireOfflineTransitionTerminalIntent intent =
            GrimoireOfflineTransitionTerminalIntent.Undecided,
        CovenantResetPhase? inFlight = null) => new(
            Binding(),
            Lifecycle(state, intent),
            CovenantResetPhase.InventoryPrepared,
            inFlight,
            inFlight is null ? null : new(Digest(0x31), Digest(0x32)),
            ReplacementEvidence: null);

    private static GrimoireOfflineTransitionLifecycle Lifecycle(
        GrimoireOfflineTransitionState state,
        GrimoireOfflineTransitionTerminalIntent intent =
            GrimoireOfflineTransitionTerminalIntent.Undecided)
    {

        GrimoireOfflineTransitionClosingEvidence closing = state switch
        {
            GrimoireOfflineTransitionState.Prepared
                or GrimoireOfflineTransitionState.Closing =>
                new(false, false, false, false, false, null),
            _ => new(true, true, true, true, true, Guid.Parse("22222222-2222-4222-8222-222222222222")),
        };

        GrimoireOfflineTransitionVerificationEvidence verifying = state switch
        {
            GrimoireOfflineTransitionState.Verifying => new(false, false, false),
            GrimoireOfflineTransitionState.DatabaseReconciliationPending
                or GrimoireOfflineTransitionState.RetirementPending => new(true, true, true),
            _ => new(false, false, false),
        };

        GrimoireOfflineTransitionReconciliationEvidence? reconciliation = state switch
        {
            GrimoireOfflineTransitionState.DatabaseReconciliationPending =>
                Reconciliation(GrimoireOfflineTransitionReconciliationStep.CandidateVerified),
            GrimoireOfflineTransitionState.RetirementPending =>
                Reconciliation(GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified),
            _ => null,
        };

        return new(state, intent, closing, verifying, reconciliation, Blocker: null);

    }

    private static GrimoireOfflineTransitionBinding Binding() => new(
        Operation,
        GrimoireOfflineTransitionKind.CovenantReset,
        PayloadVersion: 1,
        SlotEpoch: 5,
        Digest(0x11),
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        Guid.Parse("44444444-4444-4444-8444-444444444444"),
        new(10, 20, 30),
        new(11, 21, 31),
        Digest(0x12),
        ExpectedDatabaseOperationRevision: 7,
        ParentReceiptBindingDigest: null);

    private static GrimoireOfflineTransitionReconciliationEvidence Reconciliation(
        GrimoireOfflineTransitionReconciliationStep step) => new(
            step,
            step >= GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner
                ? Digest(0x51)
                : null,
            ParentReceiptNotRequired: true,
            ParentReceiptDigest: null,
            LaneClosed: step >= GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            CovenantDispositionIntent: step >= GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight
                ? GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                : null);

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

}
