using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Persisted_legacy_tool_wait_parks_recovery_and_refuses_continuation_without_rewriting_checkpoint(bool protectedCheckpoint, bool acceptedBeforeCutover)
    {
        const string frontier = "unpaid-parked-wait";

        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        BudgetReservationService daily = new(db, f.Settings);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, quest.Id, default);

        Assert.True(run.IsSuccess, run.Error.Message);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(quest.OperationId, "before-restart", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(execution.Value, "before-restart", leased.Operation.Revision, run.Value);

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.Connection.BeginTransaction())
            binding = (await GuildLocalExecutionStore.ReadWithinAsync(connection.Connection, transaction, quest.Id, default)).Value;

        var dispatch = await store.PrepareDispatchAsync(driver, quest.Id, binding.Input.Digest, quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        GuildLocalInferenceExecution invocation = new(store, driver, dispatch.Value);

        PaidPortEngine paid = new(daily, true);

        bool paidCall = frontier != "unpaid-parked-wait";

        if (paidCall)
        {
            var paidResult = await paid.ExecuteGuildAsync(binding, (await store.ReadLocalInputAsync(driver, default)).Value,
                invocation, new UnusedInference(), null!, default);

            Assert.True(paidResult.IsSuccess, paidResult.Error.Message);
        }

        Assert.True((await store.ResolveLocalDispatchAsync(driver, quest.Id, default)).IsSuccess);

        Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

        GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, 0,
            [new() { Index = 0, Description = acceptedBeforeCutover ? "Choose an option" : "Use read_file_chunk to inspect the option", Attempts = 1 }],
            new() { CurrentStep = 0, ParentApprenticeId = binding.ParentApprenticeId, LaunchRequested = true, EscalationReason = "Which option?" });

        ArtifactSensitivityLabel? sourceLabel = protectedCheckpoint ? new(Guid.NewGuid(), SensitiveArtifactKind.GuildContent,
            Guid.NewGuid(), binding.SessionId, null, null, 1, new CovenantDigest(new byte[32]), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow) : null;

        var waiting = await store.WaitForInputAsync(driver, question, continuation, new(quest.Id, snapshot, sourceLabel), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        if (acceptedBeforeCutover)
        {
            var historicalAnswer = await store.AcceptContinuationAsync(caller, quest.Id,
                new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Accepted before the tool cutover"), default);

            Assert.True(historicalAnswer.IsSuccess, historicalAnswer.Error.Message);

            // Stage the exact historical storage shape left by a previous version: an accepted
            // continuation, no offered question ids, and an immutable checkpoint with the old name.
            snapshot = snapshot with { Plan = [new() { Index = 0, Description = "Use read_file_chunk to inspect the option", Attempts = 1 }] };

            byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot);

            Guid contentId = Guid.NewGuid(), historicalQuestion = Guid.NewGuid();

            byte[] digest = System.Security.Cryptography.SHA256.HashData(body);

            DerivedArtifactWrite sensitivity = new(SensitiveArtifactKind.GuildContent, contentId, quest.SessionId, quest.CampaignId,
                null, 1, new CovenantDigest(digest), sourceLabel?.Sensitivity ?? ContentSensitivity.None,
                sourceLabel?.Provenance ?? GenerationProvenance.CreateExact([]));

            await using SqliteTransaction transaction = connection.Connection.BeginTransaction();

            var written = await GuildStore.WriteContentWithinAsync(connection.Connection, transaction,
                new(contentId, historicalQuestion, GuildContentKind.ExecutionCheckpoint, 1, quest.SessionId, quest.CampaignId,
                    "application/json", body, sensitivity), DateTimeOffset.UtcNow, default);

            Assert.True(written.IsSuccess, written.Error.Message);

            await using SqliteCommand guard = connection.Connection.CreateCommand();

            guard.Transaction = transaction;

            guard.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_local_wait_checkpoints_guard_update'";

            string guardSql = (string)(await guard.ExecuteScalarAsync())!;

            guard.CommandText = "DROP TRIGGER guild_local_wait_checkpoints_guard_update";

            await guard.ExecuteNonQueryAsync();

            await using SqliteCommand stage = connection.Connection.CreateCommand();

            stage.Transaction = transaction;

            stage.CommandText = "UPDATE guild_local_wait_checkpoints SET QuestionId=$question,ContentId=$content,Digest=$digest,ByteLength=$length WHERE QuestId=$quest AND ContinuationId=$continuation";

            stage.Parameters.AddWithValue("$question", historicalQuestion.ToString("D").ToUpperInvariant());

            stage.Parameters.AddWithValue("$content", contentId.ToString("D").ToUpperInvariant());

            stage.Parameters.AddWithValue("$digest", digest);

            stage.Parameters.AddWithValue("$length", body.Length);

            stage.Parameters.AddWithValue("$quest", quest.Id.ToString("D").ToUpperInvariant());

            stage.Parameters.AddWithValue("$continuation", continuation.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await stage.ExecuteNonQueryAsync());

            guard.CommandText = guardSql;

            await guard.ExecuteNonQueryAsync();

            await transaction.CommitAsync();

            waiting = await store.GetAsync(caller, quest.Id, default);
        }

        await using SqliteCommand retained = connection.Connection.CreateCommand();

        retained.CommandText = "SELECT hex(c.Body) FROM guild_content c JOIN guild_local_wait_checkpoints w ON w.ContentId=c.ContentId";

        string before = (string)(await retained.ExecuteScalarAsync())!;

        LongRunningOperation currentOperation = (await operations.GetAsync(quest.OperationId))!;

        GuildQuestRecoveryHandler recoveryHandler = new(f.Connections, f.Gate, TimeProvider.System, []);

        var recovered = await recoveryHandler.RecoverAsync(currentOperation, default);

        Assert.Equal(ErrorCodes.Guild.LegacyToolName, recovered.ErrorCode);

        var accepted = await store.AcceptContinuationAsync(caller, quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Continue"), default);

        Assert.True(accepted.IsFailure);

        Assert.Equal(acceptedBeforeCutover ? ErrorCodes.Guild.IdempotencyConflict : ErrorCodes.Guild.LegacyToolName, accepted.Error.Code);

        if (!acceptedBeforeCutover)
        {
            Assert.Contains("read_file_chunk", accepted.Error.Message);

            Assert.Contains("read_file", accepted.Error.Message);
        }

        Assert.Equal(acceptedBeforeCutover ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Assert.True((await store.ParkLocalExecutionAsync(driver, default, ErrorCodes.Guild.LegacyToolName)).IsSuccess);

        GuildQuestStore restarted = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        var observed = await restarted.GetAsync(caller, quest.Id, default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(observed.Value.Quest,
            RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext.Default.GuildQuestDto));

        Assert.True(json.RootElement.TryGetProperty("reasonCode", out var reason));

        Assert.Equal("Guild.LegacyToolName", reason.GetString());

        Assert.True(json.RootElement.TryGetProperty("toolDiagnostics", out var tools));

        Assert.Equal("read_file_chunk", tools[0].GetProperty("toolName").GetString());

        Assert.Equal("read_file", tools[0].GetProperty("replacement").GetString());

        Assert.DoesNotContain("Use read_file_chunk to inspect the option", tools.GetRawText());

        Assert.Equal(before, (string)(await retained.ExecuteScalarAsync())!);
    }
}
