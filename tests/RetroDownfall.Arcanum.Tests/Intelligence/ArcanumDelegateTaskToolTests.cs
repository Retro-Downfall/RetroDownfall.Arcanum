using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ArcanumDelegateTaskToolTests
{

    [Fact]

    public async Task InvokeAsync_IntersectsDelegatedAttachmentIdsWithMaterializedParentAllowlist()
    {

        Guid allowedAttachmentId = Guid.NewGuid();

        Guid undelegatedAttachmentId = Guid.NewGuid();

        CapturingSubagentRunner runner = new(
            new SubagentRunResult(
                Success: true,
                Summary: "unused",
                RunId: Guid.NewGuid(),
                Usage: new DelegatedManaUsage(1, 0m, 1, Exhausted: false),
                FailureCode: null));

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        using IDisposable scope = AttachmentMemoryGateAmbient.BeginTurn();

        AttachmentMemoryGateAmbient.RegisterMaterialized(
            new AttachmentMemoryProvenance(
                Guid.NewGuid(),
                allowedAttachmentId,
                "allowed",
                1,
                "hash",
                DateTimeOffset.UtcNow,
                "SessionAttachment",
                AttachmentSourceAvailability.Available));

        JsonElement files = JsonSerializer.SerializeToElement(
            new[]
            {
                new
                {
                    path = "notes.txt",
                    content = "secret",
                    attachment_id = undelegatedAttachmentId,
                },
            });

        object? output = await tool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["prompt"] = "Review the delegated attachment.",
                    ["files"] = files,
                    ["max_tokens"] = 100,
                }),
            CancellationToken.None);

        Assert.Equal("Subagent task failed: Explicit files were not delegated by the parent attachment policy.", output);

        Assert.Null(runner.Request);

    }

    [Fact]
    public async Task InvokeAsync_PassesOnlyExplicitPromptFilesAndBudget()
    {
        CapturingSubagentRunner runner = new(
            new SubagentRunResult(
                Success: true,
                Summary: "The child summary.",
                RunId: Guid.NewGuid(),
                Usage: new DelegatedManaUsage(750, 0.01m, 1, Exhausted: false),
                FailureCode: null));
        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: "test-model");
        JsonElement files = JsonSerializer.SerializeToElement(
            new[]
            {
                new { path = "src/A.cs", content = "sealed class A {}" },
            });
        AIFunctionArguments arguments = new(
            new Dictionary<string, object?>
            {
                ["prompt"] = "Review A.",
                ["files"] = files,
                ["max_tokens"] = 1_000,
            });

        object? output = await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.Equal("The child summary.", output);
        Assert.NotNull(runner.Request);
        Assert.Equal("Review A.", runner.Request.Prompt);
        Assert.Equal("test-model", runner.Request.Model);
        Assert.Equal(1_000, runner.Request.MaxTokens);
        AttachedFileDto file = Assert.Single(runner.Request.Files);
        Assert.Equal("src/A.cs", file.RelativePath);
        Assert.Equal("sealed class A {}", file.Content);
    }

    [Fact]
    public async Task InvokeAsync_AcceptsFilesBeyondTheFormerTotalCountCeiling()
    {
        CapturingSubagentRunner runner = new(
            new SubagentRunResult(
                Success: true,
                Summary: "reviewed",
                RunId: Guid.NewGuid(),
                Usage: new DelegatedManaUsage(100, 0.01m, 1, Exhausted: false),
                FailureCode: null));

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        JsonElement files = JsonSerializer.SerializeToElement(
            Enumerable.Range(0, 40)
                .Select(static index => new
                {
                    path = $"src/File{index:D2}.cs",
                    content = $"sealed class File{index:D2} {{}}",
                })
                .ToArray());

        object? output = await tool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["prompt"] = "Review every delegated file.",
                    ["files"] = files,
                    ["max_tokens"] = 2_000,
                }),
            CancellationToken.None);

        Assert.Equal("reviewed", output);

        Assert.Equal(40, runner.Request!.Files.Count);

    }

    [Fact]
    public async Task InvokeAsync_WhenBudgetIsExhausted_ReturnsRequiredParentMessage()
    {
        CapturingSubagentRunner runner = new(
            new SubagentRunResult(
                Success: false,
                Summary: string.Empty,
                RunId: Guid.NewGuid(),
                Usage: new DelegatedManaUsage(1_001, 0.01m, 1, Exhausted: true),
                FailureCode: SubagentFailureCodes.BudgetExhausted));
        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);
        AIFunctionArguments arguments = new(
            new Dictionary<string, object?>
            {
                ["prompt"] = "Do bounded work.",
                ["max_tokens"] = 1_000,
            });

        object? output = await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.Equal(
            "Subagent task failed: Delegated budget exhausted.",
            output);
    }

    [Fact]
    public void ParentContextInjector_AddsExactSystemMessageForBudgetFailureOnly()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "parent request"),
        ];

        SubagentParentContextInjector.AppendBudgetFailureIfRequired(
            messages,
            ArcanumBuiltInToolNames.DelegateTask,
            SubagentParentContextInjector.BudgetExhaustedMessage);

        ChatMessage injected = Assert.Single(
            messages,
            static message => message.Role == ChatRole.System);
        Assert.Equal(
            "Subagent task failed: Delegated budget exhausted.",
            injected.Text);
    }

    private sealed class CapturingSubagentRunner(SubagentRunResult result) : ISubagentRunner
    {
        public SubagentRunRequest? Request { get; private set; }

        public Task<SubagentRunResult> RunAsync(
            SubagentRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(result);
        }
    }
}
