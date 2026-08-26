using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The Ward, the disclosure, and the ordering an agent-initiated retirement runs under.
/// </summary>
/// <remarks>
/// Entered at <c>ProcessSingleToolCallAsync</c>, which is the method the turn loop calls for every
/// tool a model emits — there is nothing between a model's <c>retire_covenant</c> call and this. The
/// staging material a live turn publishes is stood up around it, because what is under test is what
/// the pipeline does with a retirement, not how a provider attempt comes to have one.
/// </remarks>
public sealed class CovenantAgentRetirementTests
{

    private const string Key = "preference.builds";

    private const string Arguments = """{"key":"preference.builds","lane":"Confirmed"}""";

    [Fact]
    public async Task With_Wards_disabled_a_retirement_is_denied_rather_than_executed_unwarded()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsDisabled());

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        // Switching Wards off removes the operator's only chance to refuse, and silence is not consent
        // to erase their own standing instructions.
        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

        Assert.Contains("Wards are switched off", processed.ResultText, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_turn_with_no_staging_capability_refuses_the_retirement()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

        Assert.Contains("no Covenant staging capability", processed.ResultText, StringComparison.Ordinal);

    }

    /// <summary>
    /// The one Ward that shows something other than the model's arguments. A retirement erases a
    /// standing instruction the operator wrote, so what they approve is the content that disappears.
    /// </summary>
    [Fact]
    public async Task The_operator_is_shown_the_content_about_to_disappear_rather_than_the_arguments()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.Equal(1, ward.WaitCount);

        string shown = ward.LastArguments!.RootElement.GetRawText();

        Assert.Contains(CovenantRetirementHarness.Disclosure, shown, StringComparison.Ordinal);

        Assert.Contains("\"globalContentAppliesAfterwards\":true", shown, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Declining_the_Ward_invokes_no_tool()
    {

        RecordingWard ward = new() { Approve = false };

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.Equal(1, ward.WaitCount);

        Assert.False(harness.ToolRan);

        Assert.Contains("did not approve", processed.ResultText, StringComparison.Ordinal);

    }

    /// <summary>
    /// The journal is written before the effect, not after. A receipt written afterwards cannot record
    /// the one case it exists for: an effect that happened and then lost its answer.
    /// </summary>
    [Fact]
    public async Task A_disclosure_receipt_is_committed_before_the_tool_runs()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.True(harness.ToolRan);

        Assert.Equal(["disclosed", "tool"], harness.Order);

    }

    /// <summary>
    /// A journal that cannot commit stops the retirement entirely, because proceeding would be a
    /// change Arcanum could never account for afterwards.
    /// </summary>
    [Fact]
    public async Task A_journal_that_cannot_commit_stops_the_retirement()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled())
        {
            JournalFailure = new Error(ErrorCodes.Covenant.Unavailable, "The disclosure journal is closed."),
        };

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

    }

    /// <summary>
    /// The carve-out. A target this turn never carried is one the operator has not seen in this
    /// conversation, so it waits for them in person rather than approving itself.
    /// </summary>
    [Fact]
    public async Task A_target_this_turn_never_admitted_cannot_self_approve_under_configured_auto_approval()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(
            ward,
            AutoApprove(CovenantToolNames.RetireCovenant));

        using IDisposable staging = harness.PublishStaging(admitTarget: false);

        _ = await harness.RetireAsync();

        Assert.Equal(1, ward.WaitCount);

        Assert.Equal(0, ward.AutomaticCount);

    }

    /// <summary>
    /// A target the turn did carry may still self-approve, which is what the allowlist is for.
    /// </summary>
    [Fact]
    public async Task A_target_this_turn_admitted_may_self_approve_under_configured_auto_approval()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(
            ward,
            AutoApprove(CovenantToolNames.RetireCovenant));

        using IDisposable staging = harness.PublishStaging(admitTarget: true);

        _ = await harness.RetireAsync();

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(1, ward.AutomaticCount);

        Assert.True(harness.ToolRan);

    }

    [Fact]
    public async Task A_target_the_probe_refuses_never_reaches_a_Ward()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled())
        {
            PreflightFailure = new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant entry is pinned, so the agent may not retire it."),
        };

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.Equal(0, ward.WaitCount);

        Assert.False(harness.ToolRan);

        Assert.Contains("pinned", processed.ResultText, StringComparison.Ordinal);

    }

    private static WardPolicySettings WardsEnabled() =>
        new() { Enabled = true, ForbiddenArts = [] };

    private static WardPolicySettings WardsDisabled() =>
        new() { Enabled = false, ForbiddenArts = [] };

    private static WardPolicySettings AutoApprove(params string[] tools) =>
        new()
        {
            Enabled = true,
            ForbiddenArts = [],
            AutoApprove = new WardAutoApprovePolicySettings { Enabled = true, Tools = [.. tools] },
        };

    private sealed class RecordingWard : IWard
    {

        public bool Approve { get; init; } = true;

        public int WaitCount { get; private set; }

        public int AutomaticCount { get; private set; }

        public JsonDocument? LastArguments { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {

            WaitCount++;

            LastArguments = arguments is null
                ? null
                : JsonDocument.Parse(arguments.RootElement.GetRawText());

            return Task.FromResult(
                new WardResolution(Approve, null, DateTimeOffset.UtcNow, WardResolutionOrigin.Human));

        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {

            AutomaticCount++;

            return new WardResolution(allowed, reason, DateTimeOffset.UtcNow, origin);

        }

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

}
