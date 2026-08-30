using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The record-only Ward audit, canonical disclosure, and retained containment an agent-initiated
/// retirement runs under.
/// </summary>
[Collection("Telemetry")]
public sealed class CovenantAgentRetirementTests
{

    [Fact]
    public async Task Disabled_Wards_execute_retirement_without_waiting()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsDisabled());

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.True(harness.ToolRan);

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal([WardResolutionOrigin.Ungated], ward.AutomaticResolutionOrigins);

    }

    [Fact]
    public async Task An_unattended_eligible_turn_executes_retirement()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync(
            InvocationAttendance.Unattended);

        Assert.True(harness.ToolRan);

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

    }

    [Fact]
    public async Task Retirement_records_one_ungated_pair_and_never_requests_approval()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal([WardResolutionOrigin.Ungated], ward.AutomaticResolutionOrigins);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        IntelligenceEvent warded = processed.WardEvents[0];

        IntelligenceEvent resolved = processed.WardEvents[1];

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

        Assert.DoesNotContain(
            harness.ObservedEvents,
            static evt => evt is ToolApprovalRequestedEvent);

    }

    [Fact]
    public async Task Disclosure_acknowledgement_precedes_the_retirement_effect()
    {

        CovenantRetirementHarness harness = new(new RecordingWard(), WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.True(harness.ToolRan);

        Assert.Equal(["disclosed", "tool"], harness.Order);

    }

    [Fact]
    public async Task A_journal_failure_stops_the_retirement_effect()
    {

        CovenantRetirementHarness harness = new(new RecordingWard(), WardsEnabled())
        {
            JournalFailure = new Error(ErrorCodes.Covenant.Unavailable, "The disclosure journal is closed."),
        };

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

    }

    [Fact]
    public async Task A_turn_with_no_staging_ambient_never_reaches_the_effect()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

        Assert.Contains("no Covenant staging capability", processed.ResultText, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("Campaign.A", "Confirmed")]
    [InlineData("preference.builds", "confirmed")]
    public async Task A_malformed_target_never_reaches_the_effect(string key, string lane)
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync(key: key, lane: lane);

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

    }

    [Fact]
    public async Task An_ineligible_invocation_never_reaches_the_effect()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync(eligibleInvocation: false);

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

    }

    [Theory]
    [InlineData("The target is missing.")]
    [InlineData("The target is already a tombstone.")]
    [InlineData("This Covenant entry is pinned.")]
    [InlineData("The target preflight is stale.")]
    public async Task A_target_the_probe_refuses_never_reaches_the_effect(string reason)
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled())
        {
            PreflightFailure = new Error(ErrorCodes.Covenant.StaleSnapshot, reason),
        };

        using IDisposable staging = harness.PublishStaging();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

        Assert.Contains(reason, processed.ResultText, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_mismatched_preflight_target_never_reaches_the_effect()
    {

        RecordingWard ward = new();

        CovenantRetirementHarness harness = new(ward, WardsEnabled());

        using IDisposable staging = harness.PublishStaging(
            CovenantRetirementHarness.Preflight(normalizedKey: "some.other.key"));

        _ = await harness.RetireAsync();

        Assert.False(harness.ToolRan);

        Assert.Equal(0, ward.WaitCount);

    }

    [Fact]
    public async Task The_disclosed_retirement_carries_no_ward_evidence_digest()
    {

        CovenantRetirementHarness harness = new(new RecordingWard(), WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        Assert.Null(Assert.IsType<CovenantDisclosureDraft>(harness.DisclosureDraft).WardEvidenceDigest);

    }

    [Fact]
    public async Task Retirement_records_one_ungated_ward_metric()
    {

        ConcurrentQueue<KeyValuePair<string, object?>[]> measurements = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {

            if (instrument.Name == "arcanum_ward_decisions_total")
            {
                measurements.Enqueue(tags.ToArray());
            }

        });

        listener.Start();

        CovenantRetirementHarness harness = new(new RecordingWard(), WardsEnabled());

        using IDisposable staging = harness.PublishStaging();

        _ = await harness.RetireAsync();

        KeyValuePair<string, object?>[] recorded = Assert.Single(measurements);

        Assert.Equal(CovenantToolNames.RetireCovenant, recorded[0].Value);

        Assert.Equal("ungated", recorded[1].Value);

    }

    private static WardPolicySettings WardsEnabled() =>
        new() { Enabled = true, ForbiddenArts = [] };

    private static WardPolicySettings WardsDisabled() =>
        new() { Enabled = false, ForbiddenArts = [] };

    private sealed class RecordingWard : IWard
    {

        public int WaitCount { get; private set; }

        public List<WardResolutionOrigin> AutomaticResolutionOrigins { get; } = [];

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {

            WaitCount++;

            return Task.FromResult(
                new WardResolution(true, null, DateTimeOffset.UtcNow, WardResolutionOrigin.Human));

        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {

            AutomaticResolutionOrigins.Add(origin);

            return new WardResolution(allowed, reason, DateTimeOffset.UtcNow, origin);

        }

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

}
