using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Fact]
    public async Task Owner_evidence_distinguishes_stale_patch_from_a_commit()
    {
        _workspace.WriteFile("stale.txt", "newer contents\n");

        await using TestMcpSession session = await CreateSessionAsync();

        RecordingPatchReceiptSink sink = new();

        JsonElement arguments = JsonSerializer.SerializeToElement(new ApplyPatchParams(
            """
            --- a/stale.txt
            +++ b/stale.txt
            @@ -1 +1 @@
            -old contents
            +proposed contents
            """), McpJsonSerializerContext.Default.ApplyPatchParams);

        ApplyPatchInvocationContext invocation = new(
            Guid.NewGuid(), Guid.NewGuid(),
            new ToolInvocationIdentity("patch-provider-turn", "patch-call", 0, 0, ToolRiskClassifier.ApplyPatchToolName),
            arguments.GetRawText(), "fixture-model", DateTimeOffset.UnixEpoch, sink);

        using IDisposable binding = ApplyPatchInvocationAmbient.Begin(invocation);

        McpToolsCallResultWire result = await session.CallToolAsync(ToolRiskClassifier.ApplyPatchToolName, arguments);

        Assert.False(result.IsError);
        Assert.Empty(sink.Receipts);
        Assert.Equal("newer contents\n", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "stale.txt")));

        JsonElement evidence = ReadOwnerEvidence(result);
        Assert.Equal("toolPrecondition", evidence.GetProperty("phase").GetString());
        Assert.Equal("notDispatched", evidence.GetProperty("effect").GetString());
        Assert.Equal("inspectWorkspace", evidence.GetProperty("nextAction").GetString());
        Assert.DoesNotContain("proposed contents", evidence.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_evidence_keeps_complete_failed_check_diagnostics_retrievable_only_in_its_connection()
    {
        string[] messages = Enumerable.Range(0, 300).Select(index => $"error log line {index}: {new string('x', 132)} 🐉").ToArray();

        FakeWorkspaceCheckRuntime runtime = new(new WorkspaceCheckExecutionStatus(true, false, "available"))
        {
            Result = new WorkspaceCheckToolResultEnvelope
            {
                Status = "failed",
                Code = "check_failed",
                ExitCode = 3,
                Diagnostics = messages.Select(message => new WorkspaceCheckToolResultItem(null, null, null, "error", null, message)).ToArray(),
                TotalDiagnosticCount = messages.Length,
                ErrorCount = messages.Length,
                StandardError = string.Join('\n', messages),
            },
        };

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with { ToolOutputCapBytes = 65_536 },
            workspaceCheckRuntime: runtime,
            allowHostProcessTools: false);

        using IDisposable boundSession = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));

        McpToolsCallResultWire result = await session.CallToolAsync(ToolRiskClassifier.WorkspaceCheckToolName,
            JsonSerializer.SerializeToElement(new WorkspaceCheckParams { Profile = "dotnet-build" },
                McpJsonSerializerContext.Default.WorkspaceCheckParams));

        Assert.False(result.IsError);
        Assert.Equal(1, runtime.RunCount);
        using JsonDocument inline = JsonDocument.Parse(Assert.Single(result.Content).Text);
        Assert.True(inline.RootElement.GetProperty("truncated").GetBoolean());

        JsonElement evidence = ReadOwnerEvidence(result);
        Assert.Equal("toolExecution", evidence.GetProperty("phase").GetString());
        Assert.Equal("completed", evidence.GetProperty("effect").GetString());
        Assert.Equal("readToolOutput", evidence.GetProperty("nextAction").GetString());
        JsonElement output = evidence.GetProperty("output");
        Assert.Equal("read_tool_output", output.GetProperty("toolName").GetString());
        string handle = output.GetProperty("handle").GetString()!;
        Assert.Contains("diagnostics", output.GetProperty("streams").EnumerateArray().Select(stream => stream.GetString()));

        JsonRpcResponse listed = await session.SendRequestAsync("tools/list", null);
        McpToolsListResultWire tools = listed.Result!.Value.Deserialize(McpJsonSerializerContext.Default.McpToolsListResultWire)!;
        Assert.Contains(tools.Tools, static tool => tool.Name == "read_tool_output");
        Assert.DoesNotContain(tools.Tools, static tool => tool.Name is "execute_command" or "read_command_output");

        string modelText = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.WorkspaceCheckToolName,
            new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspaceCheck, result.Content[0].Text),
            new ToolResultMaterializer());
        using JsonDocument modelResult = JsonDocument.Parse(modelText);
        Assert.Equal(handle, modelResult.RootElement.GetProperty("output").GetProperty("handle").GetString());

        int statusReadCount = runtime.StatusReadCount;

        await using TestMcpSession other = await CreateSessionAsync(workspaceCheckRuntime: runtime, allowHostProcessTools: false);
        JsonElement firstPage = JsonSerializer.SerializeToElement(
            new ReadCommandOutputParams { Handle = handle, Stream = "diagnostics", Offset = 0, MaxBytes = 1024 },
            McpJsonSerializerContext.Default.ReadCommandOutputParams);

        Assert.True((await other.CallToolAsync("read_tool_output", firstPage)).IsError);
        Assert.True((await session.CallToolAsync("read_command_output", firstPage)).IsError);

        using (PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid())))
        {
            Assert.True((await session.CallToolAsync("read_tool_output", firstPage)).IsError);
        }

        runtime.Status = new WorkspaceCheckExecutionStatus(false, true, "The pinned executable changed.");
        Assert.True((await session.CallToolAsync("read_tool_output", firstPage)).IsError);
        runtime.Status = new WorkspaceCheckExecutionStatus(true, false, "available");

        string complete = await ReadCompleteCommandOutputAsync(session, handle, "diagnostics", maxBytes: 1024, toolName: "read_tool_output");
        using JsonDocument archived = JsonDocument.Parse(complete);
        Assert.Equal(messages, archived.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("message").GetString()));
        Assert.Equal(string.Join('\n', messages), archived.RootElement.GetProperty("standardError").GetString());
        Assert.Equal(1, runtime.RunCount);
        Assert.Equal(statusReadCount, runtime.StatusReadCount);
        Assert.True((await session.CallToolAsync("read_tool_output", firstPage)).IsError);
    }

    [Fact]
    public async Task Owner_evidence_output_expires_on_reconnect()
    {
        FakeWorkspaceCheckRuntime runtime = new(new WorkspaceCheckExecutionStatus(true, false, "available"))
        {
            Result = new WorkspaceCheckToolResultEnvelope
            {
                Status = "failed",
                Code = "check_failed",
                ExitCode = 2,
                StandardError = new string('x', 16_384),
            },
        };

        using IDisposable boundSession = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));

        string handle;
        string? artifactRoot;

        await using (TestMcpSession first = await CreateSessionAsync(workspaceCheckRuntime: runtime))
        {
            McpToolsCallResultWire result = await first.CallToolAsync("workspace_check",
                JsonSerializer.SerializeToElement(new WorkspaceCheckParams { Profile = "dotnet-build" },
                    McpJsonSerializerContext.Default.WorkspaceCheckParams));

            handle = ReadOwnerEvidence(result).GetProperty("output").GetProperty("handle").GetString()!;
            artifactRoot = first.Server.CommandOutputArtifactRootForTests;
            Assert.NotNull(artifactRoot);
            Assert.True(Directory.Exists(artifactRoot));
        }

        Assert.False(Directory.Exists(artifactRoot));

        await using TestMcpSession reopened = await CreateSessionAsync(workspaceCheckRuntime: runtime);
        JsonElement request = JsonSerializer.SerializeToElement(
            new ReadCommandOutputParams { Handle = handle, Stream = "diagnostics", Offset = 0, MaxBytes = 1024 },
            McpJsonSerializerContext.Default.ReadCommandOutputParams);

        Assert.True((await reopened.CallToolAsync("read_tool_output", request)).IsError);
        Assert.Equal(1, runtime.RunCount);
    }

    private static JsonElement ReadOwnerEvidence(McpToolsCallResultWire result)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(result, McpJsonSerializerContext.Default.McpToolsCallResultWire);
        Assert.True(payload.TryGetProperty("_meta", out JsonElement meta), "The owning tool did not report typed failure evidence.");
        return meta.GetProperty("arcanum/turnFailureEvidence");
    }
}
