using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class ToolInvocationRecoveryTests(GrimoireFixture database)
{
    [SkippableFact]
    public Task Concurrent_reconnect_keeps_an_interrupted_effect_fenced_without_another_dispatch() => WithActiveTurnAsync(async turn =>
    {
        int handoffs = 0;

        string Effect()
        {
            if (Interlocked.Increment(ref handoffs) == 1)
                throw new IOException("The fixture interrupted the effect before its disposition could be confirmed.");

            return "The physical effect was dispatched again.";
        }

        const string toolName = "invocation_recovery_fixture";

        ChatOptions options = new() { Tools = [AIFunctionFactory.Create(Effect, toolName)] };

        Task<ToolExecutionPipeline.ProcessedToolCall> Invoke() => turn.InvokeAsync(new("stable-provider-call", toolName), options);

        await Assert.ThrowsAsync<IOException>(Invoke);

        var reconnected = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke));

        Assert.Equal(1, handoffs);

        Assert.All(reconnected, response =>
        {
            Assert.True(response.Failed);

            Assert.Equal(TurnRecoveryAction.ReconcileTool, response.FailureEvidence?.NextAction);

            Assert.NotNull(response.FailureEvidence?.InvocationId);
        });

        Assert.Equal(reconnected[0].FailureEvidence?.InvocationId, reconnected[1].FailureEvidence?.InvocationId);
    });

    [SkippableFact]
    public Task A_lost_acknowledgement_reuses_the_existing_patch_receipt_under_the_original_slot() => WithActiveTurnAsync(async turn =>
    {
        const string filename = "effect.txt";

        const string patch = "--- a/effect.txt\n+++ b/effect.txt\n@@ -1 +1 @@\n-before\n+after\n";

        string filepath = Path.Combine(turn.Workspace, filename);

        await File.WriteAllTextAsync(filepath, "before\n");

        ApplyPatchToolExecutionService service = new(turn.Workspace, new WorkspacePatchSettings(), 32_768, McpJsonSerializerContext.Default);

        int handoffs = 0;

        string? originalResult = null;

        async Task<string> PatchAsync(string patch, CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref handoffs);

            ApplyPatchInvocationContext context = Assert.IsType<ApplyPatchInvocationContext>(ApplyPatchInvocationAmbient.Current);

            var result = await service.ExecuteAsync(new(patch), context, cancellationToken);

            if (attempt == 1)
            {
                Assert.Equal("after\n", await File.ReadAllTextAsync(filepath, cancellationToken));

                originalResult = result.SerializedResult;

                throw new IOException("The acknowledgement was lost after the patch and receipt committed.");
            }

            return result.SerializedResult;
        }

        ChatOptions options = new() { Tools = [AIFunctionFactory.Create(PatchAsync, "apply_patch")] };

        Task<ToolExecutionPipeline.ProcessedToolCall> Invoke() => turn.InvokeAsync(new("patch-provider-call", "apply_patch",
            new Dictionary<string, object?> { ["patch"] = patch }), options);

        await Assert.ThrowsAsync<IOException>(Invoke);

        Assert.Equal("after\n", await File.ReadAllTextAsync(filepath));

        Guid assistant = turn.Context.AssistantEntryId!.Value;

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(new(assistant.ToString("D"), "patch-provider-call", 1, 0, "apply_patch"));

        var inspected = await turn.Store.ReadAsync(turn.Request.SessionId!.Value, receipt.Id, default);

        Assert.True(inspected.IsSuccess, inspected.Error.Message);

        Assert.Equal(ToolInvocationState.Completed, inspected.Value!.State);

        Assert.Equal(receipt.ResultEntryId, inspected.Value.ResultEntryId);

        var recovered = await Invoke();

        Assert.Equal(originalResult, recovered.ResultText);

        Assert.Equal(1, handoffs);

        Assert.Equal("after\n", await File.ReadAllTextAsync(filepath));
    });

    [SkippableFact]
    public Task An_exact_retry_after_a_pre_dispatch_interruption_uses_the_current_authorized_tool_once() => WithActiveTurnAsync(async turn =>
    {
        int handoffs = 0;

        string Effect()
        {
            Interlocked.Increment(ref handoffs);

            return "Authorized result.";
        }

        const string toolName = "invocation_recovery_predispatch_fixture";

        ChatOptions options = new() { Tools = [AIFunctionFactory.Create(Effect, toolName)] };

        FunctionCallContent original = new("stable-predispatch-call", toolName);

        await Assert.ThrowsAsync<IOException>(() => turn.InvokeAsync(original, options,
            (_, _) => Task.FromException(new IOException("The fixture interrupted the caller before handoff."))));

        Assert.Equal(0, handoffs);

        var result = await turn.InvokeAsync(original, options);

        Assert.Equal("Authorized result.", result.ResultText);

        Assert.Equal(result.ResultText, (await turn.InvokeAsync(original, options)).ResultText);

        Assert.Equal(1, handoffs);
    });

    [SkippableFact]
    public Task Patch_owner_proof_of_no_mutation_allows_the_same_intent_after_a_fresh_precondition() => WithActiveTurnAsync(async turn =>
    {
        const string patch = "--- a/owner-precondition.txt\n+++ b/owner-precondition.txt\n@@ -1 +1 @@\n-before\n+after\n";

        string filepath = Path.Combine(turn.Workspace, "owner-precondition.txt");

        await File.WriteAllTextAsync(filepath, "different\n");

        ApplyPatchToolExecutionService service = new(turn.Workspace, new WorkspacePatchSettings(), 32_768, McpJsonSerializerContext.Default);

        async Task<object> PatchAsync(string patch, CancellationToken ct)
        {
            ApplyPatchInvocationContext context = Assert.IsType<ApplyPatchInvocationContext>(ApplyPatchInvocationAmbient.Current);

            var result = await service.ExecuteAsync(new(patch), context, ct);

            return new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspacePatch, result.SerializedResult,
                context.ReceiptHandled) { FailureEvidence = result.FailureEvidence };
        }

        ChatOptions options = new() { Tools = [new PatchOwnerFunction(PatchAsync)] };

        FunctionCallContent call = new("owner-precondition", "apply_patch", new Dictionary<string, object?> { ["patch"] = patch });

        var refused = await turn.InvokeAsync(call, options);

        Assert.Equal(TurnEffectState.NotDispatched, refused.FailureEvidence?.Effect);

        Assert.Equal("different\n", await File.ReadAllTextAsync(filepath));

        Guid assistant = turn.Context.AssistantEntryId!.Value;

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(new(assistant.ToString("D"), "owner-precondition", 1, 0, "apply_patch"));

        Assert.Equal(ToolInvocationState.Prepared, (await turn.Store.ReadAsync(turn.Request.SessionId!.Value, receipt.Id, default)).Value!.State);

        await File.WriteAllTextAsync(filepath, "before\n");

        var accepted = await turn.InvokeAsync(call, options);

        Assert.Contains("\"status\":\"ok\"", accepted.ResultText, StringComparison.Ordinal);

        Assert.Equal("after\n", await File.ReadAllTextAsync(filepath));

        Assert.Equal(ToolInvocationState.Completed, (await turn.Store.ReadAsync(turn.Request.SessionId!.Value, receipt.Id, default)).Value!.State);
    });

    [SkippableFact]
    public Task Erasing_a_saved_result_preserves_completion_evidence_and_blocks_another_dispatch() => WithActiveTurnAsync(async turn =>
    {
        int handoffs = 0;

        string Effect()
        {
            Interlocked.Increment(ref handoffs);

            return "Recorded result.";
        }

        const string tool = "recovery_erasure_fixture";

        const string providerCall = "stable-erasure-call";

        ChatOptions options = new() { Tools = [AIFunctionFactory.Create(Effect, tool)] };

        FunctionCallContent call = new(providerCall, tool);

        Assert.Equal("Recorded result.", (await turn.InvokeAsync(call, options)).ResultText);

        Guid session = turn.Request.SessionId!.Value;

        Guid assistant = turn.Context.AssistantEntryId!.Value;

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(new(assistant.ToString("D"), providerCall, 1, 0, tool));

        Assert.True(await turn.Transcript.DeleteEntryAsync(session, receipt.ResultEntryId));

        var retained = await turn.Store.ReadAsync(session, receipt.Id, default);

        Assert.Equal(ToolInvocationState.Completed, retained.Value!.State);

        Assert.Null(retained.Value.ResultEntryId);

        var retry = await turn.InvokeAsync(call, options);

        Assert.Equal(TurnEffectState.Completed, retry.FailureEvidence?.Effect);

        Assert.Equal(receipt.Id, retry.FailureEvidence?.InvocationId);

        Assert.Equal(1, handoffs);
    });

    [SkippableFact]
    public Task Durable_slot_rejects_a_changed_intent_wrong_session_and_stale_executor() => WithActiveTurnAsync(async turn =>
    {
        Guid session = turn.Request.SessionId!.Value;

        Guid assistant = turn.Context.AssistantEntryId!.Value;

        ToolInvocationIdentity identity = new(assistant.ToString("D"), "original", 2, 0, "fixture");

        ToolInvocationKey key = ToolInvocationKey.Capture(session, assistant, identity, "{\"a\":1,\"b\":2}");

        var prepared = await turn.Store.PrepareAsync(key, turn.Owner, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Equal(ToolInvocationState.Prepared, prepared.Value.State);

        ToolInvocationKey canonicalReplay = ToolInvocationKey.Capture(session, assistant, identity, "{\"b\":2,\"a\":1}");

        var replay = await turn.Store.PrepareAsync(canonicalReplay, turn.Owner, default);

        Assert.Equal(prepared.Value, replay.Value);

        ToolInvocationKey conflict = ToolInvocationKey.Capture(session, assistant, identity, "{\"a\":2,\"b\":2}");

        Assert.True((await turn.Store.AdmitAsync(conflict, turn.Owner, default)).IsFailure);

        Assert.True((await turn.Store.AdmitAsync(key, turn.Owner with { ExecutorId = Guid.NewGuid() }, default)).IsFailure);

        Assert.Null((await turn.Store.ReadAsync(Guid.NewGuid(), key.Receipt.Id, default)).Value);

        var contenders = await Task.WhenAll(Task.Run(() => turn.Store.AdmitAsync(key, turn.Owner, default)),
            Task.Run(() => turn.Store.AdmitAsync(key, turn.Owner, default)));

        Assert.All(contenders, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Single(contenders, result => result.Value.MayDispatch);

        Assert.Equal(ToolInvocationState.Dispatched, (await turn.Store.ReadAsync(session, key.Receipt.Id, default)).Value!.State);

        Assert.True((await turn.Store.ConfirmAsync(key, key.Receipt.ResultEntryId, default)).IsFailure);

        await turn.InterruptAsync();

        Assert.True((await turn.Store.AdmitAsync(key, turn.Owner, default)).IsFailure);

        Assert.Equal(ToolInvocationState.Dispatched, (await turn.Store.ReadAsync(session, key.Receipt.Id, default)).Value!.State);
    });

    private async Task WithActiveTurnAsync(Func<RecoveryTurn, Task> test)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = database.CopyDatabase();

        await using ArcanumDbContext db = database.CreateContext(path);

        var settings = new TestOptionsSnapshot<ArcanumSettings>(new());

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            settings, null, null, FixtureOrdinaryConnectionFactory.For(db), FixtureLabeledArtifactGuard.For(db), memoryDelegation: null);

        var created = await repository.CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "effect recovery", default);

        Assert.True(created.IsSuccess, created.Error.Message);

        Guid session = created.Value;

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource((SqliteConnection)db.Database.GetDbConnection()),
            new CovenantQuotaGuard(), Guid.NewGuid());

        var acquired = await claims.AcquireAsync(new(Guid.NewGuid(), 0, Guid.NewGuid(), session, SessionTurnSurface.Intelligence,
            new CovenantDigest(new byte[32]), new CovenantDigest(new byte[32]), null, 0, 0), default);

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        using GrimoireTurnWriter writer = new(repository, repository,
            new SessionEventHub(NullLogger<SessionEventHub>.Instance), NullLogger<GrimoireTurnWriter>.Instance,
            turnCommitter: repository, claimedBeginStore: repository, claims: claims);

        TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        try
        {
            PingRequest request = new("Record the local effect", SessionId: session, WorkingDirectory: workspace.Root);

            var begun = await writer.BeginStreamedAssistantReplyAsync(request, InvocationContexts.AttendedGlobalOnlySession(),
                request.Prompt, "fixture-model", default, acquired.Value);

            Assert.True(begun.IsSuccess, begun.Error.Message);

            Guid assistant = Assert.IsType<Guid>(begun.Value.AssistantEntryId);

            var owner = Assert.IsType<SessionTurnClaimLease>(begun.Value.ClaimLease);

            SqliteToolInvocationStore store = new(FixtureOrdinaryConnectionFactory.For(db), TimeProvider.System);

            ToolExecutionPipeline.TurnContext active = new() { PersistedSessionId = session, AssistantEntryId = assistant,
                InvocationId = assistant.ToString("D"), ClaimLease = owner, ModelUsed = "fixture-model", WorkspaceRoot = workspace.Root };

            SanctumPipelineHarness sanctum = SanctumPipelineHarness.Create(workspace.Root);

            ToolExecutionPipeline NewPipeline() => new(settings, new RecordOnlyWard(), sanctum.Guard, new NoOpSessionAttachmentStore(),
                NullLogger<ToolExecutionPipeline>.Instance, grimoireTurnWriter: writer, toolInvocationStore: store);

            await test(new(workspace.Root, request, active, NewPipeline, store, owner, repository,
                () => writer.TryResolveInterruptedOnStreamExitAsync(begun.Value, "The provider stopped.")));
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    private sealed class PatchOwnerFunction(Func<string, CancellationToken, Task<object>> invoke) : AIFunction
    {
        private static readonly JsonDocument Schema = JsonDocument.Parse("{}");

        public override string Name => "apply_patch";

        public override JsonElement JsonSchema => Schema.RootElement;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments args, CancellationToken ct) =>
            await invoke(Assert.IsType<string>(args["patch"]), ct);
    }

    private sealed record RecoveryTurn(string Workspace, PingRequest Request, ToolExecutionPipeline.TurnContext Context,
        Func<ToolExecutionPipeline> CreatePipeline, SqliteToolInvocationStore Store, SessionTurnClaimLease Owner,
        GrimoireRepository Transcript, Func<Task> InterruptAsync)
    {
        internal Task<ToolExecutionPipeline.ProcessedToolCall> InvokeAsync(FunctionCallContent call, ChatOptions options,
            Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit = null) =>
            CreatePipeline().ProcessSingleToolCallAsync(call, Request, options, null, Request.SessionId!.Value.ToString("D"), Context,
                suppressInvocationFailures: false, cancellationToken: default, liveWardEmit: liveWardEmit, toolRoundOrdinal: 1);
    }
}
