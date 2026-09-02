using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionLifecycleTests
{

    private static readonly Guid Operation =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid SourceGeneration =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

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

                bool currentValid = GrimoireOfflineTransitionLifecycleValidator.ValidPayload(current);

                bool nextValid = GrimoireOfflineTransitionLifecycleValidator.ValidPayload(next);

                Assert.True(
                    currentValid && nextValid,
                    $"{from} -> {to}: current-valid={currentValid}, next-valid={nextValid}; "
                    + "a pair refused for an invalid fixture, not by the edge guard, proves "
                    + "nothing about the edge.");

                bool accepted = Handler().ValidateAdvance(current, next).IsSuccess;

                Assert.True(
                    legal.Contains((from, to)) == accepted,
                    $"{from} -> {to}: expected {legal.Contains((from, to))}, actual {accepted}.");

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
    public void Applying_completion_requires_the_exact_prior_in_flight_publication()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying);

        CovenantResetOfflineTransitionPayloadV1 direct = applying with
        {
            LastCompletedPhase = CovenantResetPhase.CanonicalApplied,
        };

        Assert.True(Handler().ValidateAdvance(applying, direct).IsFailure);

    }

    [Fact]
    public void Closing_enters_keep_closed_only_after_complete_closed_proof()
    {

        CovenantResetOfflineTransitionPayloadV1 partial = Payload(
            GrimoireOfflineTransitionState.Closing) with
        {
            Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Closing) with
            {
                ClosingEvidence = new(true, true, false, false, false, null),
            },
        };

        GrimoireOfflineTransitionBlocker blocker = new(
            "Covenant.ManualRecoveryRequired",
            GrimoireOfflineTransitionState.Closing,
            Digest(0x75));

        CovenantResetOfflineTransitionPayloadV1 partialKept = partial with
        {
            Lifecycle = partial.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = blocker,
            },
        };

        Assert.True(Handler().ValidateAdvance(partial, partialKept).IsFailure);

        CovenantResetOfflineTransitionPayloadV1 complete = partial with
        {
            Lifecycle = partial.Lifecycle with
            {
                ClosingEvidence = new(
                    true,
                    true,
                    true,
                    true,
                    true,
                    partial.Binding.SourceDatasetGeneration),
            },
        };

        CovenantResetOfflineTransitionPayloadV1 completeKept = complete with
        {
            Lifecycle = complete.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = blocker,
            },
        };

        Assert.True(Handler().ValidateAdvance(complete, completeKept).IsSuccess);

    }

    [Fact]
    public void Edges_preserve_every_unowned_evidence_family_and_recovered_shapes_are_coherent()
    {

        CovenantResetOfflineTransitionPayloadV1 closing = Payload(
            GrimoireOfflineTransitionState.Closing) with
        {
            Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Closing) with
            {
                ClosingEvidence = new(true, false, false, false, false, null),
            },
        };

        CovenantResetOfflineTransitionPayloadV1 closureAdvance = closing with
        {
            Lifecycle = closing.Lifecycle with
            {
                ClosingEvidence = new(true, true, false, false, false, null),
            },
        };

        Assert.True(Handler().ValidateAdvance(closing, closureAdvance with
        {
            LastCompletedPhase = CovenantResetPhase.CanonicalApplied,
        }).IsFailure);

        Assert.True(Handler().ValidateAdvance(closing, closureAdvance with
        {
            ReplacementEvidence = Replacement(),
        }).IsFailure);

        Assert.True(Handler().ValidateAdvance(closing, closureAdvance with
        {
            Lifecycle = closureAdvance.Lifecycle with
            {
                VerificationEvidence = new(true, false, false),
            },
        }).IsFailure);

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying,
            inFlight: CovenantResetPhase.CanonicalApplied);

        CovenantResetOfflineTransitionPayloadV1 completed = applying with
        {
            LastCompletedPhase = CovenantResetPhase.CanonicalApplied,
            InFlightPhase = null,
            InFlightBeforeState = null,
        };

        Assert.True(Handler().ValidateAdvance(applying, completed with
        {
            Lifecycle = completed.Lifecycle with
            {
                ClosingEvidence = completed.Lifecycle.ClosingEvidence with
                {
                    ClosedDatasetGeneration = Guid.NewGuid(),
                },
            },
        }).IsFailure);

        Assert.True(Handler().ValidateAdvance(applying, completed with
        {
            InFlightBeforeState = new(Digest(0x41), Digest(0x42)),
        }).IsFailure);

        CovenantResetOfflineTransitionPayloadV1 retirement = Payload(
            GrimoireOfflineTransitionState.RetirementPending,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
        {
            InFlightPhase = CovenantResetPhase.CanonicalApplied,
            InFlightBeforeState = new(Digest(0x31), Digest(0x32)),
        };

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(retirement));

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryCurrent = new(
            closing.Binding with { Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure },
            closing.Lifecycle,
            closing.LastCompletedPhase,
            closing.InFlightPhase,
            closing.InFlightBeforeState,
            closing.ReplacementEvidence,
            OrdinaryFactoryContinuationCompleted: false);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryNext = factoryCurrent with
        {
            Lifecycle = closureAdvance.Lifecycle,
            OrdinaryFactoryContinuationCompleted = true,
        };

        Assert.True(new HealthyCatalogFactoryErasureOfflineTransitionHandlerV1()
            .ValidateAdvance(factoryCurrent, factoryNext).IsFailure);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryBoundary =
            factoryCurrent with
            {
                Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Applying),
                LastCompletedPhase = CovenantResetPhase.ManagedArtifactsProcessed,
            };

        Assert.True(new HealthyCatalogFactoryErasureOfflineTransitionHandlerV1()
            .ValidateAdvance(
                factoryBoundary,
                factoryBoundary with { OrdinaryFactoryContinuationCompleted = true })
            .IsSuccess);

    }

    [Fact]
    public void Caller_asserted_blocker_resolution_and_combined_resume_mutation_are_refused()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying,
            inFlight: CovenantResetPhase.CanonicalApplied);

        GrimoireOfflineTransitionBlocker blocker = new(
            "Covenant.ManualRecoveryRequired",
            GrimoireOfflineTransitionState.Applying,
            Digest(0x71));

        CovenantResetOfflineTransitionPayloadV1 kept = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = blocker,
            },
        };

        CovenantResetOfflineTransitionPayloadV1 forged = applying with
        {
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x72)),
        };

        Assert.True(Handler().ValidateAdvance(kept, forged).IsFailure);

        Assert.True(Handler().ValidateAdvance(kept, forged with
        {
            ReplacementEvidence = Replacement(),
        }).IsFailure);

    }

    [Fact]
    public void Reconciliation_suffix_requires_exact_step_shape_parent_binding_and_terminal_intent()
    {

        CovenantResetOfflineTransitionPayloadV1 reconciling = Payload(
            GrimoireOfflineTransitionState.DatabaseReconciliationPending,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
        {
            LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
        };

        CovenantDigest parent = Digest(0x61);

        foreach (GrimoireOfflineTransitionReconciliationStep step in
            Enum.GetValues<GrimoireOfflineTransitionReconciliationStep>())
        {

            GrimoireOfflineTransitionReconciliationEvidence absent =
                ExactReconciliation(step, parent: null);

            CovenantResetOfflineTransitionPayloadV1 absentPayload = reconciling with
            {
                Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = absent },
            };

            Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(absentPayload));

            GrimoireOfflineTransitionReconciliationEvidence bound =
                ExactReconciliation(step, parent);

            CovenantResetOfflineTransitionPayloadV1 boundPayload = reconciling with
            {
                Binding = reconciling.Binding with { ParentReceiptBindingDigest = parent },
                Lifecycle = reconciling.Lifecycle with { ReconciliationEvidence = bound },
            };

            Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(boundPayload));

            GrimoireOfflineTransitionReconciliationEvidence future = step switch
            {
                GrimoireOfflineTransitionReconciliationStep.CandidateVerified =>
                    absent with { DatabaseTerminalWinnerDigest = Digest(0x51) },
                GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner =>
                    absent with { ParentReceiptNotRequired = true },
                GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied =>
                    absent with { LaneClosed = true },
                GrimoireOfflineTransitionReconciliationStep.LaneClosed =>
                    absent with
                    {
                        CovenantDispositionIntent =
                            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
                    },
                _ => absent with
                {
                    CovenantDispositionIntent =
                        GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
                },
            };

            Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
                absentPayload with
                {
                    Lifecycle = absentPayload.Lifecycle with
                    {
                        ReconciliationEvidence = future,
                    },
                }));

        }

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
            Digest(0x71));

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
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x71)),
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
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x72)),
        }).IsFailure);

    }

    [Fact]
    public void Keep_closed_can_be_entered_again_after_a_resolved_park()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying,
            inFlight: CovenantResetPhase.CanonicalApplied);

        GrimoireOfflineTransitionBlocker firstBlocker = new(
            "Covenant.ManualRecoveryRequired",
            GrimoireOfflineTransitionState.Applying,
            Digest(0x71));

        CovenantResetOfflineTransitionPayloadV1 kept = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = firstBlocker,
            },
        };

        Assert.True(Handler().ValidateAdvance(applying, kept).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1 resumed = applying with
        {
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x71)),
        };

        Assert.True(Handler().ValidateAdvance(kept, resumed).IsSuccess);

        GrimoireOfflineTransitionBlocker secondBlocker = new(
            "Covenant.ManualRecoveryRequired",
            GrimoireOfflineTransitionState.Applying,
            Digest(0x77));

        CovenantResetOfflineTransitionPayloadV1 keptAgain = resumed with
        {
            Lifecycle = resumed.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = secondBlocker,
            },
            BlockerResolutionEvidence = null,
        };

        Assert.True(Handler().ValidateAdvance(resumed, keptAgain).IsSuccess);

        HealthyCatalogFactoryErasureOfflineTransitionHandlerV1 factoryHandler = new();

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryApplying = Factory(
            applying,
            continuationCompleted: false);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryKept = factoryApplying with
        {
            Lifecycle = factoryApplying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = firstBlocker,
            },
        };

        Assert.True(factoryHandler.ValidateAdvance(factoryApplying, factoryKept).IsSuccess);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryResumed = factoryApplying with
        {
            BlockerResolutionEvidence = new(Digest(0x71), Digest(0x71)),
        };

        Assert.True(factoryHandler.ValidateAdvance(factoryKept, factoryResumed).IsSuccess);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factoryKeptAgain = factoryResumed with
        {
            Lifecycle = factoryResumed.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = secondBlocker,
            },
            BlockerResolutionEvidence = null,
        };

        Assert.True(factoryHandler.ValidateAdvance(factoryResumed, factoryKeptAgain).IsSuccess);

    }

    [Fact]
    public void Factory_continuation_is_false_until_its_single_boundary_publication_then_true()
    {

        HealthyCatalogFactoryErasureOfflineTransitionHandlerV1 handler = new();

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 prepared = Factory(
            Payload(GrimoireOfflineTransitionState.Prepared),
            continuationCompleted: false);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(prepared));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            prepared with { OrdinaryFactoryContinuationCompleted = true }));

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 beforeBoundary = Factory(
            Payload(GrimoireOfflineTransitionState.Applying) with
            {
                LastCompletedPhase = CovenantResetPhase.CanonicalApplied,
            },
            continuationCompleted: false);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(beforeBoundary));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            beforeBoundary with { OrdinaryFactoryContinuationCompleted = true }));

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 boundary = beforeBoundary with
        {
            LastCompletedPhase = CovenantResetPhase.ManagedArtifactsProcessed,
        };

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 completed = boundary with
        {
            OrdinaryFactoryContinuationCompleted = true,
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(boundary));

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(completed));

        Assert.True(handler.ValidateAdvance(boundary, completed).IsSuccess);

        Assert.True(handler.ValidateAdvance(completed, boundary).IsFailure);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 nextPhase = completed with
        {
            InFlightPhase = CovenantResetPhase.HandlesClosed,
            InFlightBeforeState = new(Digest(0x41), Digest(0x42)),
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(nextPhase));

        Assert.True(handler.ValidateAdvance(completed, nextPhase).IsSuccess);

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            nextPhase with { OrdinaryFactoryContinuationCompleted = false }));

        Assert.True(handler.ValidateAdvance(
            boundary,
            nextPhase with { OrdinaryFactoryContinuationCompleted = true }).IsFailure);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 laterApplying = completed with
        {
            LastCompletedPhase = CovenantResetPhase.WalTruncated,
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(laterApplying));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            laterApplying with { OrdinaryFactoryContinuationCompleted = false }));

        CovenantResetOfflineTransitionPayloadV1 committedReset = Payload(
            GrimoireOfflineTransitionState.ReopenPrepared,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
        {
            LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
        };

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 committed = Factory(
            committedReset,
            continuationCompleted: true);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(committed));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            committed with { OrdinaryFactoryContinuationCompleted = false }));

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 retired = Factory(
            Payload(
                GrimoireOfflineTransitionState.RetirementPending,
                GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            },
            continuationCompleted: true);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(retired));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            retired with { OrdinaryFactoryContinuationCompleted = false }));

        CovenantResetOfflineTransitionPayloadV1 closingReset = Payload(
            GrimoireOfflineTransitionState.Closing) with
        {
            Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Closing) with
            {
                ClosingEvidence = new(true, true, true, true, true, SourceGeneration),
            },
        };

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 closing = Factory(
            closingReset,
            continuationCompleted: false);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 rollback = Factory(
            closingReset with
            {
                Lifecycle = closingReset.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.ReopenPrepared,
                    TerminalIntent =
                        GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
                },
            },
            continuationCompleted: false);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(rollback));

        Assert.True(handler.ValidateAdvance(closing, rollback).IsSuccess);

    }

    [Fact]
    public void Factory_continuation_is_true_in_valid_later_blocked_and_commit_path_shapes()
    {

        CovenantResetOfflineTransitionPayloadV1 verifyingReset = Payload(
            GrimoireOfflineTransitionState.Verifying,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
        {
            LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
        };

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 verifying = Factory(
            verifyingReset,
            continuationCompleted: true);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 kept = Factory(
            verifyingReset with
            {
                Lifecycle = verifyingReset.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.KeepClosed,
                    Blocker = new(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        GrimoireOfflineTransitionState.Verifying,
                        Digest(0x76)),
                },
            },
            continuationCompleted: true);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 reconciling = Factory(
            Payload(
                GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            },
            continuationCompleted: true);

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1[] validControls =
        [
            kept,
            verifying,
            reconciling,
        ];

        Assert.All(validControls, static control => Assert.True(
            GrimoireOfflineTransitionLifecycleValidator.ValidPayload(control)));

        Assert.All(validControls, static control => Assert.False(
            GrimoireOfflineTransitionLifecycleValidator.ValidPayload(control with
            {
                OrdinaryFactoryContinuationCompleted = false,
            })));

    }

    [Fact]
    public void Complete_closing_proof_is_bound_to_the_immutable_source_generation()
    {

        CovenantResetOfflineTransitionPayloadV1 matching = Payload(
            GrimoireOfflineTransitionState.Applying);

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(matching));

        CovenantResetOfflineTransitionPayloadV1 wrong = matching with
        {
            Lifecycle = matching.Lifecycle with
            {
                ClosingEvidence = matching.Lifecycle.ClosingEvidence with
                {
                    ClosedDatasetGeneration = Guid.NewGuid(),
                },
            },
        };

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(wrong));

        CovenantResetOfflineTransitionPayloadV1 wrongRollback = wrong with
        {
            Lifecycle = wrong.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.ReopenPrepared,
                TerminalIntent = GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
            },
            LastCompletedPhase = CovenantResetPhase.InventoryPrepared,
        };

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(wrongRollback));

        CovenantResetOfflineTransitionPayloadV1 wrongKept = wrong with
        {
            Lifecycle = wrong.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = new(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    GrimoireOfflineTransitionState.Applying,
                    Digest(0x73)),
            },
        };

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(wrongKept));

        CovenantResetOfflineTransitionPayloadV1 closing = Payload(
            GrimoireOfflineTransitionState.Closing) with
        {
            Lifecycle = Lifecycle(GrimoireOfflineTransitionState.Closing) with
            {
                ClosingEvidence = new(true, true, true, true, true, Guid.NewGuid()),
            },
        };

        Assert.True(Handler().ValidateAdvance(
            closing,
            closing with
            {
                Lifecycle = closing.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.Applying,
                },
            }).IsFailure);

    }

    [Fact]
    public void Replacement_evidence_advances_only_through_the_exact_compaction_sequence()
    {

        CovenantResetOfflineTransitionPayloadV1 none = Payload(
            GrimoireOfflineTransitionState.Applying) with
        {
            LastCompletedPhase = CovenantResetPhase.WalTruncated,
        };

        CovenantResetOfflineTransitionPayloadV1 baseEvidence = none with
        {
            ReplacementEvidence = ReplacementBase(),
        };

        CovenantResetOfflineTransitionPayloadV1 stagingOwned = baseEvidence with
        {
            ReplacementEvidence = ReplacementStagingOwned(),
        };

        CovenantResetOfflineTransitionPayloadV1 contentProved = stagingOwned with
        {
            ReplacementEvidence = ReplacementContentProved(),
        };

        Assert.True(Handler().ValidateAdvance(none, baseEvidence).IsSuccess);

        Assert.True(Handler().ValidateAdvance(baseEvidence, stagingOwned).IsSuccess);

        Assert.True(Handler().ValidateAdvance(stagingOwned, contentProved).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1 compacting = contentProved with
        {
            InFlightPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightBeforeState = new(Digest(0x41), Digest(0x42)),
        };

        Assert.True(Handler().ValidateAdvance(contentProved, compacting).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1 compacted = compacting with
        {
            LastCompletedPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightPhase = null,
            InFlightBeforeState = null,
        };

        Assert.True(Handler().ValidateAdvance(compacting, compacted).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1 inPlaceCompacting = none with
        {
            InFlightPhase = CovenantResetPhase.DatabaseCompacted,
            InFlightBeforeState = new(Digest(0x43), Digest(0x44)),
        };

        Assert.True(Handler().ValidateAdvance(none, inPlaceCompacting).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1[] invalidFromNone =
        [
            none with { ReplacementEvidence = ReplacementStagingOwned() },
            none with { ReplacementEvidence = ReplacementContentProved() },
            none with
            {
                LastCompletedPhase = CovenantResetPhase.CanonicalApplied,
                ReplacementEvidence = ReplacementBase(),
            },
            none with
            {
                InFlightPhase = CovenantResetPhase.DatabaseCompacted,
                InFlightBeforeState = new(Digest(0x45), Digest(0x46)),
                ReplacementEvidence = ReplacementBase(),
            },
        ];

        Assert.All(invalidFromNone, candidate =>
            Assert.True(Handler().ValidateAdvance(none, candidate).IsFailure));

        Assert.True(Handler().ValidateAdvance(
            baseEvidence,
            baseEvidence with { ReplacementEvidence = ReplacementContentProved() }).IsFailure);

        Assert.True(Handler().ValidateAdvance(
            stagingOwned,
            stagingOwned with
            {
                ReplacementEvidence = ReplacementContentProved() with
                {
                    StagingPhysicalIdentityDigest = Digest(0x99),
                },
            }).IsFailure);

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            compacted with { ReplacementEvidence = ReplacementStagingOwned() }));

        Assert.True(Handler().ValidateAdvance(
            compacted,
            compacted with { ReplacementEvidence = null }).IsFailure);

        CovenantResetOfflineTransitionPayloadV1 kept = compacted with
        {
            Lifecycle = compacted.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = new(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    GrimoireOfflineTransitionState.Applying,
                    Digest(0x74)),
            },
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(kept));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            kept with { ReplacementEvidence = ReplacementStagingOwned() }));

        CovenantResetOfflineTransitionPayloadV1 retired = Payload(
            GrimoireOfflineTransitionState.RetirementPending,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) with
        {
            LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            ReplacementEvidence = ReplacementContentProved(),
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(retired));

        Assert.False(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(
            retired with { ReplacementEvidence = ReplacementStagingOwned() }));

    }

    [Fact]
    public void Replacement_leaf_uses_the_strict_shared_leaf_contract()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying) with
        {
            LastCompletedPhase = CovenantResetPhase.WalTruncated,
        };

        string[] valid =
        [
            new('a', 255),
            new string('é', 127) + "a",
        ];

        Assert.All(valid, leaf => Assert.True(
            GrimoireOfflineTransitionLifecycleValidator.ValidPayload(applying with
            {
                ReplacementEvidence = ReplacementBase() with { StagingLeaf = leaf },
            })));

        string[] invalid =
        [
            string.Empty,
            ".",
            "..",
            "parent/child",
            "parent\\child",
            new('a', 256),
            new('é', 128),
            "\uD800",
        ];

        Assert.All(invalid, leaf => Assert.False(
            GrimoireOfflineTransitionLifecycleValidator.ValidPayload(applying with
            {
                ReplacementEvidence = ReplacementBase() with { StagingLeaf = leaf },
            })));

    }

    [Fact]
    public void Blocker_error_code_is_the_content_free_literal_allowlist()
    {

        CovenantResetOfflineTransitionPayloadV1 applying = Payload(
            GrimoireOfflineTransitionState.Applying,
            inFlight: CovenantResetPhase.CanonicalApplied);

        CovenantResetOfflineTransitionPayloadV1 kept = applying with
        {
            Lifecycle = applying.Lifecycle with
            {
                State = GrimoireOfflineTransitionState.KeepClosed,
                Blocker = new(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    GrimoireOfflineTransitionState.Applying,
                    Digest(0x71)),
            },
        };

        Assert.True(GrimoireOfflineTransitionLifecycleValidator.ValidPayload(kept));

        string[] refused =
        [
            ErrorCodes.Covenant.IntegrityFailure,
            "manual recovery required",
            "Covenant.ManualRecoveryRequired\nsecret",
            "Covenant.ManualRecoveryRequired\u0001",
            "Covenánt.ManualRecoveryRequired",
            ".Covenant.ManualRecoveryRequired",
            "Covenant..ManualRecoveryRequired",
            "Covenant.ManualRecoveryRequired!",
        ];

        Assert.All(refused, errorCode => Assert.False(
            GrimoireOfflineTransitionLifecycleValidator.ValidPayload(kept with
            {
                Lifecycle = kept.Lifecycle with
                {
                    Blocker = kept.Lifecycle.Blocker! with { ErrorCode = errorCode },
                },
            })));

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
                        Digest(0x72)),
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

        if (intent is GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
            && state is GrimoireOfflineTransitionState.ReopenPrepared
                or GrimoireOfflineTransitionState.Verifying
                or GrimoireOfflineTransitionState.DatabaseReconciliationPending
                or GrimoireOfflineTransitionState.RetirementPending)
        {

            payload = payload with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            };

        }

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

        if (payload.Lifecycle.TerminalIntent
                is GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
            && state is GrimoireOfflineTransitionState.ReopenPrepared
                or GrimoireOfflineTransitionState.Verifying
                or GrimoireOfflineTransitionState.DatabaseReconciliationPending
                or GrimoireOfflineTransitionState.RetirementPending)
        {

            payload = payload with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
            };

        }

        if (state is GrimoireOfflineTransitionState.KeepClosed)
        {

            GrimoireOfflineTransitionState resumeState = LegalResumeState(current ? to : from);

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

            GrimoireOfflineTransitionTerminalIntent resumeIntent =
                payload.Lifecycle.TerminalIntent;

            GrimoireOfflineTransitionLifecycle resumeLifecycle =
                Lifecycle(resumeState, resumeIntent);

            payload = payload with
            {
                Lifecycle = resumeLifecycle with
                {
                    State = GrimoireOfflineTransitionState.KeepClosed,
                    ClosingEvidence = !current
                        && resumeState is GrimoireOfflineTransitionState.Closing
                        ? new(
                            true,
                            true,
                            true,
                            true,
                            true,
                            SourceGeneration)
                        : resumeLifecycle.ClosingEvidence,
                    VerificationEvidence = !current
                        && resumeState is GrimoireOfflineTransitionState.Verifying
                        ? new(true, true, true)
                        : resumeLifecycle.VerificationEvidence,
                },
                LastCompletedPhase = resumeIntent
                    is GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                    ? CovenantResetPhase.SidecarsVerified
                    : CovenantResetPhase.InventoryPrepared,
            };

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
                        SourceGeneration),
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
                LegalResumeState(to),
                Digest(0x73));

            payload = payload with
            {
                Lifecycle = payload.Lifecycle with
                {
                    Blocker = current ? blocker : null,
                },
                BlockerResolutionEvidence = current
                    ? null
                    : new(Digest(0x73), Digest(0x73)),
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
                        LegalResumeState(from),
                        Digest(0x74)),
                },
                // A degenerate KeepClosed -> KeepClosed pair falls into both this block and
                // the "from is KeepClosed" block above, which stamped resume evidence onto
                // this same (!current) side for the mirrored reason (resuming out of
                // KeepClosed). A parked payload can carry a Blocker or resume evidence, never
                // both, so parking wins here and the resume evidence is cleared.
                BlockerResolutionEvidence = from is GrimoireOfflineTransitionState.KeepClosed
                    ? null
                    : payload.BlockerResolutionEvidence,
            };

        }

        if (from == to)
        {

            payload = AdvanceSameStateEvidence(payload, current);

        }

        return payload;

    }

    // Blocker.ResumeState can never legally be Prepared, KeepClosed, or RetirementPending
    // (ValidLifecycle), but PayloadForEdge borrows the raw "other" state of the pair under
    // test for that field so a genuinely-illegal edge stays refusable through a resume-state
    // mismatch rather than through a malformed payload. Substituting a state that is always a
    // legal ResumeState keeps the substituted payload's own shape internally coherent because
    // Lifecycle(...) already gives every non-Prepared, non-Closing state complete closing
    // evidence, empty verification, and no reconciliation evidence — exactly what
    // StateEvidenceCoherent's Applying branch requires.
    private static GrimoireOfflineTransitionState LegalResumeState(
        GrimoireOfflineTransitionState natural) => natural is GrimoireOfflineTransitionState.Prepared
            or GrimoireOfflineTransitionState.KeepClosed
            or GrimoireOfflineTransitionState.RetirementPending
        ? GrimoireOfflineTransitionState.Applying
        : natural;

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
                InFlightPhase = current
                    ? null
                    : CovenantResetPhase.CanonicalApplied,
                InFlightBeforeState = current
                    ? null
                    : new(Digest(0x31), Digest(0x32)),
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
            _ => new(true, true, true, true, true, SourceGeneration),
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
        SourceGeneration,
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
            ParentReceiptNotRequired:
                step >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
            ParentReceiptDigest: null,
            LaneClosed: step >= GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            CovenantDispositionIntent: step >= GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight
                ? GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                : null);

    private static GrimoireOfflineTransitionReconciliationEvidence ExactReconciliation(
        GrimoireOfflineTransitionReconciliationStep step,
        CovenantDigest? parent) => new(
            step,
            step >= GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner
                ? Digest(0x51)
                : null,
            ParentReceiptNotRequired: step
                    >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied
                && parent is null,
            ParentReceiptDigest: step
                    >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied
                ? parent
                : null,
            LaneClosed: step >= GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            CovenantDispositionIntent: step
                    >= GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight
                ? GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                : null);

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

    private static HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 Factory(
        CovenantResetOfflineTransitionPayloadV1 payload,
        bool continuationCompleted) => new(
            payload.Binding with
            {
                Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            },
            payload.Lifecycle,
            payload.LastCompletedPhase,
            payload.InFlightPhase,
            payload.InFlightBeforeState,
            payload.ReplacementEvidence,
            continuationCompleted);

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

}
