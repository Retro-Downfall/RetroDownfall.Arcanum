using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ArcanumDelegateTaskToolTests
{
    [Fact]
    public async Task Ordinary_delegation_requires_an_explicit_cap_in_schema_and_invocation()
    {
        CapturingSubagentRunner runner = new(new SubagentRunResult(true, "effect", Guid.NewGuid(),
            new DelegatedManaUsage(1, 0m, 1, Exhausted: false), null));

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        Assert.Equal(2, tool.JsonSchema.GetProperty("anyOf").GetArrayLength());

        object? result = await tool.InvokeAsync(new AIFunctionArguments { ["prompt"] = "task" });

        Assert.Equal("Subagent task failed: A delegated token or cost budget is required.", result);

        Assert.Null(runner.Request);
    }

    [Fact]
    public async Task Guild_direct_native_delegation_refuses_before_starting_a_subagent()
    {
        CapturingSubagentRunner runner = new(new SubagentRunResult(true, "effect", Guid.NewGuid(),
            new DelegatedManaUsage(1, 0m, 1, Exhausted: false), null));

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        using IDisposable scope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

        object? output = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["prompt"] = "delegate effect",
            ["max_tokens"] = 10,
        }));

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, output?.ToString());

        Assert.Null(runner.Request);
    }

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
            }, AdHocJson.Options);

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
            }, AdHocJson.Options);
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
                .ToArray(), AdHocJson.Options);

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

    public static IEnumerable<object?[]> InvalidSuppliedCaps()
    {
        object?[] invalidCost = [null, 0m, -1m, "invalid", double.NaN, double.PositiveInfinity,
            double.NegativeInfinity, double.MaxValue, JsonValue("null"), JsonValue("true"),
            JsonValue("1e100"), JsonValue("-0.1")];

        foreach (object? invalid in invalidCost)
        {
            yield return ["max_cost_usd", invalid];
        }

        object?[] invalidTokens = [null, 0L, -1L, "invalid", double.NaN, double.PositiveInfinity,
            double.MaxValue, 1.5m, JsonValue("null"), JsonValue("true"),
            JsonValue("9223372036854775808"), JsonValue("1.5")];

        foreach (object? invalid in invalidTokens)
        {
            yield return ["max_tokens", invalid];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSuppliedCaps))]
    public async Task Every_supplied_cap_must_be_valid_even_when_the_other_cap_is_valid(string key, object? invalid)
    {
        CapturingSubagentRunner runner = SuccessfulRunner();

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        AIFunctionArguments arguments = new()
        {
            ["prompt"] = "bounded task",
            ["max_tokens"] = 100L,
            ["max_cost_usd"] = 1m,
        };

        arguments[key] = invalid;

        object? result = await tool.InvokeAsync(arguments);

        Assert.Equal("Subagent task failed: A delegated token or cost budget is required.", result);

        Assert.Null(runner.Request);
    }

    [Theory]
    [MemberData(nameof(InvalidSuppliedCaps))]
    public async Task Invalid_supplied_caps_are_refused_before_file_or_memory_grant_processing(string key, object? invalid)
    {
        CapturingSubagentRunner runner = SuccessfulRunner();

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        AIFunctionArguments arguments = new()
        {
            ["prompt"] = "bounded task",
            ["max_tokens"] = 100L,
            ["max_cost_usd"] = 1m,
            ["files"] = JsonValue("false"),
            ["memory_grant"] = "invalid",
        };

        arguments[key] = invalid;

        object? result = await tool.InvokeAsync(arguments);

        Assert.Equal("Subagent task failed: A delegated token or cost budget is required.", result);

        Assert.Null(runner.Request);
    }

    public static IEnumerable<object[]> ExactCostInputs()
    {
        yield return [0.7d, "0.7"];

        yield return [0.7000000000000001d, "0.7000000000000001"];

        yield return [0.7000000000000000000000000001m, "0.7000000000000000000000000001"];

        yield return [JsonValue("0.7000000000000000000000000001"), "0.7000000000000000000000000001"];
    }

    [Theory]
    [MemberData(nameof(ExactCostInputs))]
    public async Task Accepted_costs_keep_their_declared_numeric_value(object supplied, string expectedText)
    {
        decimal expected = decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);

        CapturingSubagentRunner runner = SuccessfulRunner();

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        object? result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["prompt"] = "cost-only task",
            ["max_cost_usd"] = supplied,
        });

        Assert.Equal("summary", result);

        Assert.NotNull(runner.Request);

        Assert.Null(runner.Request.MaxTokens);

        Assert.Equal(expected, runner.Request.MaxCostUsd);

        DelegatedManaTracker tracker = new(null, runner.Request.MaxCostUsd);

        tracker.RecordUsage(null, expected);

        Assert.False(tracker.GetUsage().Exhausted);

        BudgetExhaustedException failure = Assert.Throws<BudgetExhaustedException>(
            () => tracker.RecordUsage(null, 0.0000000000000000000000000001m));

        Assert.Equal(DelegatedBudgetExhaustionReason.Cost, failure.Reason);
    }

    [Fact]
    public async Task A_genuinely_omitted_cost_dimension_retains_token_only_delegation()
    {
        CapturingSubagentRunner runner = SuccessfulRunner();

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        Assert.Equal("summary", await tool.InvokeAsync(new AIFunctionArguments
        {
            ["prompt"] = "token-only task",
            ["max_tokens"] = 100L,
        }));

        Assert.Equal(100L, runner.Request!.MaxTokens);

        Assert.Null(runner.Request.MaxCostUsd);
    }

    [Fact]
    public async Task Both_valid_numeric_string_caps_are_preserved_together()
    {
        CapturingSubagentRunner runner = SuccessfulRunner();

        ArcanumDelegateTaskTool tool = new(runner, inheritedModel: null);

        Assert.Equal("summary", await tool.InvokeAsync(new AIFunctionArguments
        {
            ["prompt"] = "bounded task",
            ["max_tokens"] = "100",
            ["max_cost_usd"] = "0.7",
        }));

        Assert.Equal(100L, runner.Request!.MaxTokens);

        Assert.Equal(0.7m, runner.Request.MaxCostUsd);
    }

    private static CapturingSubagentRunner SuccessfulRunner() => new(new SubagentRunResult(
        true, "summary", Guid.NewGuid(), new DelegatedManaUsage(1, 0m, 1, Exhausted: false), null));

    private static JsonElement JsonValue(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
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
