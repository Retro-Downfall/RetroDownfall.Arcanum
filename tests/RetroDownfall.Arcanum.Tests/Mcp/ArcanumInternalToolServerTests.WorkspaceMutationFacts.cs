using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Theory]
    [InlineData("write_file", "write", true)]
    [InlineData("edit_file", "edit", true)]
    [InlineData("edit_file", "edit", false)]
    public async Task Native_file_results_report_only_completed_relative_mutations(string tool, string operation, bool succeeds)
    {
        const string path = "docs/notes.txt";
        const string before = "before mutation";
        const string updated = "private updated text";

        string physicalPath = _workspace.WriteFile(path, before);

        await using TestMcpSession session = await CreateSessionAsync();
        using IDisposable binding = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));

        JsonElement args = tool == "write_file"
            ? JsonSerializer.SerializeToElement(new WriteFileParams("docs/./notes.txt", updated), McpJsonSerializerContext.Default.WriteFileParams)
            : JsonSerializer.SerializeToElement(new ReplaceTextBlockParams
            {
                RelativePath = "docs/./notes.txt",
                ExactSearchText = succeeds ? before : "missing exact block",
                ReplacementText = updated,
            }, McpJsonSerializerContext.Default.ReplaceTextBlockParams);

        McpToolsCallResultWire response = await session.CallToolAsync(tool, args);

        Assert.Equal(succeeds ? updated : before, await File.ReadAllTextAsync(physicalPath));
        Assert.Equal(!succeeds, response.IsError);

        if (!succeeds)
        {
            Assert.Null(ReadMutationMetadata(response));
            return;
        }

        JsonElement fact = Assert.Single(Assert.IsType<JsonElement>(ReadMutationMetadata(response)).EnumerateArray());
        Assert.Equal(path, fact.GetProperty("relativePath").GetString());
        Assert.Equal(operation, fact.GetProperty("operation").GetString());
        Assert.Equal(2, fact.EnumerateObject().Count());
    }

    [SkippableFact]
    public async Task Native_file_success_does_not_report_an_undisclosed_symlink_target()
    {
        Skip.If(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(), "Contained symbolic link fixture requires Unix.");

        string target = _workspace.WriteFile("real/notes.txt", "original");
        Directory.CreateSymbolicLink(Path.Combine(_workspace.Root, "alias"), Path.GetDirectoryName(target)!);

        await using TestMcpSession session = await CreateSessionAsync();
        using IDisposable binding = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));
        JsonElement args = JsonSerializer.SerializeToElement(new WriteFileParams("alias/notes.txt", "after"),
            McpJsonSerializerContext.Default.WriteFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("write_file", args);

        Assert.False(result.IsError);
        Assert.Equal("after", await File.ReadAllTextAsync(target));
        Assert.Contains("alias", Assert.Single(result.Content).Text, StringComparison.Ordinal);
        Assert.Null(ReadMutationMetadata(result));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Native_patch_reports_only_committed_complete_visible_file_details(bool dryRun, bool forceOmission)
    {
        string[] paths = forceOmission
            ? [new string('d', 90) + ".txt", new string('e', 90) + ".txt", new string('f', 90) + ".txt"]
            : ["edit.txt", "delete.txt"];

        foreach (string path in paths)
        {
            _workspace.WriteFile(path, "before\n");
        }

        string patch = string.Join('\n', paths.Select((path, index) => index == 1 && !forceOmission
            ? $"--- a/{path}\n+++ /dev/null\n@@ -1 +0,0 @@\n-before"
            : $"--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-before\n+after"));

        await using TestMcpSession session = await CreateSessionAsync(maxJsonRpcLineBytes: forceOmission ? 9_216 : 2_097_152);
        RecordingPatchReceiptSink sink = new();
        JsonElement args = JsonSerializer.SerializeToElement(new ApplyPatchParams(patch, dryRun),
            McpJsonSerializerContext.Default.ApplyPatchParams);
        ApplyPatchInvocationContext invocation = new(Guid.NewGuid(), Guid.NewGuid(),
            new ToolInvocationIdentity("patch-fact-turn", "patch-call", 0, 0, "apply_patch"),
            args.GetRawText(), "fixture-model", DateTimeOffset.UnixEpoch, sink);
        using IDisposable binding = ApplyPatchInvocationAmbient.Begin(invocation);

        try
        {
            McpToolsCallResultWire response = await session.CallToolAsync("apply_patch", args);
            Assert.False(response.IsError);
            using JsonDocument body = JsonDocument.Parse(Assert.Single(response.Content).Text);
            Assert.Equal(dryRun ? "dry_run" : "ok", body.RootElement.GetProperty("status").GetString());

            Assert.Equal(dryRun ? "before\n" : "after\n", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, paths[0])));

            if (dryRun)
            {
                Assert.Empty(sink.Receipts);
                Assert.Null(ReadMutationMetadata(response));
                return;
            }

            Assert.Single(sink.Receipts);

            if (forceOmission)
            {
                Assert.True(body.RootElement.GetProperty("omittedFileCount").GetInt32() > 0);
                Assert.Null(ReadMutationMetadata(response));
                return;
            }

            Assert.False(File.Exists(Path.Combine(_workspace.Root, paths[1])));
            JsonElement[] facts = Assert.IsType<JsonElement>(ReadMutationMetadata(response)).EnumerateArray().ToArray();
            Assert.Collection(facts,
                fact =>
                {
                    Assert.Equal("edit.txt", fact.GetProperty("relativePath").GetString());
                    Assert.Equal("modify", fact.GetProperty("operation").GetString());
                    Assert.Equal(2, fact.EnumerateObject().Count());
                },
                fact =>
                {
                    Assert.Equal("delete.txt", fact.GetProperty("relativePath").GetString());
                    Assert.Equal("delete", fact.GetProperty("operation").GetString());
                    Assert.Equal(2, fact.EnumerateObject().Count());
                });
        }
        finally
        {
            foreach (PendingApplyPatchReceipt receipt in sink.Receipts)
            {
                await receipt.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private static JsonElement? ReadMutationMetadata(McpToolsCallResultWire result)
    {
        JsonElement wire = JsonSerializer.SerializeToElement(result, McpJsonSerializerContext.Default.McpToolsCallResultWire);
        return wire.TryGetProperty("_meta", out JsonElement meta)
            && meta.TryGetProperty("arcanum/committedWorkspaceMutations", out JsonElement facts) ? facts : null;
    }
}
