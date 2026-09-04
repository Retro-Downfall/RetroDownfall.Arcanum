using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Issues #216 and #217: every ordinary tool call receives a record-only Ward pair without entering
/// the retired classifier, prompt, or auto-denial path.
///
/// The metric test asserts <c>Assert.Single</c> over <c>arcanum_ward_decisions_total</c>, which is a
/// single process-wide instrument on the shared <c>"Arcanum"</c> meter — every ward decision recorded
/// by any concurrently running class lands in the same listener. The <c>Telemetry</c> collection is
/// the <c>DisableParallelization</c> guarantee that assertion depends on.
/// </summary>
[Collection("Telemetry")]
public sealed class WardRecordPipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("read_file_chunk")]
    [InlineData("read_saga")]
    [InlineData("delegate_task")]
    [InlineData("web_search")]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    public async Task Every_ordinary_tool_records_an_ungated_ward_pair_without_blocking(string toolName)
    {

        List<ToolExecutionEvent> observed = [];

        WardGate ward = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            toolName,
            () =>
            {
                invoked = true;

                return "ran";
            },
            observer: evt =>
            {
                observed.Add(evt);

                return ValueTask.CompletedTask;
            },
            argumentsSnapshot: """{"scope":"fixture"}""");

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.False(processed.Denied);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        IntelligenceEvent warded = processed.WardEvents[0];

        IntelligenceEvent resolved = processed.WardEvents[1];

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.Equal(toolName, warded.WardToolName);

        Assert.Equal(toolName, resolved.WardToolName);

        JsonElement arguments = Assert.IsType<JsonElement>(warded.WardArguments);

        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);

        Assert.Equal("fixture", arguments.GetProperty("scope").GetString());

        Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

        Assert.True(resolved.WardAllowed);

        Assert.DoesNotContain(observed, static evt => evt is ToolApprovalRequestedEvent);

        Assert.Empty(ward.GetActiveWards());

    }

    [Fact]
    public async Task Unattended_write_file_call_executes_when_listed_as_a_forbidden_art()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            new WardPolicySettings
            {
                ForbiddenArts = ["write_file"],
                UnattendedMode = true,
            });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "write_file",
            () =>
            {
                invoked = true;

                return "ran";
            },
            request: new PingRequest("hi", WorkingDirectory: "/tmp", UnattendedMode: true));

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(1, ward.AutomaticCount);

        Assert.Equal(WardResolutionOrigin.Ungated, ward.LastAutomaticOrigin);

    }

    [Fact]
    public async Task A_configured_forbidden_art_is_recorded_then_blocked_by_Sanctum()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new DenyAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = ["execute_command"] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "execute_command",
            () =>
            {
                invoked = true;

                return "ran";
            },
            turnContext: SanctumStrictTurnContext());

        Assert.False(invoked);

        Assert.True(processed.Denied);

        Assert.Contains("Sanctum Guard", processed.ResultText, StringComparison.Ordinal);

        Assert.Equal(1, ward.AutomaticCount);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Theory]
    [InlineData("web_search", "web_search", true)]
    [InlineData("execute_command", "execute_command", true)]
    [InlineData("model_supplied_unknown_tool", "unregistered", false)]
    public async Task Every_ordinary_tool_records_an_ungated_ward_decision_metric(
        string toolName,
        string metricToolName,
        bool registerTool)
    {

        ConcurrentQueue<KeyValuePair<string, object?>[]> measurements = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {

            if (instrument.Name != "arcanum_ward_decisions_total")
            {
                return;
            }

            measurements.Enqueue(tags.ToArray());

        });

        listener.Start();

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        _ = await ProcessAsync(
            pipeline,
            toolName,
            static () => "ran",
            registerTool: registerTool);

        KeyValuePair<string, object?>[] recorded = Assert.Single(measurements);

        Assert.Equal(
            ["tool_name", "origin"],
            recorded.Select(static tag => tag.Key));

        Assert.Equal(metricToolName, recorded[0].Value);

        Assert.Equal("ungated", recorded[1].Value);

    }

    /// <summary>
    /// On the buffered path the Ward frames are accumulated rather than emitted live. If the tool
    /// then throws under the tolerant-failure policy — an MCP transport fault, a workspace IO error
    /// — the client must still learn that the call was recorded and how it was resolved, not merely
    /// that something failed.
    /// </summary>
    [Fact]
    public async Task A_tolerated_invocation_failure_still_reports_ungated_record_frames()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-write_file", "write_file", new Dictionary<string, object?>()),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(
                            () =>
                            {
                                throw new InvalidOperationException("mcp transport fault");
#pragma warning disable CS0162
                                return "unreachable";
#pragma warning restore CS0162
                            },
                            "write_file"),
                    ],
                },
                activeSpell: null,
                sessionId: "session-1",
                new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                CancellationToken.None);

        Assert.True(processed.Failed);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static e => e.Type));

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Fact]
    public async Task Live_ward_emit_forwards_the_pair_without_buffering_it()
    {

        List<IntelligenceEvent> emitted = [];

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "write_file",
            static () => "ran",
            argumentsSnapshot: """{"scope":"fixture"}""",
            liveWardEmit: (evt, _) =>
            {
                emitted.Add(evt);

                return Task.CompletedTask;
            });

        Assert.Same(Array.Empty<IntelligenceEvent>(), processed.WardEvents);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            emitted.Select(static evt => evt.Type));

        Assert.Equal(emitted[0].WardId, emitted[1].WardId);

        Assert.All(
            emitted,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Fact]
    public async Task Live_apply_patch_session_refusal_emits_the_pair_without_a_buffer()
    {

        List<IntelligenceEvent> emitted = [];

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            ToolRiskClassifier.ApplyPatchToolName,
            static () => "must not run",
            argumentsSnapshot: """{"patch":"fixture"}""",
            liveWardEmit: (evt, _) =>
            {
                emitted.Add(evt);

                return Task.CompletedTask;
            });

        Assert.Equal(
            """{"status":"invalid_request","code":"session_required","message":"apply_patch requires a bound persisted session and assistant-turn invocation."}""",
            processed.ResultText);

        Assert.Same(Array.Empty<IntelligenceEvent>(), processed.WardEvents);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            emitted.Select(static evt => evt.Type));

        Assert.Equal(emitted[0].WardId, emitted[1].WardId);

        Assert.All(
            emitted,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Fact]
    public async Task Live_tolerated_failure_emits_the_pair_without_a_buffer()
    {

        List<IntelligenceEvent> emitted = [];

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        Func<string> faultingTool = static () => throw new InvalidOperationException("mcp transport fault");

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-write_file", "write_file", new Dictionary<string, object?>()),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(
                            faultingTool,
                            "write_file"),
                    ],
                },
                activeSpell: null,
                sessionId: "session-1",
                new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                CancellationToken.None,
                liveWardEmit: (evt, _) =>
                {
                    emitted.Add(evt);

                    return Task.CompletedTask;
                },
                argumentsSnapshot: """{"scope":"fixture"}""");

        Assert.True(processed.Failed);

        Assert.Equal(
            "[Tool error: write_file failed with an internal error. The operator has been notified.]",
            processed.ResultText);

        Assert.Same(Array.Empty<IntelligenceEvent>(), processed.WardEvents);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            emitted.Select(static evt => evt.Type));

        Assert.Equal(emitted[0].WardId, emitted[1].WardId);

        Assert.All(
            emitted,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Fact]
    public async Task Malformed_non_empty_arguments_keep_the_raw_Ward_payload()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "write_file",
            () =>
            {
                invoked = true;

                return "ran";
            },
            argumentsSnapshot: "{not-json");

        Assert.True(invoked);

        IntelligenceEvent warded = Assert.Single(
            processed.WardEvents,
            static evt => evt.Type == IntelligenceEventType.Warded);

        JsonElement arguments = Assert.IsType<JsonElement>(warded.WardArguments);

        JsonProperty raw = Assert.Single(arguments.EnumerateObject());

        Assert.Equal("raw", raw.Name);

        Assert.Equal("{not-json", raw.Value.GetString());

    }

    [Fact]
    public void Ward_arguments_builder_rejects_an_empty_payload()
    {

        _ = Assert.Throws<ArgumentException>(
            static () => ToolExecutionPipeline.BuildWardArgumentsDocument("", ""));

    }

    [Fact]
    public async Task Ordinary_call_without_arguments_or_disclosure_skips_Ward_payload_materialization()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "write_file",
            () =>
            {
                invoked = true;

                return "ran";
            },
            argumentsSnapshot: "");

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        IntelligenceEvent warded = processed.WardEvents[0];

        IntelligenceEvent resolved = processed.WardEvents[1];

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

        Assert.Null(warded.WardArguments);

    }

    /// <summary>
    /// Issue #220: a record-only tool call must cost the same whether the operator has configured no
    /// forbidden arts or five hundred of them. Nothing on this path has any business reading that list,
    /// and the way a reader would betray itself is allocation — enumerating, copying or hashing the
    /// names once per call.
    ///
    /// The statistic is the median per-call allocation rather than the total over the window, and that
    /// distinction is the whole of this test's stability. Two costs land inside the window
    /// that have nothing to do with the settings. <see cref="WardGate"/>'s resolved-tombstone
    /// <c>ConcurrentDictionary</c> rehashes itself once during the run — around 100 KB charged to a
    /// single call, at an index chosen by this process's string hash seed and by the freshly minted
    /// ward GUIDs, so it falls inside one gate's window and outside the other's roughly half the time.
    /// A gen0 collection likewise retires the measuring thread's allocation context and charges its
    /// unused remainder to whichever call was in flight. Both are lone spikes in a window of otherwise
    /// identical samples, and a total-over-the-window budget has to be widened past 100 KB to survive
    /// them — wide enough to wave through the very scaling this test exists to catch. Real scaling is
    /// not a spike: consulting the list happens on every call, so it moves the median, which the tight
    /// per-call budget below then refuses.
    /// </summary>
    [Fact]
    public void N_tool_record_path_allocation_does_not_scale_with_ForbiddenArts_count()
    {

        const int WarmupCount = 8;

        const int SampleCount = 128;

        const int ForbiddenArtCount = 512;

        // Measured, every sampled call allocates the same 3240 bytes under both settings, so the
        // honest budget is "nothing measurable" and this is slack for a stray boxed value rather than
        // room for a real cost. It sits two orders of magnitude under the cheapest way a call could
        // consult the list at all: merely copying 512 references would be 4096 bytes on its own.
        const long MaximumSettingsDependentBytesPerToolCall = 64;

        const int TombstoneCapacitySeedCount = 768;

        List<string> configuredForbiddenArts = new(ForbiddenArtCount);

        for (int i = 0; i < ForbiddenArtCount; i++)
        {

            configuredForbiddenArts.Add($"forbidden_art_{i}");

        }

        ArcanumSettings emptySettings = new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings { ForbiddenArts = [] },
            },
        };

        ArcanumSettings configuredSettings = new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings { ForbiddenArts = configuredForbiddenArts },
            },
        };

        WardGate emptyWard = new(new TestOptionsMonitor<ArcanumSettings>(emptySettings));

        WardGate configuredWard = new(new TestOptionsMonitor<ArcanumSettings>(configuredSettings));

        // The production record ids are random GUIDs. Give both real gates the same table shape so a
        // settings-dependent cost cannot hide behind one gate's tombstone dictionary simply being
        // smaller than the other's. This does not pin down when that dictionary rehashes — growth is
        // triggered by bucket-chain length against random keys, not by a count the seeding could step
        // over — which is why the reduction below is a median and not a sum.
        for (int i = 0; i < TombstoneCapacitySeedCount; i++)
        {

            string wardId = $"allocation-capacity-seed-{i}";

            _ = emptyWard.RecordAutomaticResolution(
                wardId,
                allowed: true,
                reason: null,
                WardResolutionOrigin.Ungated);

            _ = configuredWard.RecordAutomaticResolution(
                wardId,
                allowed: true,
                reason: null,
                WardResolutionOrigin.Ungated);

        }

        ToolExecutionPipeline emptyPipeline = CreatePipeline(
            emptyWard,
            new AllowAllSanctumGuard(),
            emptySettings);

        ToolExecutionPipeline configuredPipeline = CreatePipeline(
            configuredWard,
            new AllowAllSanctumGuard(),
            configuredSettings);

        FunctionCallContent[] calls = new FunctionCallContent[WarmupCount + SampleCount];

        for (int i = 0; i < calls.Length; i++)
        {

            calls[i] = new FunctionCallContent(
                $"allocation-probe-{i}",
                "allocation_probe",
                new Dictionary<string, object?>());

        }

        AIFunction allocationProbe = AIFunctionFactory.Create(
            static () => "pong",
            "allocation_probe");

        PingRequest request = new("hi", WorkingDirectory: "/tmp");

        ChatOptions options = new() { Tools = [allocationProbe] };

        ToolExecutionPipeline.TurnContext turnContext = new();

        RunToolCalls(
            emptyPipeline,
            calls,
            start: 0,
            count: WarmupCount,
            request,
            options,
            turnContext);

        RunToolCalls(
            configuredPipeline,
            calls,
            start: 0,
            count: WarmupCount,
            request,
            options,
            turnContext);

        // Allocated ahead of both windows so that recording a sample never itself allocates inside the
        // span being measured.
        long[] emptyPerCallBytes = new long[SampleCount];

        long[] configuredPerCallBytes = new long[SampleCount];

        MeasureToolCalls(
            emptyPipeline,
            calls,
            start: WarmupCount,
            request,
            options,
            turnContext,
            emptyPerCallBytes);

        MeasureToolCalls(
            configuredPipeline,
            calls,
            start: WarmupCount,
            request,
            options,
            turnContext,
            configuredPerCallBytes);

        long emptyBytesPerCall = MedianOf(emptyPerCallBytes);

        long configuredBytesPerCall = MedianOf(configuredPerCallBytes);

        long deltaBytesPerCall = configuredBytesPerCall - emptyBytesPerCall;

        // The window totals are not asserted on, but a human reading a failure needs them: they are
        // what says whether a rehash or a collection landed in one window, and the medians alone hide
        // that by design.
        output.WriteLine(
            $"Issue #220 allocation sample: N={SampleCount}; "
                + $"empty={emptyBytesPerCall}/call over {emptyPerCallBytes.Sum()}; "
                + $"configured={configuredBytesPerCall}/call over {configuredPerCallBytes.Sum()}; "
                + $"delta={deltaBytesPerCall}/call");

        Assert.True(
            Math.Abs(deltaBytesPerCall) <= MaximumSettingsDependentBytesPerToolCall,
            $"N={SampleCount}; empty={emptyBytesPerCall}/call over {emptyPerCallBytes.Sum()}; "
                + $"configured={configuredBytesPerCall}/call over {configuredPerCallBytes.Sum()}; "
                + $"delta={deltaBytesPerCall}/call");

    }

    /// <summary>
    /// The middle sample of the window, over a copy so the caller's record stays in call order. With
    /// an even sample count this is the upper middle rather than the mean of the two: the reported
    /// number should be one the run actually observed, and no per-call cost is ever going to hinge on
    /// half a byte.
    /// </summary>
    private static long MedianOf(long[] perCallBytes)
    {

        long[] ordered = (long[])perCallBytes.Clone();

        Array.Sort(ordered);

        return ordered[ordered.Length / 2];

    }

    /// <summary>
    /// Fills <paramref name="perCallBytes"/> with what each individual call allocated, rather than
    /// returning what the window allocated in total, so the caller can reduce the window with a
    /// statistic no single outlier can move.
    ///
    /// The reading is <see cref="GC.GetAllocatedBytesForCurrentThread"/> and not a process-wide
    /// counter: xunit's own machinery, the test host and any logging on other threads allocate
    /// throughout this window, and every byte of it would otherwise be charged to the pipeline under
    /// test. The thread identity is re-checked afterwards because that guarantee only holds while the
    /// loop stays on one thread — the calls are asserted to complete synchronously for the same
    /// reason.
    /// </summary>
    private static void MeasureToolCalls(
        ToolExecutionPipeline pipeline,
        FunctionCallContent[] calls,
        int start,
        PingRequest request,
        ChatOptions options,
        ToolExecutionPipeline.TurnContext turnContext,
        long[] perCallBytes)
    {

        int managedThreadId = System.Environment.CurrentManagedThreadId;

        for (int i = 0; i < perCallBytes.Length; i++)
        {

            long before = GC.GetAllocatedBytesForCurrentThread();

            Task<ToolExecutionPipeline.ProcessedToolCall> task = pipeline.ProcessSingleToolCallAsync(
                calls[start + i],
                request,
                options,
                activeSpell: null,
                sessionId: "session-1",
                turnContext,
                suppressInvocationFailures: false,
                CancellationToken.None,
                argumentsSnapshot: "");

            if (!task.IsCompletedSuccessfully)
            {

                throw new InvalidOperationException("Allocation probe did not complete synchronously.");

            }

            _ = task.GetAwaiter().GetResult();

            perCallBytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;

        }

        if (System.Environment.CurrentManagedThreadId != managedThreadId)
        {

            throw new InvalidOperationException("Allocation measurement changed managed threads.");

        }

    }

    private static void RunToolCalls(
        ToolExecutionPipeline pipeline,
        FunctionCallContent[] calls,
        int start,
        int count,
        PingRequest request,
        ChatOptions options,
        ToolExecutionPipeline.TurnContext turnContext)
    {

        int end = start + count;

        for (int i = start; i < end; i++)
        {

            Task<ToolExecutionPipeline.ProcessedToolCall> task = pipeline.ProcessSingleToolCallAsync(
                calls[i],
                request,
                options,
                activeSpell: null,
                sessionId: "session-1",
                turnContext,
                suppressInvocationFailures: false,
                CancellationToken.None,
                argumentsSnapshot: "");

            if (!task.IsCompletedSuccessfully)
            {

                throw new InvalidOperationException("Allocation probe did not complete synchronously.");

            }

            _ = task.GetAwaiter().GetResult();

        }

    }

    private static ToolExecutionPipeline CreatePipeline(
        IWard ward,
        ISanctumGuard sanctumGuard,
        WardPolicySettings wardPolicy) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings { Ward = wardPolicy },
            }),
            ward,
            sanctumGuard,
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

    private static ToolExecutionPipeline CreatePipeline(
        IWard ward,
        ISanctumGuard sanctumGuard,
        ArcanumSettings settings) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            ward,
            sanctumGuard,
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

    private static ToolExecutionPipeline.TurnContext SanctumStrictTurnContext() =>
        new()
        {
            Campaign = new Campaign { Id = Guid.NewGuid(), Path = "/tmp" },
            CampaignId = Guid.NewGuid().ToString("D"),
            WorkspaceRoot = "/tmp",
            SanctumEnabled = true,
            SanctumMode = SanctumMode.Strict,
        };

    private static Task<ToolExecutionPipeline.ProcessedToolCall> ProcessAsync(
        ToolExecutionPipeline pipeline,
        string toolName,
        Func<string> implementation,
        ToolExecutionPipeline.TurnContext? turnContext = null,
        PingRequest? request = null,
        Func<ToolExecutionEvent, ValueTask>? observer = null,
        string? argumentsSnapshot = null,
        bool registerTool = true,
        Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit = null) =>
        pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>()),
            request ?? new PingRequest("hi", WorkingDirectory: "/tmp"),
            new ChatOptions
            {
                Tools = registerTool
                    ? [AIFunctionFactory.Create(implementation, toolName)]
                    : [],
            },
            activeSpell: null,
            sessionId: "session-1",
            turnContext ?? new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            CancellationToken.None,
            liveWardEmit: liveWardEmit,
            observer: observer,
            argumentsSnapshot: argumentsSnapshot);

    private sealed class RecordingWard : IWard
    {

        public int WaitCount { get; private set; }

        public int AutomaticCount { get; private set; }

        public WardResolutionOrigin? LastAutomaticOrigin { get; private set; }

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

            AutomaticCount++;

            LastAutomaticOrigin = origin;

            return new WardResolution(allowed, reason, DateTimeOffset.UtcNow, origin);

        }

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class AllowAllSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class DenyAllSanctumGuard : ISanctumGuard
    {

        private static SanctumResult Denied(string toolName) =>
            new()
            {
                Allowed = false,
                DenyReason = "The tool is disabled in this Sanctum.",
                Breach = new SanctumBreach
                {
                    BreachId = Guid.NewGuid().ToString("N"),
                    ToolName = toolName,
                    BreachType = "DisabledTool",
                    Timestamp = DateTimeOffset.UtcNow,
                },
            };

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

}
