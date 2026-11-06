using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Dispatch_replay_never_grants_another_external_attempt_or_exposure()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Result<GuildQuestDispatchRecord> prepared = await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("launch"), accounting, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Equal(GuildDispatchEvidence.Prepared, prepared.Value.Evidence);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict,
            (await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("changed"), accounting, false, default)).Error.Code);

        Result<GuildQuestDispatchAdmission> admitted = await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, prepared.Value.Revision, default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.True(admitted.Value.MayDispatch);

        Result<GuildQuestDispatchAdmission> replay = await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, prepared.Value.Revision, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.False(replay.Value.MayDispatch);

        Assert.Equal(1L, await f.CountAsync("fixture_exposures"));

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict,
            (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("launch"), Guid.NewGuid(), false, default)).Error.Code);
    }

    [Fact]
    public async Task Refused_exposure_rolls_back_the_pre_external_boundary_marker()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestDriver driver = await f.DriverAsync(await ReceiptRootAsync(f));

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        GuildQuestDispatchRecord prepared = (await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("launch"), accounting, false, default)).Value;

        f.Budget.RefuseExposure = true;

        Assert.Equal("fixture.exposure_refused", (await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, prepared.Revision, default)).Error.Code);

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));

        Result<GuildQuestDispatchRecord> replay = await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("launch"), accounting, false, default);

        Assert.Equal(GuildDispatchEvidence.Prepared, replay.Value.Evidence);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("revision")]
    [InlineData("expired")]
    [InlineData("canceled")]
    public async Task Dispatch_refuses_a_stale_driver_or_cancellation_frontier(string change)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        if (change == "owner") driver = driver with { LeaseOwner = "other-owner" };
        else if (change == "revision") driver = driver with { OperationRevision = driver.OperationRevision - 1 };
        else if (change == "expired") await f.ExecuteAsync($"UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id='{row.Quest.OperationId:N}'");
        else await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{row.Quest.Id:D}' COLLATE NOCASE");

        Result<GuildQuestDispatchRecord> result = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("launch"), Guid.NewGuid(), false, default);

        Assert.True(result.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));
    }

    [Fact]
    public async Task Dispatch_evidence_preserves_the_first_physical_identity_and_never_regresses()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestDriver driver = await f.DriverAsync(await ReceiptRootAsync(f));

        Guid dispatch = Guid.NewGuid();

        GuildQuestDispatchRecord prepared = (await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("launch"), Guid.NewGuid(), false, default)).Value;

        GuildQuestDispatchRecord possible = (await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, prepared.Revision, default)).Value.Dispatch;

        Guid child = Guid.NewGuid();

        Result<GuildQuestDispatchRecord> known = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch, possible.Revision, GuildDispatchEvidence.Confirmed, LocalChildId: child), default);

        Assert.True(known.IsSuccess, known.Error.Message);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch, known.Value.Revision, GuildDispatchEvidence.Confirmed, LocalChildId: Guid.NewGuid()), default)).Error.Code);

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch, known.Value.Revision, GuildDispatchEvidence.Prepared), default)).IsFailure);
    }

    [Fact]
    public async Task Confirmed_remote_task_can_gain_its_context_without_replacing_the_task_or_replaying_dispatch()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestDriver driver = await f.DriverAsync(await ReceiptRootAsync(f));

        Result<GuildQuestDispatchRecord> prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("remote launch"), Guid.NewGuid(), false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Result<GuildQuestDispatchAdmission> possible = await f.Store.MarkPossiblyDispatchedAsync(driver,
            prepared.Value.DispatchId, prepared.Value.Revision, default);

        Assert.True(possible.IsSuccess, possible.Error.Message);

        Result<GuildQuestDispatchRecord> task = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(prepared.Value.DispatchId, possible.Value.Dispatch.Revision, GuildDispatchEvidence.Confirmed, RemoteTaskId: "task-A"), default);

        Assert.True(task.IsSuccess, task.Error.Message);

        GuildQuestDispatchEvidence observed = new(task.Value.DispatchId, task.Value.Revision, GuildDispatchEvidence.Confirmed,
            RemoteTaskId: "task-A", RemoteContextId: "context-A");

        Result<GuildQuestDispatchRecord> context = await f.Store.RecordDispatchEvidenceAsync(driver, observed, default);

        Assert.True(context.IsSuccess, context.Error.Message);

        Assert.Equal("context-A", context.Value.RemoteContextId);

        Assert.Equal(context.Value, (await f.Store.RecordDispatchEvidenceAsync(driver, observed, default)).Value);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await f.Store.RecordDispatchEvidenceAsync(driver,
            observed with { ExpectedRevision = context.Value.Revision, RemoteContextId = "context-B" }, default)).Error.Code);

        Assert.Equal(1L, await f.CountAsync("fixture_exposures"));
    }

    [Fact]
    public async Task A_continuation_only_start_never_allows_a_later_initial_dispatch_identity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        Result<GuildQuestStored> waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        Result<GuildQuestStored> accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Proceed"), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        driver = driver with { OperationRevision = driver.OperationRevision + 2 };

        Result<GuildQuestDispatchRecord> resumed = await f.Store.PrepareDispatchAsync(driver, continuation,
            ReceiptDigest("accepted continuation"), Guid.NewGuid(), true, default);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        Result<GuildQuestDispatchRecord> duplicate = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("late initial launch"), Guid.NewGuid(), false, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, duplicate.Error.Code);

        Assert.Equal(1L, await f.CountAsync("guild_dispatches"));
    }

    [Fact]
    public async Task A_terminal_receipt_cannot_become_new_dispatch_permission_when_the_ledger_is_inconsistent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestResult rejected = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Rejected, ErrorCodes.Guild.Rejected), null, NoDispatchProven: true);

        Result<GuildResultReceipt> terminal = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("reject")),
            row.Quest.Revision, rejected, default);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        string expiry = RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Format(DateTimeOffset.UtcNow.AddMinutes(5));

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET State=1,LeaseOwner='fixture-driver',LeaseExpiresAt='{expiry}',Revision=Revision+1 WHERE Id='{row.Quest.OperationId:N}'");

        Result<GuildQuestDispatchRecord> dispatch = await f.Store.PrepareDispatchAsync(driver with { OperationRevision = driver.OperationRevision + 2 },
            Guid.NewGuid(), ReceiptDigest("replacement"), Guid.NewGuid(), false, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, dispatch.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));
    }

    [Fact]
    public async Task Terminal_result_replay_reuses_receipt_outbox_and_settlement_before_stale_CAS()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = ReceiptResult(row.Quest.Id);

        GuildMutationIdentity mutation = new(Guid.NewGuid(), ReceiptDigest("terminal"));

        Result<GuildResultReceipt> committed = await f.Store.CommitResultAsync(driver, mutation, row.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.Equal(result.Receipt, committed.Value);

        Assert.True((await f.Store.CommitResultAsync(driver, mutation, 0, result, default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await f.Store.CommitResultAsync(driver, mutation, 0,
            result with { Receipt = result.Receipt with { ResultId = Guid.NewGuid() } }, default)).Error.Code);

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(1L, await f.CountAsync("guild_result_outbox"));

        Assert.Equal(1L, await f.CountAsync("fixture_settlements"));

        GuildQuestStored after = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        Assert.Equal(QuestState.Completed, after.Quest.State);

        Assert.Equal(GuildDeliveryState.Pending, after.Quest.DeliveryState);
    }

    [Theory]
    [InlineData("guild_content", "INSERT")]
    [InlineData("guild_result_receipts", "INSERT")]
    [InlineData("LongRunningOperations", "UPDATE")]
    [InlineData("guild_result_outbox", "INSERT")]
    [InlineData("fixture_settlements", "INSERT")]
    [InlineData("guild_quests", "UPDATE")]
    public async Task Result_fault_at_each_durable_boundary_rolls_back_the_entire_completion(string table, string operation)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        long content = await f.CountAsync("guild_content");

        await f.ExecuteAsync($"CREATE TRIGGER fixture_receipt_fault BEFORE {operation} ON {table} BEGIN SELECT RAISE(ABORT,'fixture interruption'); END;");

        Result<GuildResultReceipt> result = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("terminal")),
            row.Quest.Revision, ReceiptResult(row.Quest.Id), default);

        Assert.True(result.IsFailure);

        Assert.Equal(content, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("guild_result_outbox"));

        Assert.Equal(0L, await f.CountAsync("fixture_settlements"));

        Assert.Equal((long)LongRunningOperationState.Running,
            await ReceiptScalarAsync(f, $"SELECT State FROM LongRunningOperations WHERE Id='{row.Quest.OperationId:N}'"));
    }

    [Fact]
    public async Task Unknown_dispatch_cannot_be_rejected_or_declared_canceled_by_a_boolean_claim()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord prepared = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("launch"), Guid.NewGuid(), false, default)).Value;

        Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, prepared.DispatchId, prepared.Revision, default)).Value.MayDispatch);

        await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{row.Quest.Id:D}' COLLATE NOCASE");

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        foreach ((QuestState outcome, string code) in new[] { (QuestState.Rejected, ErrorCodes.Guild.Rejected), (QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled) })
        {
            GuildQuestResult claim = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null, outcome, code), null,
                NoDispatchProven: true, WorkStoppedProven: true);

            Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("claim")), row.Quest.Revision, claim, default)).IsFailure);
        }

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(1L, await f.CountAsync("fixture_exposures"));
    }

    [Fact]
    public async Task Protected_result_uses_the_canonical_content_and_label_transaction()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        ArtifactSensitivityLabel source = new(Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), row.Quest.SessionId, null, null,
            1, new(SHA256.HashData("source"u8)), ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow);

        GuildQuestResult result = ReceiptResult(row.Quest.Id) with { SourceLabel = source };

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("protected")), row.Quest.Revision, result, default)).IsSuccess);

        Assert.Equal(1L, await ReceiptScalarAsync(f, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));
    }

    [Fact]
    public async Task Refused_settlement_rolls_back_result_while_uncertain_settlement_remains_visible()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = ReceiptResult(row.Quest.Id);

        f.Budget.RefuseSettlement = true;

        Assert.Equal("fixture.settlement_refused", (await f.Store.CommitResultAsync(driver,
            new(Guid.NewGuid(), ReceiptDigest("settle")), row.Quest.Revision, result, default)).Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("fixture_settlements"));

        f.Budget.RefuseSettlement = false;

        f.Budget.SettlementState = GuildAccountingState.Uncertain;

        Assert.True((await f.Store.CommitResultAsync(driver,
            new(Guid.NewGuid(), ReceiptDigest("settle")), row.Quest.Revision, result, default)).IsSuccess);

        GuildQuestStored completed = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        Assert.Equal(QuestState.Completed, completed.Quest.State);

        Assert.Equal(GuildAccountingState.Uncertain, completed.Quest.AccountingState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_never_dispatched_rejection_or_requested_quiescent_cancellation_is_truthful(bool cancel)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        if (cancel)
        {
            await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{row.Quest.Id:D}' COLLATE NOCASE");

            row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;
        }

        QuestState outcome = cancel ? QuestState.Canceled : QuestState.Rejected;

        string reason = cancel ? LongRunningOperationRecoveryOutcomes.Cancelled : ErrorCodes.Guild.Rejected;

        GuildQuestResult result = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null, outcome, reason), null,
            NoDispatchProven: true, WorkStoppedProven: cancel);

        Result<GuildResultReceipt> committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("stop")),
            row.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        GuildQuestStored after = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        Assert.Equal(outcome, after.Quest.State);

        Assert.Equal(cancel, after.Quest.CancellationConverged);

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));
    }

    [Fact]
    public async Task Parent_application_commits_one_input_and_acknowledgement_and_replays_after_restart()
    {
        await using Fixture f = await Fixture.CreateAsync();

        (GuildQuestStored parent, GuildQuestStored child, GuildResultReceipt result) = await CompletedReceiptChildAsync(f);

        GuildResultApplicationRequest request = new(new(Guid.NewGuid(), ReceiptDigest("apply")), parent.Quest.Id,
            child.Quest.Id, result.ResultId, result.CanonicalContentDigest, parent.Quest.Revision);

        Result<GuildResultApplicationReceipt> applied = await f.Store.ApplyResultAsync(f.Caller, request, default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        f.Restart();

        Result<GuildResultApplicationReceipt> replay = await f.Store.ApplyResultAsync(f.Caller, request with { ExpectedRecipientRevision = 0 }, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt.Replayed);

        Assert.Equal(applied.Value.RecipientRevision, replay.Value.RecipientRevision);

        Assert.Equal(1L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal(1L, await f.CountAsync("guild_result_applications"));

        Assert.Equal((long)GuildDeliveryState.Applied,
            await ReceiptScalarAsync(f, "SELECT DeliveryStateCode FROM guild_result_outbox"));

        Assert.Equal(1L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await f.Store.ApplyResultAsync(f.Caller,
            request with { CanonicalContentDigest = ReceiptDigest("changed") }, default)).Error.Code);
    }

    [Fact]
    public async Task Fresh_parent_application_refuses_revoked_recipient_authority_without_acknowledging_delivery()
    {
        await using Fixture f = await Fixture.CreateAsync();

        (GuildQuestStored parent, GuildQuestStored child, GuildResultReceipt result) = await CompletedReceiptChildAsync(f);

        GuildQuestStored current = (await f.Store.GetAsync(f.Caller, parent.Quest.Id, default)).Value;

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Caller, new(key, ReceiptDigest("revoke recipient")), current.Quest.SignetId,
            new(key, current.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Result<GuildResultApplicationReceipt> applied = await f.Store.ApplyResultAsync(f.Caller,
            new(new(Guid.NewGuid(), ReceiptDigest("apply")), parent.Quest.Id, child.Quest.Id,
                result.ResultId, result.CanonicalContentDigest, parent.Quest.Revision), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, applied.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal((long)GuildDeliveryState.Pending, await ReceiptScalarAsync(f, "SELECT DeliveryStateCode FROM guild_result_outbox"));
    }

    [Theory]
    [InlineData("guild_result_applications", "INSERT")]
    [InlineData("guild_result_inputs", "INSERT")]
    [InlineData("guild_result_outbox", "UPDATE")]
    [InlineData("guild_quests", "UPDATE")]
    public async Task Parent_application_fault_never_leaves_an_input_receipt_or_acknowledgement_alone(string table, string operation)
    {
        await using Fixture f = await Fixture.CreateAsync();

        (GuildQuestStored parent, GuildQuestStored child, GuildResultReceipt result) = await CompletedReceiptChildAsync(f);

        await f.ExecuteAsync($"CREATE TRIGGER fixture_application_fault BEFORE {operation} ON {table} BEGIN SELECT RAISE(ABORT,'fixture interruption'); END;");

        Assert.True((await f.Store.ApplyResultAsync(f.Caller, new(new(Guid.NewGuid(), ReceiptDigest("apply")), parent.Quest.Id,
            child.Quest.Id, result.ResultId, result.CanonicalContentDigest, parent.Quest.Revision), default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal(0L, await f.CountAsync("guild_result_applications"));

        Assert.Equal((long)GuildDeliveryState.Pending,
            await ReceiptScalarAsync(f, "SELECT DeliveryStateCode FROM guild_result_outbox"));
    }

    private static async Task<GuildQuestStored> ReceiptRootAsync(Fixture f)
    {
        Result<GuildQuestStored> result = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static async Task ConfirmReceiptDispatchAsync(Fixture f, GuildQuestDriver driver)
    {
        Result<GuildQuestDispatchRecord> prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("launch"), Guid.NewGuid(), false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Result<GuildQuestDispatchAdmission> possible = await f.Store.MarkPossiblyDispatchedAsync(driver, prepared.Value.DispatchId, prepared.Value.Revision, default);

        Assert.True(possible.IsSuccess, possible.Error.Message);

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver,
            new(prepared.Value.DispatchId, possible.Value.Dispatch.Revision, GuildDispatchEvidence.Confirmed, LocalChildId: Guid.NewGuid()), default)).IsSuccess);
    }

    private static async Task<(GuildQuestStored, GuildQuestStored, GuildResultReceipt)> CompletedReceiptChildAsync(Fixture f)
    {
        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver parentDriver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, parentDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child, "child-driver");

        await ConfirmReceiptDispatchAsync(f, childDriver);

        child = (await f.Store.GetAsync(f.Caller, child.Quest.Id, default)).Value;

        Result<GuildResultReceipt> completed = await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), ReceiptDigest("terminal")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id), default);

        Assert.True(completed.IsSuccess, completed.Error.Message);

        return (parent, child, completed.Value);
    }

    private static GuildQuestResult ReceiptResult(Guid quest)
    {
        byte[] body = Encoding.UTF8.GetBytes("Immutable child contribution");

        string digest = Convert.ToHexString(SHA256.HashData(body));

        return new(new(quest, Guid.NewGuid(), digest, new(Guid.NewGuid(), GuildContentKind.QuestResult, 1,
            digest, "text/plain", body.Length, false), QuestState.Completed, null), body);
    }

    private static string ReceiptDigest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<object?> ReceiptScalarAsync(Fixture f, string sql)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
