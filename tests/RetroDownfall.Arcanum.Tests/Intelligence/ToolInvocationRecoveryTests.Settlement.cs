using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ToolInvocationRecoveryTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Sibling_settlement_native_patch_replay_keeps_the_owner_verdict_and_never_writes_twice(bool incomplete) => WithActiveTurnAsync(async turn =>
    {
        const string patch = """
            --- a/one.txt
            +++ b/one.txt
            @@ -1 +1 @@
            -one
            +ONE
            --- a/two.txt
            +++ b/two.txt
            @@ -1 +1 @@
            -two
            +TWO
            """;

        string one = Path.Combine(turn.Workspace, "one.txt");
        string two = Path.Combine(turn.Workspace, "two.txt");

        await File.WriteAllTextAsync(one, "one\n");
        await File.WriteAllTextAsync(two, "two\n");

        int physicalWrites = 0;

        await using NativeTools host = await OpenNativeToolsAsync(turn,
            root => new MultiFileCommitCoordinator(root, new MultiFileCommitCoordinatorOptions
            {
                AfterCommitStepAsync = (step, _) =>
                {
                    Assert.Equal(0, step.Index);
                    Assert.Equal("ONE\n", File.ReadAllText(one));
                    physicalWrites++;

                    if (incomplete)
                    {
                        File.WriteAllText(one, "external\n");
                    }

                    return ValueTask.CompletedTask;
                },
                BeforeCommitStepAsync = (step, _) => step.Index == 1
                    ? ValueTask.FromException(new IOException("Fixture interrupted the second file commit."))
                    : ValueTask.CompletedTask,
            }));
        AIFunction tool = await NativeToolAsync(host, "apply_patch", TrustedStructuredToolResultKind.WorkspacePatch);
        FunctionCallContent call = new("rolled-back-patch", tool.Name, new Dictionary<string, object?> { ["patch"] = patch });
        ChatOptions options = new() { Tools = [tool] };

        var rolledBack = await turn.InvokeAsync(call, options);

        Assert.Equal(1, physicalWrites);
        Assert.Equal(incomplete ? "external\n" : "one\n", await File.ReadAllTextAsync(one));
        Assert.Equal("two\n", await File.ReadAllTextAsync(two));
        TurnEffectState effect = incomplete ? TurnEffectState.Unknown : TurnEffectState.Completed;
        ToolInvocationState disposition = incomplete ? ToolInvocationState.Dispatched : ToolInvocationState.Completed;
        Assert.Equal(effect, rolledBack.FailureEvidence?.Effect);
        ToolInvocationEvidence initial = await ReadDispositionAsync(turn, call);

        Assert.Equal(disposition, initial.State);

        var progress = await turn.Store.ReadTurnProgressAsync(turn.Request.SessionId!.Value, initial.AssistantEntryId, default);

        Assert.True(progress.IsSuccess, progress.Error.Message);

        Assert.Equal(incomplete ? initial.ReceiptId : null, progress.Value.OutstandingInvocationId);

        Assert.Equal(incomplete ? null : initial.ReceiptId, progress.Value.LastVerifiedInvocationId);

        var replayed = await turn.InvokeAsync(call, options);

        Assert.Equal(1, physicalWrites);
        Assert.Equal(rolledBack.ResultText, replayed.ResultText);
        Assert.Equal(effect, replayed.FailureEvidence?.Effect);
        Assert.Equal(disposition, (await ReadDispositionAsync(turn, call)).State);

        if (incomplete)
        {
            Assert.Equal(TurnRecoveryAction.ReconcileTool, replayed.FailureEvidence?.NextAction);
            Assert.NotNull(replayed.FailureEvidence?.InvocationId);
        }
        else
        {
            TurnFailureEvidence original = Assert.IsType<TurnFailureEvidence>(rolledBack.FailureEvidence);

            TurnFailureEvidence restored = Assert.IsType<TurnFailureEvidence>(replayed.FailureEvidence);

            Assert.Equal(original with { Output = null }, restored with { Output = null });

            TurnFailureOutput originalOutput = Assert.IsType<TurnFailureOutput>(original.Output);

            TurnFailureOutput restoredOutput = Assert.IsType<TurnFailureOutput>(restored.Output);

            Assert.Equal(originalOutput.ToolName, restoredOutput.ToolName);

            Assert.Equal(originalOutput.Handle, restoredOutput.Handle);

            Assert.Equal<string>(originalOutput.Streams, restoredOutput.Streams);
        }
    });

    [SkippableFact]
    public Task Sibling_settlement_a_native_workspace_check_refused_before_process_launch_has_no_pending_effect() => WithActiveTurnAsync(async turn =>
    {
        await using NativeTools host = await OpenNativeToolsAsync(turn);
        AIFunction tool = await NativeToolAsync(host, "workspace_check", TrustedStructuredToolResultKind.WorkspaceCheck);
        FunctionCallContent invalid = new("invalid-check-profile", tool.Name, new Dictionary<string, object?> { ["profile"] = "" });

        var refused = await turn.InvokeAsync(invalid, new ChatOptions { Tools = [tool] });

        Assert.Equal(TurnFailurePhase.ToolArguments, refused.FailureEvidence?.Phase);
        Assert.Equal(TurnEffectState.NotDispatched, refused.FailureEvidence?.Effect);
        Assert.Equal(ToolInvocationState.Prepared, (await ReadDispositionAsync(turn, invalid)).State);
    });

    [SkippableFact]
    public Task Sibling_settlement_a_native_missing_read_file_failure_has_no_pending_effect() => WithActiveTurnAsync(async turn =>
    {
        await using NativeTools host = await OpenNativeToolsAsync(turn);
        AIFunction tool = await NativeToolAsync(host, "read_file");
        FunctionCallContent missing = new("missing-read", tool.Name, new Dictionary<string, object?>
        {
            ["relativePath"] = "missing.txt",
            ["startLine"] = 1,
            ["endLine"] = 1,
        });
        ChatOptions options = new() { Tools = [tool] };

        var refused = await turn.InvokeAsync(missing, options);

        Assert.Equal(ToolOutcomeCode.NotFound, refused.ToolOutcome?.Code);
        Assert.Equal(ToolInvocationState.Prepared, (await ReadDispositionAsync(turn, missing)).State);

        await File.WriteAllTextAsync(Path.Combine(turn.Workspace, "missing.txt"), "now exists\n");

        var accepted = await turn.InvokeAsync(missing, options);

        Assert.Contains("now exists", accepted.ResultText, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationState.Completed, (await ReadDispositionAsync(turn, missing)).State);
    });

    private static async Task<ToolInvocationEvidence> ReadDispositionAsync(RecoveryTurn turn, FunctionCallContent call)
    {
        string assistant = turn.Context.AssistantEntryId!.Value.ToString("D");
        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(new(assistant, call.CallId, 1, 0, call.Name!));

        var result = await turn.Store.ReadAsync(turn.Request.SessionId!.Value, receipt.Id, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        return Assert.IsType<ToolInvocationEvidence>(result.Value);
    }

    private static async Task<NativeTools> OpenNativeToolsAsync(RecoveryTurn turn,
        Func<string, MultiFileCommitCoordinator>? patchCoordinator = null)
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var (toServer, fromServer, server) = InProcessMcpTransport.CreateServerChannelPair(
            new HumanPromptRegistry(), services.GetRequiredService<IServiceScopeFactory>(), new InertPacer(), Path.GetFullPath(turn.Workspace),
            listDirectoryMaxPaths: 32, intelligenceSettings: new IntelligenceSettings(), maxFileReadSizeBytes: 1024 * 1024,
            conclaveEnabled: false, sagaEnabled: false, a2aClientEnabled: false, attachmentsToolEnabled: false,
            maxJsonRpcLineBytes: 1_048_576, logger: NullLogger<ArcanumInternalToolServer>.Instance,
            workspaceCheckRuntime: new RejectBeforeLaunch());
        server.PatchCoordinatorForTests = patchCoordinator;
        ChannelClientTransport transport = new(toServer, fromServer, maxJsonRpcLineBytes: 1_048_576, ambientConnectionKey: server.AmbientConnectionKey);
        SdkMcpClientWrapper client = new(transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "arcanum-tests", Version = "1.0.0" } },
            TimeSpan.FromSeconds(10), 32_768, 1_048_576, new McpElicitationSink(), internalServer: server);

        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationTokenSource stop = new();
        Task run = Task.Run(() => server.RunAsync(stop.Token));
        NativeTools host = new(client, services, stop, run);

        try
        {
            await client.InitializeAsync(connect.Token);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task<AIFunction> NativeToolAsync(NativeTools host, string name, TrustedStructuredToolResultKind? resultKind = null)
    {
        McpBridgeTool declared = Assert.Single(await host.Client.GetToolsAsync(), tool => tool.Name == name);
        return resultKind is { } kind ? declared.WithTrustedStructuredResult(kind) : declared;
    }

    private sealed class NativeTools(SdkMcpClientWrapper client, ServiceProvider services, CancellationTokenSource stop, Task run) : IAsyncDisposable
    {
        internal SdkMcpClientWrapper Client => client;

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await stop.CancelAsync();

            try
            {
                await run;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }

            stop.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class RejectBeforeLaunch : IWorkspaceCheckRuntime
    {
        public WorkspaceCheckExecutionStatus GetStatus(string workspaceRoot) => new(true, false, "eligible fixture");
        public bool CanReadRetainedOutput(string workspaceRoot) => false;
        public Task<WorkspaceCheckToolResultEnvelope> RunAsync(WorkspaceCheckRuntimeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A missing profile must be refused by the actual native owner before its process boundary.");
    }

    private sealed class InertPacer : IUnseenServantPacer
    {
        public Task<bool> SetDynamicIntervalAsync(string jobName, int intervalMinutes, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public int GetEffectiveInterval(UnseenServantJob job) => 0;

        public Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
