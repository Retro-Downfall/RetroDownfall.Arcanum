using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("native-opt-in")]
    [InlineData("protected-path-opt-in")]
    [InlineData("protected")]
    [InlineData("external")]
    [InlineData("claimed-host")]
    public async Task Completed_write_facts_require_native_unprotected_tool_execution(string route)
    {
        using ArcanumTestHomeScope home = new("workspace-mutation-facts");

        const string body = "test-only mutation contents";
        const string externalText = "Wrote file 'report.txt'.";

        string path = route == "protected-path-opt-in" ? ".git/private-note.txt" : "report.txt";

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(
            settings: new ArcanumSettings
            {
                Workspaces = new WorkspaceSettings { AllowProtectedPathWrites = route is "native-opt-in" or "protected-path-opt-in" },
            },
            configureServices: services => services.AddSingleton<ISanctumGuard>(new ConfigurableSanctumGuard()));
        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);
        McpBridgeTool selected = Assert.IsType<McpBridgeTool>(tools.Single(static tool => tool.Name == "write_file"));

        if (route is "external" or "claimed-host")
        {
            selected = selected.WithClient(new WorkspaceOutcomeExternalClient(new CallToolResult
            {
                Content = [new TextContentBlock { Text = externalText }],
                Meta = new JsonObject
                {
                    [WorkspaceMutationFact.MetadataName] = JsonNode.Parse("""[{"relativePath":"report.txt","operation":"write"}]"""),
                },
            }, claimsHostOwnership: route == "claimed-host"));
        }

        ToolExecutionPipeline.ProcessedToolCall processed = await ToolExecutionPipelineTests.CreatePipeline(materializer: new ToolResultMaterializer())
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("write", "write_file", new Dictionary<string, object?>
                {
                    ["relativePath"] = path,
                    ["content"] = body,
                }),
                WorkspaceOutcomeRequest(_workspace.Root),
                new ChatOptions { Tools = [selected] },
                null,
                null,
                new ToolExecutionPipeline.TurnContext
                {
                    WorkspaceRoot = _workspace.Root,
                    PersistedSessionId = Guid.NewGuid(),
                    AssistantEntryId = Guid.NewGuid(),
                    ProtectedContent = route == "protected",
                },
                suppressInvocationFailures: false,
                CancellationToken.None);

        Assert.False(processed.Failed);
        Assert.False(processed.Denied);

        string physicalPath = Path.Combine(_workspace.Root, path);

        if (route is not ("external" or "claimed-host"))
        {
            Assert.Equal(body, await File.ReadAllTextAsync(physicalPath));
            Assert.Contains("Wrote file", processed.ResultText, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(File.Exists(physicalPath));
            Assert.Equal(externalText, processed.ResultText);
        }

        if (route is "native" or "native-opt-in")
        {
            Assert.Equal(new WorkspaceMutationFact(path, WorkspaceMutationOperation.Write),
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WorkspaceMutationFact>>(processed.CommittedWorkspaceMutations)));
        }
        else
        {
            Assert.Null(processed.CommittedWorkspaceMutations);
        }
    }

    [Theory]
    [InlineData("""[{"relativePath":"report.txt","operation":1}]""")]
    [InlineData("""[{"relativePath":"report.txt","operation":"write","content":"private bytes"}]""")]
    public void Native_mutation_metadata_rejects_numeric_operations_and_unexpected_payload_fields(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, McpJsonSerializerContext.Default.WorkspaceMutationFactArray));
}
