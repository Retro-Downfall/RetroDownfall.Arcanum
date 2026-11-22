using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Mcp;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ToolInvocationRecoveryTests
{
    [SkippableFact]
    public Task Begun_owner_commits_protected_tool_result_and_receipt_after_durable_expiry() =>
        WithActiveTurnAsync(async turn =>
        {
            var toolResult = await DispatchProtectedReadAsync(turn);

            await ExpireBegunWriteDeadlineAsync(turn);

            Guid session = turn.Owner.Claim.SessionId;

            int before = (await turn.Transcript.GetSessionEntriesAsync(session))!.Count;

            await AppendProtectedResultAsync(turn, toolResult);

            var effect = await ReadProtectedEffectAsync(turn, toolResult);

            Assert.Equal(ToolInvocationState.Completed, effect.State);

            Assert.NotNull(effect.ResultEntryId);

            Assert.NotEqual(toolResult.ProtectedInvocationCompletion!.Key.Receipt.ResultEntryId, effect.ResultEntryId);

            Assert.Equal(before + 2, (await turn.Transcript.GetSessionEntriesAsync(session))!.Count);
        });

    [SkippableFact]
    public Task Begun_owner_saves_reconciliation_checkpoint_after_durable_expiry() =>
        WithActiveTurnAsync(async turn =>
        {
            var toolResult = await DispatchProtectedReadAsync(turn);

            await ExpireBegunWriteDeadlineAsync(turn);

            SessionRecoveryCheckpointStore checkpoints = new(turn.Connections, TimeProvider.System);

            var checkpoint = ReconciliationCheckpoint(turn, toolResult);

            var saved = await checkpoints.SaveAsync(turn.Owner, checkpoint, default);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            Assert.Equal(checkpoint, saved.Value);

            var retained = await checkpoints.FindAsync(checkpoint.SessionId, checkpoint.Id, default);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Equal(checkpoint, retained.Value);

            Assert.Equal(ToolInvocationState.Dispatched, (await ReadProtectedEffectAsync(turn, toolResult)).State);
        });

    [SkippableTheory]
    [InlineData("terminal")]
    [InlineData("released")]
    [InlineData("executor")]
    [InlineData("boot")]
    [InlineData("reservation")]
    public Task Protected_tool_write_refuses_missing_begun_owner_authority(string lostAuthority) =>
        WithActiveTurnAsync(async turn =>
        {
            var toolResult = await DispatchProtectedReadAsync(turn);

            turn.Handle.ClaimLease = await LoseBegunWriteAuthorityAsync(turn, lostAuthority);

            Guid session = turn.Owner.Claim.SessionId;

            int before = (await turn.Transcript.GetSessionEntriesAsync(session))!.Count;

            await Assert.ThrowsAsync<InvalidOperationException>(() => AppendProtectedResultAsync(turn, toolResult));

            Assert.Equal(before, (await turn.Transcript.GetSessionEntriesAsync(session))!.Count);

            var effect = await ReadProtectedEffectAsync(turn, toolResult);

            Assert.Equal(ToolInvocationState.Dispatched, effect.State);

            Assert.Null(effect.ResultEntryId);
        });

    [SkippableTheory]
    [InlineData("terminal")]
    [InlineData("released")]
    [InlineData("executor")]
    [InlineData("boot")]
    [InlineData("reservation")]
    public Task Recovery_checkpoint_write_refuses_missing_begun_owner_authority(string lostAuthority) =>
        WithActiveTurnAsync(async turn =>
        {
            var toolResult = await DispatchProtectedReadAsync(turn);

            var owner = await LoseBegunWriteAuthorityAsync(turn, lostAuthority);

            SessionRecoveryCheckpointStore checkpoints = new(turn.Connections, TimeProvider.System);

            var checkpoint = ReconciliationCheckpoint(turn, toolResult);

            Assert.Equal(ErrorCodes.Hub.SessionRecoveryConflict, (await checkpoints.SaveAsync(owner, checkpoint, default)).Error.Code);

            var retained = await checkpoints.FindAsync(checkpoint.SessionId, checkpoint.Id, default);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Null(retained.Value);

            Assert.Equal(ToolInvocationState.Dispatched, (await ReadProtectedEffectAsync(turn, toolResult)).State);
        });

    [SkippableFact]
    public Task Released_begun_owner_cannot_acknowledge_a_retained_checkpoint_as_its_own_write() =>
        WithActiveTurnAsync(async turn =>
        {
            var toolResult = await DispatchProtectedReadAsync(turn);

            SessionRecoveryCheckpointStore checkpoints = new(turn.Connections, TimeProvider.System);

            var checkpoint = ReconciliationCheckpoint(turn, toolResult);

            var saved = await checkpoints.SaveAsync(turn.Owner, checkpoint, default);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            var replayed = await checkpoints.SaveAsync(turn.Owner, checkpoint, default);

            Assert.True(replayed.IsSuccess, replayed.Error.Message);

            Assert.Equal(checkpoint, replayed.Value);

            turn.Writer.Dispose();

            Assert.Equal(ErrorCodes.Hub.SessionRecoveryConflict, (await checkpoints.SaveAsync(turn.Owner, checkpoint, default)).Error.Code);

            var retained = await checkpoints.FindAsync(checkpoint.SessionId, checkpoint.Id, default);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Equal(checkpoint, retained.Value);
        });

    private static async Task<ToolExecutionPipeline.ProcessedToolCall> DispatchProtectedReadAsync(RecoveryTurn turn)
    {
        const string text = "fixture-private-slow-tool";

        await File.WriteAllTextAsync(Path.Combine(turn.Workspace, "protected-result.txt"), text);

        await using var client = await SdkMcpClientWrapperTests.CreateInitializedClientAsync(turn.Workspace);

        var tool = Assert.Single(await client.GetToolsAsync(), tool => tool.Name == "read_file");

        FunctionCallContent read = new("protected-result", tool.Name, new Dictionary<string, object?>
        {
            ["relativePath"] = "protected-result.txt",
            ["startLine"] = 1,
            ["endLine"] = 1,
        });

        turn.Context.ProtectedContent = true;

        var result = await turn.InvokeAsync(read, new ChatOptions { Tools = [tool] });

        Assert.Contains(text, result.ResultText, StringComparison.Ordinal);

        Assert.NotNull(result.ProtectedInvocationCompletion);

        Assert.Equal(ToolInvocationState.Dispatched, (await ReadProtectedEffectAsync(turn, result)).State);

        return result;
    }

    private static async Task ExpireBegunWriteDeadlineAsync(RecoveryTurn turn)
    {
        var opened = await turn.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var lease = opened.Value;

        await using var update = lease.Connection.CreateCommand();

        update.CommandText = """
            UPDATE session_turn_claims SET LeaseDeadlineUtc=$past
            WHERE ClaimId=$claim AND StateCode=2 AND LeaseDeadlineUtc > $now
            """;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = update.Parameters.AddWithValue("$past", UtcInstantText.Format(now.AddHours(-1)));

        _ = update.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

        _ = update.Parameters.AddWithValue("$claim", turn.Owner.Claim.ClaimId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static async Task<SessionTurnClaimLease> LoseBegunWriteAuthorityAsync(RecoveryTurn turn, string lostAuthority)
    {
        if (lostAuthority == "released")
        {
            turn.Writer.Dispose();

            Assert.True(turn.Owner.HoldsActiveExecution is false);

            return turn.Owner;
        }

        if (lostAuthority == "reservation")
        {
            var copy = turn.Owner with { Claim = turn.Owner.Claim with { FinalizationReservationId = Guid.NewGuid() } };

            Assert.True(copy.HoldsActiveExecution is false);

            return copy;
        }

        if (lostAuthority == "terminal")
        {
            var interruption = new SessionTurnClaimInterruption(turn.Owner,
                SessionTurnClaimOutcome.RestoredInterrupted(ErrorCodes.Hub.SessionTurnRestoredInterrupted, 409, []));

            var finalized = await turn.Transcript.CommitTurnAsync(new TurnCommitRequest(
                turn.Owner.FutureAssistantEntryId, turn.Owner.Claim.SessionId, AssistantFinalizationOutcome.Committed,
                "Retained partial answer", turn.Owner.Claim.RequestDigest, ContentSensitivity.None, GenerationProvenance.CreateExact([]),
                claimInterruption: interruption), default);

            Assert.True(finalized.IsSuccess, finalized.Error.Message);
        }
        else
        {
            await ChangeDurableWriteAuthorityAsync(turn, lostAuthority);
        }

        Assert.Same(turn.Owner, turn.Handle.ClaimLease);

        Assert.True(turn.Owner.HoldsActiveExecution is true);

        return turn.Owner;
    }

    private static async Task ChangeDurableWriteAuthorityAsync(RecoveryTurn turn, string lostAuthority)
    {
        var opened = await turn.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var lease = opened.Value;

        await using var command = lease.Connection.CreateCommand();

        command.CommandText = lostAuthority switch
        {
            "executor" => "UPDATE session_turn_claims SET ExecutorId=$replacement WHERE ClaimId=$id AND StateCode=2",
            "boot" => "UPDATE session_turn_claims SET OwnerBootId=$replacement WHERE ClaimId=$id AND StateCode=2",
            _ => throw new ArgumentOutOfRangeException(nameof(lostAuthority)),
        };

        _ = command.Parameters.AddWithValue("$id", turn.Owner.Claim.ClaimId.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$replacement", Guid.NewGuid().ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static Task AppendProtectedResultAsync(RecoveryTurn turn, ToolExecutionPipeline.ProcessedToolCall result) =>
        turn.Writer.TryAppendToolInteractionAsync(turn.Owner.Claim.SessionId, result.ToolName, result.ArgsSnapshot, result.ResultText,
            "fixture-model", default, ProviderCallSensitivities.Create(ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])), turn.Handle, result.ProtectedInvocationCompletion);

    private static async Task<ToolInvocationEvidence> ReadProtectedEffectAsync(RecoveryTurn turn, ToolExecutionPipeline.ProcessedToolCall result)
    {
        var key = Assert.IsType<ToolInvocationCompletion>(result.ProtectedInvocationCompletion).Key;

        var observed = await turn.Store.ReadAsync(key.SessionId, key.Receipt.Id, default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        return Assert.IsType<ToolInvocationEvidence>(observed.Value);
    }

    private static SessionRecoveryCheckpointRecord ReconciliationCheckpoint(RecoveryTurn turn, ToolExecutionPipeline.ProcessedToolCall result) =>
        new(Guid.NewGuid(), turn.Owner.Claim.SessionId, turn.Owner.FutureAssistantEntryId, turn.Owner.Claim.ClaimId,
            SessionRecoveryKind.ReconciliationRequired, new("fixture-provider", "fixture-model", new CovenantDigest(new byte[32]), null, null, null),
            null, result.ProtectedInvocationCompletion!.Key.Receipt.Id, null, null);
}
