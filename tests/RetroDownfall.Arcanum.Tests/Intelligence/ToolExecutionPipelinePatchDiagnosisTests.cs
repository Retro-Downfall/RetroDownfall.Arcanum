using System.Reflection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolExecutionPipelinePatchDiagnosisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-patch-diagnosis-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SanctumMode.Strict, "malformed", "invalid_hunk", "hunk header is malformed")]
    [InlineData(SanctumMode.AuditOnly, "malformed", "invalid_hunk", "hunk header is malformed")]
    [InlineData(SanctumMode.Strict, "oversized", "patch_too_large", "Split the diff into smaller apply_patch calls")]
    [InlineData(SanctumMode.AuditOnly, "oversized", "patch_too_large", "Split the diff into smaller apply_patch calls")]
    [InlineData(SanctumMode.Strict, "missing", "invalid_patch", "patch must be a string")]
    [InlineData(SanctumMode.AuditOnly, "missing", "invalid_patch", "patch must be a string")]
    [InlineData(SanctumMode.Strict, "wrong-type", "invalid_patch", "patch must be a string")]
    [InlineData(SanctumMode.AuditOnly, "wrong-type", "invalid_patch", "patch must be a string")]
    public async Task Patch_validation_preserves_parser_diagnosis_without_claiming_a_policy_breach(
        SanctumMode mode, string scenario, string code, string remedy)
    {
        SanctumPipelineHarness harness = CreateHarness(mode);

        Dictionary<string, object?> arguments = [];

        if (scenario != "missing")
        {
            arguments["patch"] = scenario switch
            {
                "oversized" => new string('x', checked((int)ArcanumRuntimeDefaults.CodingTools.Patch.MaxPatchBytes + 1)),
                "wrong-type" => 42,
                _ => "--- a/private-token.txt\n+++ b/private-token.txt\n@@ malformed @@\n-before\n+after\n",
            };
        }

        (ToolExecutionPipeline.ProcessedToolCall result, bool invoked) = await ProcessAsync(harness, mode, arguments);

        Assert.Contains(code, result.ResultText, StringComparison.Ordinal);

        Assert.Contains(remedy, result.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("Sanctum Guard has blocked", result.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("update the Sanctum config", result.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("private-token", result.ResultText, StringComparison.Ordinal);

        Assert.False(invoked);

        Assert.Empty(harness.Breaches.Records);

        Assert.Empty(harness.Guard.PathChecks);

        Assert.Equal([IntelligenceEventType.Warded, IntelligenceEventType.WardResolved], result.WardEvents.Select(static item => item.Type));
    }

    [Theory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public async Task Unavailable_policy_names_repair_without_suggesting_permission_relaxation(SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode);

        (ToolExecutionPipeline.ProcessedToolCall result, bool invoked) = await ProcessAsync(harness, mode,
            new Dictionary<string, object?> { ["patch"] = "not a patch" }, policyUnreadable: true);

        Assert.Contains("could not be read", result.ResultText, StringComparison.Ordinal);

        Assert.Contains("operator", result.ResultText, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("allow this operation", result.ResultText, StringComparison.Ordinal);

        Assert.False(invoked);

        Assert.True(result.Denied);
    }

    [Fact]
    public async Task Audit_record_failure_names_storage_recovery_without_suggesting_permission_relaxation()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, disablePatch: true);

        harness.Breaches.RecordFailure = new IOException("private-storage-path");

        (ToolExecutionPipeline.ProcessedToolCall result, bool invoked) = await ProcessAsync(harness, SanctumMode.AuditOnly,
            new Dictionary<string, object?> { ["patch"] = "not a patch" });

        Assert.Contains("could not be recorded", result.ResultText, StringComparison.Ordinal);

        Assert.Contains("storage", result.ResultText, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("allow this operation", result.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("private-storage-path", result.ResultText, StringComparison.Ordinal);

        Assert.False(invoked);

        Assert.True(result.Denied);
    }

    [Fact]
    public async Task Strict_disabled_tool_still_precedes_argument_validation_and_records_breach()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.Strict, disablePatch: true);

        (ToolExecutionPipeline.ProcessedToolCall result, bool invoked) = await ProcessAsync(harness, SanctumMode.Strict,
            new Dictionary<string, object?> { ["patch"] = "not a patch" });

        Assert.True(result.Denied);

        Assert.Contains("DisabledTool", result.ResultText, StringComparison.Ordinal);

        Assert.False(invoked);

        Assert.Equal("DisabledTool", Assert.Single(harness.Breaches.Records).BreachType);
    }

    public void Dispose() => _ = TestDirectoryCleanup.TryDelete(_root, nameof(ToolExecutionPipelinePatchDiagnosisTests));

    private SanctumPipelineHarness CreateHarness(SanctumMode mode, bool disablePatch = false)
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root);

        CampaignRepository.SetSanctumConfig(harness.Campaign, new SanctumConfig
        {
            Enabled = true,
            Mode = mode,
            EnforcePathBoundary = true,
            DisabledTools = disablePatch ? ["apply_patch"] : [],
        });

        return harness;
    }

    private static async Task<(ToolExecutionPipeline.ProcessedToolCall Result, bool Invoked)> ProcessAsync(
        SanctumPipelineHarness harness, SanctumMode mode, Dictionary<string, object?> arguments, bool policyUnreadable = false)
    {
        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings { Ward = new WardPolicySettings { ForbiddenArts = [] } },
        };

        using GrimoireTurnWriter writer = new(
            DispatchProxy.Create<IGrimoireRepository, UnreachedPatchReceiptRepository>(),
            new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance);

        ToolExecutionPipeline pipeline = new(new TestOptionsSnapshot<ArcanumSettings>(settings), new RecordOnlyWard(),
            harness.Guard, new NoOpSessionAttachmentStore(), NullLogger<ToolExecutionPipeline>.Instance,
            grimoireTurnWriter: writer);

        bool invoked = false;

        AIFunction tool = AIFunctionFactory.Create(() => { invoked = true; return "ran"; }, "apply_patch");

        ToolExecutionPipeline.ProcessedToolCall result = await pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent("patch-diagnosis", "apply_patch", arguments),
            new PingRequest("patch", WorkingDirectory: harness.WorkspaceRoot),
            new ChatOptions { Tools = [tool] }, activeSpell: null, sessionId: "patch-session",
            new ToolExecutionPipeline.TurnContext
            {
                Campaign = harness.Campaign,
                CampaignId = harness.CampaignId,
                WorkspaceRoot = harness.WorkspaceRoot,
                SanctumEnabled = true,
                SanctumMode = mode,
                SanctumPolicyUnreadable = policyUnreadable,
                PersistedSessionId = Guid.NewGuid(),
                AssistantEntryId = Guid.NewGuid(),
                InvocationId = "patch-invocation",
                ModelUsed = "fixture-model",
            }, suppressInvocationFailures: false, CancellationToken.None);

        return (result, invoked);
    }
}

/// <summary>Any receipt or repository effect before patch validation fails the fixture.</summary>
public class UnreachedPatchReceiptRepository : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException($"Patch validation reached repository member {targetMethod?.Name}.");
}
