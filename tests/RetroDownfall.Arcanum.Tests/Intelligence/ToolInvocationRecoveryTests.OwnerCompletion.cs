using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Mcp;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ToolInvocationRecoveryTests
{
    [SkippableFact]
    public Task Lost_attachment_acknowledgement_replays_only_the_exact_authorized_snapshot()
    {
        const string originalText = "fixture-private-attachment";
        const string newerText = "unrelated-later-version";

        Dictionary<Guid, SessionAttachmentRecord> records = [];
        List<Guid> reads = [];
        HashSet<Guid> visible = [];
        SessionAttachmentRecord? latest = null;

        return WithActiveTurnAsync(async turn =>
        {
            Guid session = turn.Request.SessionId!.Value;
            SessionAttachmentRecord original = AddVersion(session, 1, originalText);
            visible.Add(original.Id);

            await using var client = await SdkMcpClientWrapperTests.CreateInitializedClientAsync(turn.Workspace, attachments: turn.Attachments);

            var tool = Assert.Single(await client.GetToolsAsync(), tool => tool.Name == "attach_session_file");

            FunctionCallContent call = new("lost-attachment-ack", tool.Name, new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

            ChatOptions options = new() { Tools = [tool] };

            SessionAttachmentToolAmbient.CurrentSessionId = session;

            try
            {
                _ = SessionAttachmentTurnBudget.BeginTurn();

                var first = await turn.InvokeAsync(call, options);

                Assert.Contains(first.AdditionalContextContents!, content => content is TextContent text
                    && text.Text.Contains(originalText, StringComparison.Ordinal));

                Assert.DoesNotContain(originalText, first.ResultText, StringComparison.Ordinal);

                var evidence = await ReadOwnerDispositionAsync(turn, call);

                Assert.Equal(ToolInvocationState.Completed, evidence.State);

                Guid resultEntryId = Assert.IsType<Guid>(evidence.ResultEntryId);

                var receipt = await turn.Transcript.GetEntryByIdAsync(session, resultEntryId, default);

                Assert.NotNull(receipt);
                Assert.DoesNotContain(originalText, receipt.Content, StringComparison.Ordinal);

                // The first send was never observed. A fresh materialization budget has not seen either version.
                SessionAttachmentTurnBudget.EndTurn();
                _ = SessionAttachmentTurnBudget.BeginTurn();

                _ = AddVersion(session, 2, newerText);

                var reattached = await turn.InvokeAsync(call, options);

                Assert.Contains(reattached.AdditionalContextContents!, content => content is TextContent text
                    && text.Text.Contains(originalText, StringComparison.Ordinal));
                Assert.DoesNotContain(reattached.AdditionalContextContents!, content => content is TextContent text
                    && text.Text.Contains(newerText, StringComparison.Ordinal));
                Assert.True(reattached.ReceiptHandled);

                SessionAttachmentTurnBudget.EndTurn();
                _ = SessionAttachmentTurnBudget.BeginTurn();

                SessionAttachmentToolAmbient.CurrentSessionId = Guid.NewGuid();
                Assert.Null((await turn.InvokeAsync(call, options)).AdditionalContextContents);
                SessionAttachmentToolAmbient.CurrentSessionId = session;

                visible.Clear();
                Assert.Null((await turn.InvokeAsync(call, options)).AdditionalContextContents);
                visible.Add(original.Id);

                records[original.Id] = original with { Version = 3 };
                Assert.Null((await turn.InvokeAsync(call, options)).AdditionalContextContents);

                records.Remove(original.Id);
                Assert.Null((await turn.InvokeAsync(call, options)).AdditionalContextContents);

                Assert.Equal([original.Id, original.Id], reads);
            }
            finally
            {
                SessionAttachmentTurnBudget.EndTurn();
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        }, _ => new NoOpSessionAttachmentStore(records: records,
            logicalLookup: (session, _, version) =>
                latest is { } candidate && candidate.SessionId == session
                    && (version is null || candidate.Version == version) ? candidate : null,
            readBytes: (record, _) =>
            {
                reads.Add(record.Id);
                return Task.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(record.Version == 1 ? originalText : newerText));
            }), visible);

        SessionAttachmentRecord AddVersion(Guid session, int version, string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);

            var record = new SessionAttachmentRecord(Guid.NewGuid(), session, null, null, SessionAttachmentState.Bound,
                "notes.txt", "notes.txt", version, $"safe/notes-v{version}", Convert.ToHexStringLower(SHA256.HashData(bytes)),
                "text/plain", bytes.Length, SessionAttachmentKind.Text, DateTimeOffset.UtcNow);

            records.Add(record.Id, record);

            latest = record;

            return record;
        }
    }

    [SkippableFact]
    public Task A_protected_commit_settles_its_effect_with_the_labeled_owner_receipt_only() => WithActiveTurnAsync(async turn =>
    {
        const string privateText = "fixture-protected-verdict";

        await File.WriteAllTextAsync(Path.Combine(turn.Workspace, "protected-notes.txt"), privateText + "\n");

        await using var native = await SdkMcpClientWrapperTests.CreateInitializedClientAsync(turn.Workspace);

        var tool = Assert.Single(await native.GetToolsAsync(), tool => tool.Name == "read_file");

        FunctionCallContent read = new("protected-read", tool.Name, new Dictionary<string, object?>
        {
            ["relativePath"] = "protected-notes.txt",
            ["startLine"] = 1,
            ["endLine"] = 1,
        });

        ChatOptions options = new() { Tools = [tool] };

        turn.Context.ProtectedContent = true;

        var first = await turn.InvokeAsync(read, options);

        Assert.Contains(privateText, first.ResultText, StringComparison.Ordinal);

        Guid session = turn.Request.SessionId!.Value;

        var sensitivity = ProviderCallSensitivities.Create(ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]));

        var effect = Assert.IsType<ToolInvocationCompletion>(first.ProtectedInvocationCompletion);

        int before = (await turn.Transcript.GetSessionEntriesAsync(session))!.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            turn.Writer.TryAppendToolInteractionAsync(session, first.ToolName, first.ArgsSnapshot, first.ResultText,
                "fixture-model", default, sensitivity, turn.Handle, effect with { DispatchId = Guid.NewGuid() }));

        Assert.Equal(before, (await turn.Transcript.GetSessionEntriesAsync(session))!.Count);

        Assert.Equal(ToolInvocationState.Dispatched, (await ReadOwnerDispositionAsync(turn, read)).State);

        await turn.Writer.TryAppendToolInteractionAsync(session, first.ToolName, first.ArgsSnapshot, first.ResultText,
            "fixture-model", default, sensitivity, turn.Handle, effect);

        var completed = await ReadOwnerDispositionAsync(turn, read);

        Assert.Equal(ToolInvocationState.Completed, completed.State);

        var invocation = CaptureOwnerInvocation(turn, read);

        Guid protectedEntryId = Assert.IsType<Guid>(completed.ResultEntryId);

        Assert.NotEqual(invocation.Receipt.ResultEntryId, protectedEntryId);

        var progress = await turn.Store.ReadTurnProgressAsync(session, invocation.AssistantEntryId, default);

        Assert.True(progress.IsSuccess, progress.Error.Message);

        Assert.Null(progress.Value.OutstandingInvocationId);

        Assert.Equal(invocation.Receipt.Id, progress.Value.LastVerifiedInvocationId);

        Assert.Null(await turn.Transcript.GetEntryByIdAsync(session, invocation.Receipt.ResultEntryId, default));

        await File.WriteAllTextAsync(Path.Combine(turn.Workspace, "protected-notes.txt"), "should not be read again\n");

        var replayed = await turn.InvokeAsync(read, options);

        Assert.Equal(TurnEffectState.Completed, replayed.FailureEvidence?.Effect);

        Assert.True(replayed.ReceiptHandled);

        Assert.Equal(TurnRecoveryAction.InspectSession, replayed.FailureEvidence?.NextAction);

        Assert.Equal(protectedEntryId, (await ReadOwnerDispositionAsync(turn, read)).ResultEntryId);

        Assert.DoesNotContain(privateText, replayed.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("should not be read again", replayed.ResultText, StringComparison.Ordinal);
    });

    private static ToolInvocationKey CaptureOwnerInvocation(RecoveryTurn turn, FunctionCallContent call) =>
        ToolInvocationKey.Capture(turn.Request.SessionId!.Value, turn.Context.AssistantEntryId!.Value,
            new(turn.Context.AssistantEntryId.Value.ToString("D"), call.CallId, 1, 0, call.Name!),
            ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(call));

    private static async Task<ToolInvocationEvidence> ReadOwnerDispositionAsync(RecoveryTurn turn, FunctionCallContent call)
    {
        var key = CaptureOwnerInvocation(turn, call);

        var result = await turn.Store.ReadAsync(key.SessionId, key.Receipt.Id, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        return Assert.IsType<ToolInvocationEvidence>(result.Value);
    }
}
