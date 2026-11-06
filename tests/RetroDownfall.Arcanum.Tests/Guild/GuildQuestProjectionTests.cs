using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestProjectionTests
{
    [Fact]
    public void Pending_projects_submitted_from_the_authoritative_ledger()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Pending), Evidence(QuestState.Working));

        Assert.Equal(QuestState.Submitted, projected.State);

        Assert.False(projected.ReconciliationRequired);
    }

    [Fact]
    public void Running_projects_working_without_reusing_stale_input_evidence()
    {
        var evidence = Evidence(QuestState.InputRequired) with
        {
            WaitReason = GuildWaitReason.Input,
            AwaitingInput = new(Guid.NewGuid(), Guid.NewGuid())
        };

        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Running), evidence);

        Assert.Equal(QuestState.Working, projected.State);

        Assert.Null(projected.WaitReason);

        Assert.False(projected.ReconciliationRequired);
    }

    [Fact]
    public void Waiting_with_committed_question_and_continuation_projects_input_required()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Waiting), Evidence() with
        {
            WaitReason = GuildWaitReason.Input,
            AwaitingInput = new(Guid.NewGuid(), Guid.NewGuid())
        });

        Assert.Equal(QuestState.InputRequired, projected.State);

        Assert.Equal(GuildWaitReason.Input, projected.WaitReason);

        Assert.False(projected.ReconciliationRequired);
    }

    [Theory]
    [InlineData(QuestState.Submitted, GuildWaitReason.Maintenance)]
    [InlineData(QuestState.Working, GuildWaitReason.Budget)]
    [InlineData(QuestState.Working, GuildWaitReason.Transport)]
    public void Ordinary_waits_retain_the_committed_phase_and_reason(QuestState phase, GuildWaitReason reason)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Waiting), Evidence(phase) with { WaitReason = reason });

        Assert.Equal(phase, projected.State);

        Assert.Equal(reason, projected.WaitReason);

        Assert.False(projected.ReconciliationRequired);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Incomplete_input_evidence_requires_reconciliation(bool questionPresent, bool continuationPresent)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Waiting), Evidence(QuestState.Working) with
        {
            WaitReason = GuildWaitReason.Input,
            AwaitingInput = new(questionPresent ? Guid.NewGuid() : Guid.Empty, continuationPresent ? Guid.NewGuid() : Guid.Empty)
        });

        AssertUncertain(projected, QuestState.Working);
    }

    [Theory]
    [InlineData(QuestState.Submitted)]
    [InlineData(QuestState.Working)]
    [InlineData(QuestState.InputRequired)]
    public void Cancelling_retains_each_last_nonterminal_state_without_claiming_stopped_work(QuestState phase)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Cancelling), Evidence(phase));

        Assert.Equal(phase, projected.State);

        Assert.True(projected.CancellationRequested);

        Assert.False(projected.CancellationConverged);

        Assert.False(projected.ReconciliationRequired);
    }

    [Theory]
    [InlineData(QuestState.Submitted)]
    [InlineData(QuestState.Working)]
    [InlineData(QuestState.InputRequired)]
    public void Reconciliation_retains_each_last_nonterminal_state_with_a_safe_reason(QuestState phase)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.ReconciliationRequired, "unsafe peer diagnostic"), Evidence(phase));

        AssertUncertain(projected, phase);
    }

    [Fact]
    public void Completed_requires_matching_durable_success_receipt()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed), Evidence(QuestState.Working) with
        {
            ResultReceipt = Receipt(QuestState.Completed)
        });

        Assert.Equal(QuestState.Completed, projected.State);

        Assert.False(projected.ReconciliationRequired);

        Assert.False(projected.CancellationConverged);
    }

    [Fact]
    public void Completed_cancellation_requires_stopped_work_and_cancellation_receipt()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed, "operation.cancelled"), Evidence(QuestState.Working) with
        {
            ResultReceipt = Receipt(QuestState.Canceled, "operation.cancelled"),
            WorkStoppedProven = true
        });

        Assert.Equal(QuestState.Canceled, projected.State);

        Assert.True(projected.CancellationRequested);

        Assert.True(projected.CancellationConverged);

        Assert.False(projected.ReconciliationRequired);
    }

    [Fact]
    public void Failed_rejection_requires_no_dispatch_proof()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Failed, "Guild.Rejected"), Evidence(QuestState.Working) with { NoDispatchProven = true });

        Assert.Equal(QuestState.Rejected, projected.State);

        Assert.Equal("Guild.Rejected", projected.ReasonCode);

        Assert.False(projected.ReconciliationRequired);
    }

    [Fact]
    public void Failed_with_matching_failure_receipt_remains_failed_despite_cancellation_request()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Failed, "Guild.ChildFailed"), Evidence(QuestState.Working) with
        {
            ResultReceipt = Receipt(QuestState.Failed, "Guild.ChildFailed"),
            CancellationRequested = true
        });

        Assert.Equal(QuestState.Failed, projected.State);

        Assert.True(projected.CancellationRequested);

        Assert.False(projected.CancellationConverged);

        Assert.False(projected.ReconciliationRequired);
    }

    [Fact]
    public void Safely_abandoned_with_named_failure_projects_failed()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Abandoned, "Guild.ChildAbandoned"), Evidence(QuestState.Working) with { SafelyAbandoned = true });

        Assert.Equal(QuestState.Failed, projected.State);

        Assert.Equal("Guild.ChildAbandoned", projected.ReasonCode);

        Assert.False(projected.ReconciliationRequired);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Completed, null)]
    [InlineData(LongRunningOperationState.Completed, "operation.cancelled")]
    [InlineData(LongRunningOperationState.Failed, "Guild.Rejected")]
    [InlineData(LongRunningOperationState.Failed, "Guild.ChildFailed")]
    [InlineData(LongRunningOperationState.Abandoned, "Guild.ChildAbandoned")]
    [InlineData(LongRunningOperationState.Abandoned, null)]
    [InlineData((LongRunningOperationState)999, null)]
    public void Terminal_ledger_or_unknown_state_without_required_evidence_is_uncertain(LongRunningOperationState state, string? code)
    {
        AssertUncertain(GuildQuestProjection.Project(Operation(state, code), Evidence(QuestState.Working)), QuestState.Working);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Pending)]
    [InlineData(LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationState.Waiting)]
    [InlineData(LongRunningOperationState.Cancelling)]
    [InlineData(LongRunningOperationState.ReconciliationRequired)]
    public void Nonterminal_ledger_cannot_be_promoted_by_a_terminal_receipt(LongRunningOperationState state)
    {
        var projected = GuildQuestProjection.Project(Operation(state), Evidence(QuestState.Working) with { ResultReceipt = Receipt(QuestState.Completed) });

        AssertUncertain(projected, QuestState.Working);
    }

    [Theory]
    [InlineData(QuestState.Submitted)]
    [InlineData(QuestState.Working)]
    [InlineData(QuestState.InputRequired)]
    [InlineData(QuestState.Failed)]
    [InlineData(QuestState.Canceled)]
    [InlineData(QuestState.Rejected)]
    [InlineData((QuestState)999)]
    public void Completed_cannot_use_a_receipt_for_another_outcome(QuestState outcome)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed), Evidence(QuestState.Working) with { ResultReceipt = Receipt(outcome) });

        AssertUncertain(projected, QuestState.Working);
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("result")]
    [InlineData("digest")]
    [InlineData("content-digest")]
    [InlineData("content-kind")]
    public void Completed_receipt_identity_or_integrity_mismatch_is_uncertain(string mismatch)
    {
        GuildResultReceipt receipt = Receipt(QuestState.Completed);

        receipt = mismatch switch
        {
            "quest" => receipt with { QuestId = Guid.NewGuid() },
            "result" => receipt with { ResultId = Guid.Empty },
            "digest" => receipt with { CanonicalContentDigest = " " },
            "content-digest" => receipt with { Content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, "other", "text/plain", 1, false) },
            "content-kind" => receipt with { Content = new(Guid.NewGuid(), GuildContentKind.QuestGoal, 1, "digest", "text/plain", 1, false) },
            _ => throw new InvalidOperationException()
        };

        AssertUncertain(GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed), Evidence(QuestState.Working) with { ResultReceipt = receipt }), QuestState.Working);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Cancellation_never_completes_without_both_stopped_proof_and_receipt(bool stopped, bool receiptPresent)
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed, "operation.cancelled"), Evidence(QuestState.Working) with
        {
            WorkStoppedProven = stopped,
            ResultReceipt = receiptPresent ? Receipt(QuestState.Canceled, "operation.cancelled") : null
        });

        AssertUncertain(projected, QuestState.Working);

        Assert.True(projected.CancellationRequested);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Completed, "operation.cancelled", QuestState.Completed, null)]
    [InlineData(LongRunningOperationState.Completed, "Guild.ChildFailed", QuestState.Completed, null)]
    [InlineData(LongRunningOperationState.Completed, null, QuestState.Completed, "Guild.ChildFailed")]
    [InlineData(LongRunningOperationState.Failed, "Guild.ChildFailed", QuestState.Failed, "Guild.OtherFailure")]
    [InlineData(LongRunningOperationState.Failed, "Guild.Rejected", QuestState.Failed, "Guild.ChildFailed")]
    [InlineData(LongRunningOperationState.Abandoned, "operation.cancelled", QuestState.Canceled, "operation.cancelled")]
    [InlineData(LongRunningOperationState.Abandoned, "Guild.ChildFailed", QuestState.Completed, null)]
    public void Contradictory_terminal_evidence_requires_reconciliation(LongRunningOperationState state, string? operationCode, QuestState outcome, string? receiptCode)
    {
        var projected = GuildQuestProjection.Project(Operation(state, operationCode), Evidence(QuestState.Working) with
        {
            ResultReceipt = Receipt(outcome, receiptCode),
            WorkStoppedProven = true,
            NoDispatchProven = true,
            SafelyAbandoned = true
        });

        AssertUncertain(projected, QuestState.Working);
    }

    [Fact]
    public void Invalid_operation_binding_cannot_project_success()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed), Evidence(QuestState.Working) with
        {
            OperationId = Guid.NewGuid(),
            ResultReceipt = Receipt(QuestState.Completed)
        });

        AssertUncertain(projected, QuestState.Working);
    }

    [Theory]
    [InlineData(QuestState.Completed)]
    [InlineData(QuestState.Failed)]
    [InlineData(QuestState.Canceled)]
    [InlineData(QuestState.Rejected)]
    [InlineData((QuestState)999)]
    public void Invalid_last_nonterminal_evidence_cannot_invent_a_terminal_state(QuestState phase)
    {
        AssertUncertain(GuildQuestProjection.Project(Operation(LongRunningOperationState.Cancelling), Evidence(phase)), QuestState.Working);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Pending)]
    [InlineData(LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationState.Waiting)]
    public void Committed_cancellation_request_is_retained_before_ledger_cancellation(LongRunningOperationState state)
    {
        var projected = GuildQuestProjection.Project(Operation(state), Evidence() with { CancellationRequested = true });

        Assert.True(projected.CancellationRequested);

        Assert.False(projected.CancellationConverged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((GuildWaitReason)999)]
    [InlineData(GuildWaitReason.Reconciliation)]
    public void Waiting_without_a_supported_reason_is_uncertain(GuildWaitReason? reason)
    {
        AssertUncertain(GuildQuestProjection.Project(Operation(LongRunningOperationState.Waiting), Evidence(QuestState.Working) with { WaitReason = reason }), QuestState.Working);
    }

    [Fact]
    public void An_ordinary_wait_cannot_reuse_a_previous_input_required_phase()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Waiting), Evidence(QuestState.InputRequired) with { WaitReason = GuildWaitReason.Maintenance });

        AssertUncertain(projected, QuestState.InputRequired);
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("operation")]
    [InlineData("kind")]
    public void Missing_identity_or_wrong_operation_kind_cannot_project_a_terminal_outcome(string mismatch)
    {
        var operation = Operation(LongRunningOperationState.Completed);

        var evidence = Evidence(QuestState.Working) with { ResultReceipt = Receipt(QuestState.Completed) };

        if (mismatch == "quest")
            evidence = evidence with { QuestId = Guid.Empty, ResultReceipt = Receipt(QuestState.Completed) with { QuestId = Guid.Empty } };
        else if (mismatch == "operation")
        {
            operation = operation with { Id = Guid.Empty };

            evidence = evidence with { OperationId = Guid.Empty };
        }
        else
            operation = operation with { Kind = "subagent" };

        AssertUncertain(GuildQuestProjection.Project(operation, evidence), QuestState.Working);
    }

    [Fact]
    public void A_reconciliation_reason_cannot_be_used_as_a_named_abandonment_failure()
    {
        AssertUncertain(GuildQuestProjection.Project(Operation(LongRunningOperationState.Abandoned, "Guild.ReconciliationRequired"), Evidence(QuestState.Working) with { SafelyAbandoned = true }), QuestState.Working);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Completed, QuestState.Completed, null)]
    [InlineData(LongRunningOperationState.Failed, QuestState.Failed, "Guild.ChildFailed")]
    public void Validated_later_cancellation_convergence_preserves_the_original_terminal_outcome(LongRunningOperationState ledger, QuestState outcome, string? code)
    {
        var evidence = WithConvergence(Evidence(QuestState.Working) with
        {
            CancellationRequested = true,
            ResultReceipt = Receipt(outcome, code)
        });

        var projected = GuildQuestProjection.Project(Operation(ledger, code), evidence);

        Assert.Equal(outcome, projected.State);

        Assert.True(projected.CancellationRequested);

        Assert.True(projected.CancellationConverged);

        Assert.False(projected.ReconciliationRequired);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Pending)]
    [InlineData(LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationState.Cancelling)]
    [InlineData(LongRunningOperationState.ReconciliationRequired)]
    public void A_convergence_flag_cannot_override_unknown_or_nonterminal_execution(LongRunningOperationState ledger)
    {
        var projected = GuildQuestProjection.Project(Operation(ledger), WithConvergence(Evidence(QuestState.Working) with { CancellationRequested = true }));

        Assert.False(projected.CancellationConverged);

        Assert.NotEqual(QuestState.Canceled, projected.State);
    }

    [Fact]
    public void Convergence_without_a_cancellation_request_is_not_inferred_from_a_successful_receipt()
    {
        var projected = GuildQuestProjection.Project(Operation(LongRunningOperationState.Completed), WithConvergence(Evidence(QuestState.Working) with
        {
            ResultReceipt = Receipt(QuestState.Completed)
        }));

        Assert.Equal(QuestState.Completed, projected.State);

        Assert.False(projected.CancellationConverged);

        Assert.False(projected.CancellationRequested);
    }

    private static GuildQuestProjectionEvidence WithConvergence(GuildQuestProjectionEvidence evidence) =>
        evidence with { CancellationConverged = true };

    private static GuildResultReceipt Receipt(QuestState outcome, string? reason = null) =>
        new(QuestId, Guid.Parse("33333333-3333-3333-3333-333333333333"), "digest", null, outcome, reason);

    private static void AssertUncertain(GuildQuestProjectedState projected, QuestState phase)
    {
        Assert.Equal(phase, projected.State);

        Assert.True(projected.ReconciliationRequired);

        Assert.False(projected.CancellationConverged);

        Assert.Equal("Guild.ReconciliationRequired", projected.ReasonCode);
    }

    private static readonly Guid QuestId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GuildQuestProjectionEvidence Evidence(QuestState phase = QuestState.Submitted) =>
        new(QuestId, OperationId, phase);

    private static readonly Guid OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LongRunningOperation Operation(LongRunningOperationState state, string? terminalErrorCode = null) =>
        new(OperationId, "guild-quest", state, LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            null, null, null, null, null, null, null, DateTimeOffset.UnixEpoch, null, null, null, null, null,
            0, 1, null, null, "Quest", terminalErrorCode, 1);
}
