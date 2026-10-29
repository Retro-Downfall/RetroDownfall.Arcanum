using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Deterministic contracts for the corrections the review of the Long Rest qualification asked for: what Scenario B accepts as proof
/// that recovery needed no operator, how the kill point fails when it never comes, what the forwarder may reach and withhold, the
/// runtime credential gate, the metrics-exporter probe, the receipt, health and summary-bound checks, and the wrapper-to-test names.
/// None of them contacts Ollama or starts the published host.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed partial class LocalOllamaLongRestQualificationHarnessContractTests
{
    private const string WrapperName = "verify-local-ollama-long-rest.sh";

    private static readonly Guid Operation = Guid.Parse("6f6c6f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f");

    private static LongRestOperationSnapshot Snapshot(LongRestOperationState state, int attempt, string? code = null) =>
        new(Operation, state, code, attempt, string.Empty, attempt, null);

    // ---- Scenario B: recovery from a crash needs no operator ----

    [Theory]
    [InlineData("LongRest.InvalidOutput", false, true)]
    [InlineData("LongRest.TransportUnknown", false, true)]
    [InlineData("LongRest.InvalidRequest", false, true)]
    [InlineData("long_rest.readmit_exhausted", false, true)]
    [InlineData(null, false, true)]
    [InlineData("LongRest.InvalidRequest", true, false)]
    [InlineData("LongRest.InvalidOutput", true, false)]
    [InlineData("LongRest.TransportUnknown", true, false)]
    [InlineData(null, true, false)]
    public void Recovery_from_a_crash_may_be_answered_by_an_operator_only_once_the_worker_has_readmitted_the_sweep(string? code, bool workerHasReadmitted, bool expectsFinding)
    {
        // A park after the worker's readmission belongs to the sweep running again (a real model's invalid answer, whatever code the
        // product gives it); a park before it is the crash's own, and an operator's help there would hide a failed automatic recovery.
        string? finding = LongRestQualificationChecks.OperatorRetryFinding(LongRestRetryScope.AfterWorkerReadmission, "Scenario B recovery", code, workerHasReadmitted);

        Assert.Equal(expectsFinding, finding is not null);

        if (expectsFinding)
        {
            Assert.Contains("Scenario B recovery", finding, StringComparison.Ordinal);
            Assert.Contains(code ?? "(no code)", finding, StringComparison.Ordinal);
            Assert.Contains("operator", finding, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(LongRestQualificationChecks.OperatorRetryFinding(LongRestRetryScope.AnyFault, "Scenario A sweep", code, workerHasReadmitted));
    }

    [Fact]
    public void The_first_park_after_a_crash_must_be_one_the_worker_resumes_and_a_park_too_brief_to_poll_is_not_a_finding()
    {
        Assert.Null(LongRestQualificationChecks.CrashParkFinding([]));

        Assert.Null(LongRestQualificationChecks.CrashParkFinding(
            [Snapshot(LongRestOperationState.Running, 1), Snapshot(LongRestOperationState.Running, 3), Snapshot(LongRestOperationState.Completed, 3)]));

        Assert.Null(LongRestQualificationChecks.CrashParkFinding(
            [
                Snapshot(LongRestOperationState.Running, 1),
                Snapshot(LongRestOperationState.ReconciliationRequired, 2, LongRestParkCodes.Interrupted),
                Snapshot(LongRestOperationState.Running, 3),
            ]));

        string? finding = LongRestQualificationChecks.CrashParkFinding(
            [
                Snapshot(LongRestOperationState.Running, 1),
                Snapshot(LongRestOperationState.ReconciliationRequired, 2, ErrorCodes.LongRest.TransportUnknown),
                Snapshot(LongRestOperationState.Running, 3),
            ]);

        Assert.NotNull(finding);
        Assert.Contains(ErrorCodes.LongRest.TransportUnknown, finding, StringComparison.Ordinal);
        Assert.Contains("only an operator", finding, StringComparison.OrdinalIgnoreCase);

        // Only a park before the worker's readmission is the crash's: a later one is the sweep running again. This is the timeline the
        // first live re-run recorded, where the model's invalid answer parked the sweep long after the worker had readmitted it.
        Assert.Null(LongRestQualificationChecks.CrashParkFinding(
            [
                Snapshot(LongRestOperationState.Running, 1),
                Snapshot(LongRestOperationState.Running, 3),
                Snapshot(LongRestOperationState.ReconciliationRequired, 3, ErrorCodes.LongRest.InvalidRequest),
                Snapshot(LongRestOperationState.Pending, 3),
                Snapshot(LongRestOperationState.ReconciliationRequired, 4, LongRestParkCodes.ReadmitRequested),
                Snapshot(LongRestOperationState.Running, 5),
                Snapshot(LongRestOperationState.Completed, 5),
            ]));

        Assert.Null(LongRestQualificationChecks.CrashParkFinding(
            [
                Snapshot(LongRestOperationState.Running, 1),
                Snapshot(LongRestOperationState.ReconciliationRequired, 2, LongRestParkCodes.Interrupted),
                Snapshot(LongRestOperationState.Running, 3),
                Snapshot(LongRestOperationState.ReconciliationRequired, 3, ErrorCodes.LongRest.InvalidOutput),
            ]));

        // A takeover (one attempt beyond the dead owner's) that parks for an operator is still the crash's own park.
        Assert.NotNull(LongRestQualificationChecks.CrashParkFinding(
            [
                Snapshot(LongRestOperationState.Running, 1),
                Snapshot(LongRestOperationState.Running, 2),
                Snapshot(LongRestOperationState.ReconciliationRequired, 2, ErrorCodes.LongRest.TransportUnknown),
            ]));
    }

    [Fact]
    public void Readmission_counts_as_the_workers_only_when_it_was_seen_before_the_run_touched_the_sweep()
    {
        TimeSpan at(int seconds) => TimeSpan.FromSeconds(seconds);

        // The worker readmitted it: a second attempt beyond the dead owner's, seen running, and no operator involved.
        Assert.True(LongRestQualificationChecks.ReadmittedBeforeAnyOperator(
            [(at(0), Snapshot(LongRestOperationState.Running, 1)), (at(300), Snapshot(LongRestOperationState.Running, 3))],
            []));

        // The dead owner's attempt only, or the takeover that has not yet parked or readmitted it.
        Assert.False(LongRestQualificationChecks.ReadmittedBeforeAnyOperator(
            [(at(0), Snapshot(LongRestOperationState.Running, 1)), (at(5), Snapshot(LongRestOperationState.Running, 1))],
            []));

        Assert.False(LongRestQualificationChecks.ReadmittedBeforeAnyOperator(
            [(at(0), Snapshot(LongRestOperationState.Running, 1)), (at(300), Snapshot(LongRestOperationState.Running, 2))],
            []));

        // Parked for an operator, retried by the run, and only then running again: an operator's readmission.
        Assert.False(LongRestQualificationChecks.ReadmittedBeforeAnyOperator(
            [
                (at(0), Snapshot(LongRestOperationState.Running, 1)),
                (at(300), Snapshot(LongRestOperationState.ReconciliationRequired, 2, ErrorCodes.LongRest.TransportUnknown)),
                (at(302), Snapshot(LongRestOperationState.Running, 3)),
            ],
            [at(301)]));

        // The worker readmitted it first and the model's invalid output was answered later: still the worker's readmission.
        Assert.True(LongRestQualificationChecks.ReadmittedBeforeAnyOperator(
            [
                (at(0), Snapshot(LongRestOperationState.Running, 1)),
                (at(300), Snapshot(LongRestOperationState.Running, 3)),
                (at(330), Snapshot(LongRestOperationState.ReconciliationRequired, 3, ErrorCodes.LongRest.InvalidOutput)),
                (at(332), Snapshot(LongRestOperationState.Running, 4)),
            ],
            [at(331)]));

        Assert.False(LongRestQualificationChecks.ReadmittedBeforeAnyOperator([], []));
    }

    // ---- The kill point: it comes, or the run fails at once with the reason ----

    [Fact]
    public async Task Kill_point_returns_the_request_the_hook_saw_and_prefers_it_to_a_failed_poll()
    {
        TaskCompletionSource<LongRestRecordedRequest> fired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        LongRestRecordedRequest request = new() { Sequence = 7, Kind = LongRestProviderRequestKind.Summary };

        int polls = 0;

        Task<LongRestRecordedRequest> waiting = LongRestQualificationKillPoint.AwaitAsync(
            fired.Task,
            token =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                {
                    return Task.FromResult(Snapshot(LongRestOperationState.Running, 1));
                }

                // The host died under the request: the poll that races its death fails, and the hook's request still wins.
                _ = fired.TrySetResult(request);

                throw new HttpRequestException("The host is gone.");
            },
            static () => "none",
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Same(request, await waiting);
    }

    [Fact]
    public async Task Kill_point_fails_at_once_naming_the_operation_and_the_traffic_when_the_sweep_ends_without_a_chat_request()
    {
        TaskCompletionSource<LongRestRecordedRequest> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int polls = 0;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LongRestQualificationKillPoint.AwaitAsync(
            never.Task,
            _ => Task.FromResult(Interlocked.Increment(ref polls) < 3
                ? Snapshot(LongRestOperationState.Running, 1)
                : Snapshot(LongRestOperationState.Completed, 1)),
            static () => "2 embeddings, 0 Long Rest chat calls",
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMinutes(45),
            CancellationToken.None));

        Assert.Contains("Completed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("2 embeddings, 0 Long Rest chat calls", failure.Message, StringComparison.Ordinal);
        Assert.Contains("before any Long Rest chat request", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kill_point_gives_up_after_its_bound_with_the_last_operation_state_and_the_traffic()
    {
        TaskCompletionSource<LongRestRecordedRequest> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() => LongRestQualificationKillPoint.AwaitAsync(
            never.Task,
            _ => Task.FromResult(Snapshot(LongRestOperationState.Running, 1)),
            static () => "3 embeddings",
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(60),
            CancellationToken.None));

        Assert.Contains("Running", failure.Message, StringComparison.Ordinal);
        Assert.Contains("3 embeddings", failure.Message, StringComparison.Ordinal);
    }

    // ---- The forwarder reaches only the one endpoint it was given ----

    [Theory]
    [InlineData("/v1/chat/completions", "", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("/v1/embeddings", "?dimensions=768", "http://127.0.0.1:11434/v1/embeddings?dimensions=768")]
    [InlineData("/v1/models", "", "http://127.0.0.1:11434/v1/models")]
    [InlineData("/chat/completions", "", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("//other.example/x", "", "http://127.0.0.1:11434/v1/other.example/x")]
    [InlineData("/v1/http://127.0.0.1:11434/v1/models", "", "http://127.0.0.1:11434/v1/models")]
    public void Forwarder_target_stays_beneath_the_validated_endpoint(string path, string query, string expected)
    {
        Assert.True(LongRestQualificationProxy.TryResolveUpstream(new Uri("http://127.0.0.1:11434/v1/"), path, query, out Uri? target));
        Assert.Equal(expected, target!.AbsoluteUri);
    }

    [Theory]
    [InlineData("/v1/http://other.example/x")]
    [InlineData("/http://other.example/x")]
    [InlineData("/v1/http://127.0.0.1:9999/v1/models")]
    [InlineData("/v1/https://127.0.0.1:11434/v1/models")]
    [InlineData("/v1/http://user@127.0.0.1:11434/v1/models")]
    [InlineData("/v1/../../x")]
    [InlineData("/v1/../x")]
    [InlineData("/v1/mailto:someone@example.test")]
    [InlineData("/v1/ftp://127.0.0.1:11434/v1/x")]
    [InlineData("/v1/http://127.0.0.1:11434/other")]
    public void Forwarder_target_never_leaves_the_validated_endpoint_for_another_host_port_scheme_or_path(string path)
    {
        Assert.False(LongRestQualificationProxy.TryResolveUpstream(new Uri("http://127.0.0.1:11434/v1/"), path, string.Empty, out Uri? target));
        Assert.Null(target);
    }

    [Fact]
    public async Task Forwarder_answers_502_without_forwarding_a_request_that_names_another_host()
    {
        int upstreamRequests = 0;

        await using StubUpstream upstream = await StubUpstream.StartAsync(context =>
        {
            _ = Interlocked.Increment(ref upstreamRequests);

            return context.Response.WriteAsync("{}");
        });

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new();

        using HttpResponseMessage escaping = await client.GetAsync(new Uri($"{proxy.Endpoint.AbsoluteUri}http://other.example/x"));

        Assert.Equal(HttpStatusCode.BadGateway, escaping.StatusCode);

        using HttpResponseMessage models = await client.GetAsync(new Uri(proxy.Endpoint, "models"));

        Assert.Equal(HttpStatusCode.OK, models.StatusCode);

        Assert.Equal(1, Volatile.Read(ref upstreamRequests));

        Assert.Equal(2, proxy.All.Count);
    }

    [Fact]
    public async Task Forwarder_records_a_delivered_chat_response_with_its_usage()
    {
        await using StubUpstream upstream = await StubUpstream.StartAsync(StubChatResponse);

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(SummaryRequest(proxy.Endpoint));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A beagle.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        LongRestRecordedRequest record = Assert.Single(proxy.All);

        await WaitUntilAsync(() => record.CompletedAt is not null);

        Assert.Equal(LongRestProviderRequestKind.Summary, record.Kind);
        Assert.True(record.UpstreamCompleted);
        Assert.True(record.Delivered);
        Assert.False(record.ClientAborted);
        Assert.Equal(21L, record.PromptTokens);
        Assert.Equal(34L, record.CompletionTokens);
        Assert.Equal("stop", record.FinishReason);
    }

    [Fact]
    public async Task Forwarder_holds_the_whole_response_back_until_the_hook_has_run_and_can_withhold_it_for_good()
    {
        await using StubUpstream upstream = await StubUpstream.StartAsync(StubChatResponse);

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };

        TaskCompletionSource<LongRestRecordedRequest> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<Task<HttpResponseMessage>> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool answeredAndBilledButNotDelivered = false;

        bool hostSawNothing = false;

        proxy.SetBeforeDeliveryHook(
            static request => request.Kind == LongRestProviderRequestKind.Summary,
            async request =>
            {
                // The provider has answered and billed the call; not one byte of the answer has reached the host.
                Task<HttpResponseMessage> clientTask = await started.Task;

                answeredAndBilledButNotDelivered = request.UpstreamCompleted && !request.Delivered && request.CompletionTokens == 34L;

                hostSawNothing = !clientTask.IsCompleted;

                _ = seen.TrySetResult(request);

                return true;
            });

        Task<HttpResponseMessage> clientTask = client.SendAsync(SummaryRequest(proxy.Endpoint), HttpCompletionOption.ResponseHeadersRead);

        started.SetResult(clientTask);

        LongRestRecordedRequest held = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(answeredAndBilledButNotDelivered);

        Assert.True(hostSawNothing);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => clientTask);

        await WaitUntilAsync(() => held.CompletedAt is not null);

        Assert.True(held.UpstreamCompleted);
        Assert.False(held.Delivered);
        Assert.True(held.ClientAborted);
        Assert.Equal(21L, held.PromptTokens);

        // One shot: the next matching request is forwarded and delivered in the ordinary way.
        using HttpResponseMessage again = await client.SendAsync(SummaryRequest(proxy.Endpoint));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        LongRestRecordedRequest second = proxy.All[^1];

        await WaitUntilAsync(() => second.CompletedAt is not null);

        Assert.True(second.Delivered);
    }

    // ---- The Mutiny Scenario rewrites what the host is told, and only that ----

    [Fact]
    public async Task Forwarder_rewrites_an_adjudication_answer_for_the_host_and_keeps_the_providers_own_usage()
    {
        await using StubUpstream upstream = await StubUpstream.StartAsync(StubChatResponse);

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new();

        proxy.SetAnswerRewriter(static request => LongRestScriptedVerdicts.AllPairs(LongRestQualificationProxy.CountMemories(request.UserText)));

        // A summary is left alone, and an adjudication is rewritten into the all-pairs verdict for the number of memories it lists.
        using HttpResponseMessage summary = await client.SendAsync(SummaryRequest(proxy.Endpoint));

        Assert.Contains("A beagle.", await summary.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage adjudication = await client.SendAsync(AdjudicationRequest(proxy.Endpoint, 3));

        Assert.Equal(HttpStatusCode.OK, adjudication.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await adjudication.Content.ReadAsStringAsync());

        string? content = body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        Assert.Equal(LongRestScriptedVerdicts.AllPairs(3), content);
        Assert.Equal(34, body.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());

        LongRestRecordedRequest record = proxy.All[^1];

        await WaitUntilAsync(() => record.CompletedAt is not null);

        Assert.Equal(LongRestProviderRequestKind.Adjudication, record.Kind);
        Assert.True(record.Rewritten);
        Assert.True(record.Delivered);
        Assert.Equal(LongRestScriptedVerdicts.AllPairs(3), record.ResponseContent);
        Assert.Equal(34L, record.CompletionTokens);
        Assert.Equal("none", record.ReasoningEffort);
        Assert.False(proxy.All[0].Rewritten);

        // Stopping the mutiny restores the model's own answer.
        proxy.SetAnswerRewriter(null);

        using HttpResponseMessage plain = await client.SendAsync(AdjudicationRequest(proxy.Endpoint, 3));

        Assert.Contains("A beagle.", await plain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(proxy.All[^1].Rewritten);
    }

    [Fact]
    public async Task Forwarder_delivers_the_held_response_intact_when_the_hook_does_not_withhold_it()
    {
        await using StubUpstream upstream = await StubUpstream.StartAsync(StubChatResponse);

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new();

        proxy.SetBeforeDeliveryHook(static _ => true, static _ => Task.FromResult(false));

        using HttpResponseMessage response = await client.SendAsync(SummaryRequest(proxy.Endpoint));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(34, body.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());

        LongRestRecordedRequest record = Assert.Single(proxy.All);

        await WaitUntilAsync(() => record.CompletedAt is not null);

        Assert.True(record.UpstreamCompleted);
        Assert.True(record.Delivered);
        Assert.False(record.ClientAborted);
    }

    [Fact]
    public void Billed_but_undelivered_calls_are_the_provider_answers_the_host_never_received()
    {
        LongRestRecordedRequest[] traffic =
        [
            new() { Sequence = 1, Kind = LongRestProviderRequestKind.Summary, UpstreamCompleted = true, Delivered = true, StatusCode = 200 },
            new() { Sequence = 2, Kind = LongRestProviderRequestKind.Summary, UpstreamCompleted = true, Delivered = false, ClientAborted = true, StatusCode = 200 },
            new() { Sequence = 3, Kind = LongRestProviderRequestKind.Adjudication, UpstreamCompleted = false, Delivered = false, ClientAborted = true },
            new() { Sequence = 4, Kind = LongRestProviderRequestKind.Chat, UpstreamCompleted = true, Delivered = false, ClientAborted = true, StatusCode = 200 },
            new() { Sequence = 5, Kind = LongRestProviderRequestKind.Embedding, UpstreamCompleted = true, Delivered = false, StatusCode = 500 },
        ];

        Assert.Equal([2], LongRestQualificationChecks.BilledButUndelivered(traffic).Select(static request => request.Sequence));
    }

    // ---- The credential backend is proved in memory before the host first writes a secret ----

    [Fact]
    public void Credential_gate_holds_for_an_isolated_child_and_names_the_variable_that_breaks_it_without_its_value()
    {
        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("long-rest-credential-gate");

        try
        {
            isolation.Prepare();

            string workspace = Path.Combine(isolation.TestHome, "workspace");

            Directory.CreateDirectory(workspace);

            LongRestQualificationEnvironment environment = new(isolation, workspace);

            ProcessStartInfo start = environment.CreateStartInfo("/published/arcanum", ["serve", "--plain"]);

            LongRestQualificationEnvironment.AssertInMemoryCredentialBackend(start);

            foreach ((string variable, string broken) in new[]
            {
                ("ARCANUM_TEST_IN_MEMORY_CREDENTIALS", "0"),
                ("DOTNET_ENVIRONMENT", "Production"),
                ("ASPNETCORE_ENVIRONMENT", "Production"),
                ("ARCANUM_TEST_HOME", Path.GetTempPath()),
                ("TMPDIR", Path.GetTempPath()),
            })
            {
                ProcessStartInfo tampered = environment.CreateStartInfo("/published/arcanum", ["serve", "--plain"]);

                tampered.Environment[variable] = broken;

                InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                    () => LongRestQualificationEnvironment.AssertInMemoryCredentialBackend(tampered));

                Assert.Contains("ARCANUM_TEST_IN_MEMORY_CREDENTIALS", failure.Message, StringComparison.Ordinal);
                Assert.Contains("in-memory", failure.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(isolation.TestHome, failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(broken, failure.Message, StringComparison.Ordinal);
            }

            ProcessStartInfo missing = environment.CreateStartInfo("/published/arcanum", ["serve", "--plain"]);

            _ = missing.Environment.Remove("ARCANUM_TEST_IN_MEMORY_CREDENTIALS");

            _ = Assert.Throws<InvalidOperationException>(() => LongRestQualificationEnvironment.AssertInMemoryCredentialBackend(missing));
        }
        finally
        {
            isolation.Delete();
        }
    }

    // ---- The metrics-exporter defect is detected, not hidden by the priming scrape ----

    [Fact]
    public void A_first_scrape_without_the_requests_made_before_it_is_the_exporters_late_start()
    {
        string? finding = LongRestQualificationChecks.ExporterStartFinding(
            "after SIGKILL",
            new Dictionary<string, double> { ["arcanum_sessions_active"] = 0, ["arcanum_metrics_series_dropped_total"] = 0 });

        Assert.NotNull(finding);
        Assert.Contains("after SIGKILL", finding, StringComparison.Ordinal);
        Assert.Contains("first scrape", finding, StringComparison.OrdinalIgnoreCase);

        Assert.Null(LongRestQualificationChecks.ExporterStartFinding(
            "after SIGKILL",
            new Dictionary<string, double> { ["arcanum_http_requests_total{route=\"/api/meta\",method=\"GET\"}"] = 3 }));

        Assert.Null(LongRestQualificationChecks.ExporterStartFinding(
            "Long Rest enabled",
            new Dictionary<string, double> { ["http_server_request_duration_seconds_count{http_route=\"/api/meta\"}"] = 3 }));

        Assert.NotNull(LongRestQualificationChecks.ExporterStartFinding(
            "Long Rest enabled",
            new Dictionary<string, double> { ["arcanum_http_requests_total{route=\"/api/meta\"}"] = 0 }));
    }

    // ---- Embedding-Bounded Summary, health and receipt checks ----

    [Fact]
    public void Summaries_over_the_embedding_bound_are_named_by_node_and_size_and_bytes_are_not_characters()
    {
        LongRestNodeRow[] nodes =
        [
            new("leaf", 0, "m1", null, null),
            new("fits", 1, null, new string('x', 100), null),
            new("exact", 1, null, new string('x', 1766), null),
            new("over", 1, null, new string('x', 1767), null),
            new("wide", 2, null, new string('é', 900), null),
            new("no-summary", 1, null, null, null),
        ];

        Assert.Equal(
            [("over", 1767), ("wide", 1800)],
            LongRestQualificationChecks.OversizedSummaries(nodes, LongRestPromptLayout.SummaryBoundUtf8Bytes(2048)));

        Assert.Equal(1766, LongRestPromptLayout.SummaryBoundUtf8Bytes(2048));
        Assert.Empty(LongRestQualificationChecks.OversizedSummaries([], 1766));
    }

    [Theory]
    [InlineData(1, "EquivalentObservations")]
    [InlineData(2, "Supersession")]
    [InlineData(3, "NoChange")]
    [InlineData(9, "unknown kind 9")]
    public void Adjudication_kinds_are_rendered_by_the_products_enum_names(int code, string expected) =>
        Assert.Equal(expected, LongRestQualificationChecks.DescribeAdjudicationKind(code));

    [Fact]
    public void Health_findings_name_an_unhealthy_report_or_a_long_rest_component_that_is_missing_or_degraded()
    {
        LongRestApiHealth healthy = new(
            "Healthy",
            [new("Grimoire", "Healthy", null), new("LongRest", "Healthy", "recipe resolved")]);

        Assert.Null(LongRestQualificationChecks.HealthFinding("Scenario A", healthy));

        // Degraded elsewhere is a warning, not a finding: only an Unhealthy report or the Long Rest component counts.
        Assert.Null(LongRestQualificationChecks.HealthFinding(
            "Scenario A",
            new("Degraded", [new("Conclave", "Degraded", "disabled"), new("LongRest", "Healthy", null)])));

        string? unhealthy = LongRestQualificationChecks.HealthFinding(
            "Scenario B",
            new("Unhealthy", [new("Grimoire", "Unhealthy", "secret detail"), new("LongRest", "Healthy", null)]));

        Assert.NotNull(unhealthy);
        Assert.Contains("Scenario B", unhealthy, StringComparison.Ordinal);
        Assert.Contains("Grimoire", unhealthy, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", unhealthy, StringComparison.Ordinal);

        string? degraded = LongRestQualificationChecks.HealthFinding(
            "Scenario C",
            new("Degraded", [new("LongRest", "Degraded", "LongRest: enabled but recipe unresolved: FastModel")]));

        Assert.NotNull(degraded);
        Assert.Contains("recipe unresolved", degraded, StringComparison.Ordinal);

        // Durable operations: a recovered crash or a cancelled sweep leaves nothing awaiting repair, so a Degraded row with every sweep settled is a finding.
        Assert.Null(LongRestQualificationChecks.HealthFinding(
            "Scenario B",
            new("Healthy", [new("DurableOperations", "Healthy", "nothing awaiting repair"), new("LongRest", "Healthy", null)])));

        string? stale = LongRestQualificationChecks.HealthFinding(
            "Scenario B",
            new("Degraded", [new("DurableOperations", "Degraded", "1 operation awaiting repair"), new("LongRest", "Healthy", null)]));

        Assert.NotNull(stale);
        Assert.Contains("DurableOperations component is Degraded", stale, StringComparison.Ordinal);
        Assert.Contains("awaiting repair", stale, StringComparison.Ordinal);

        string? missing = LongRestQualificationChecks.HealthFinding("Scenario C", new("Healthy", [new("Grimoire", "Healthy", null)]));

        Assert.NotNull(missing);
        Assert.Contains("LongRest component", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_and_receipt_payloads_are_read_from_numbers_or_names_and_camel_case_fields()
    {
        using JsonDocument health = JsonDocument.Parse(
            "{\"status\":2,\"components\":[{\"name\":\"Grimoire\",\"status\":\"Healthy\",\"detail\":null},{\"name\":\"LongRest\",\"status\":1,\"detail\":\"x\"}]}");

        LongRestApiHealth read = LongRestQualificationApi.ReadHealth(health.RootElement);

        Assert.Equal("Unhealthy", read.Status);
        Assert.Equal(["Grimoire:Healthy", "LongRest:Degraded"], read.Components.Select(static component => $"{component.Name}:{component.Status}"));
        Assert.Equal("x", read.Components[1].Detail);

        using JsonDocument receipt = JsonDocument.Parse(
            "{\"receiptId\":\"AB12\",\"policyVersion\":1,\"kind\":1,\"outcome\":1,\"reason\":1,\"inputHash\":\"h\",\"outputHash\":\"o\","
            + "\"targets\":[{\"memoryId\":\"0125f9cf684e43718f0ebc1090f71e0e\",\"claimId\":\"c\",\"versionId\":\"v\",\"revision\":1,\"contentHashFormat\":1,\"contentHash\":\"hh\"}],"
            + "\"survivorMemoryId\":\"0bc1ab43124a44c6ae12a7efcfee9873\",\"survivorClaimId\":null,\"survivorVersionId\":null,\"supersededVersionIds\":[]}");

        LongRestApiReceipt readReceipt = LongRestQualificationApi.ReadReceipt(receipt.RootElement);

        Assert.Equal("AB12", readReceipt.ReceiptId);
        Assert.Equal(["0125f9cf684e43718f0ebc1090f71e0e"], readReceipt.TargetMemoryIds);
        Assert.Equal("0bc1ab43124a44c6ae12a7efcfee9873", readReceipt.SurvivorMemoryId);
        Assert.Contains("contentHash", readReceipt.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_must_be_the_one_the_memory_names_content_free_and_agree_with_the_fold()
    {
        LongRestApiMemory memory = new(
            "0125f9cf-684e-4371-8f0e-bc1090f71e0e",
            "The user owns a beagle named Biscuit.",
            null,
            DateTimeOffset.UnixEpoch,
            false,
            false,
            "0bc1ab43-124a-44c6-ae12-a7efcfee9873",
            "AB12");

        LongRestApiReceipt ok = new(
            "AB12",
            ["0125f9cf684e43718f0ebc1090f71e0e", "0bc1ab43124a44c6ae12a7efcfee9873"],
            "0bc1ab43124a44c6ae12a7efcfee9873",
            "{\"receiptId\":\"AB12\",\"inputHash\":\"h\"}");

        string[] corpus = ["The user owns a beagle named Biscuit.", "The user's cat is a grey tabby named Mochi."];

        Assert.Empty(LongRestQualificationChecks.ReceiptProblems(ok, memory, corpus));

        Assert.Single(LongRestQualificationChecks.ReceiptProblems(ok with { ReceiptId = "FFFF" }, memory, corpus));

        Assert.Single(LongRestQualificationChecks.ReceiptProblems(ok with { TargetMemoryIds = ["0bc1ab43124a44c6ae12a7efcfee9873"] }, memory, corpus));

        Assert.Single(LongRestQualificationChecks.ReceiptProblems(ok with { SurvivorMemoryId = "11111111111111111111111111111111" }, memory, corpus));

        IReadOnlyList<string> leaked = LongRestQualificationChecks.ReceiptProblems(
            ok with { RawJson = "{\"summary\":\"the user owns a BEAGLE named biscuit.\"}" },
            memory,
            corpus);

        Assert.Single(leaked);
        Assert.Contains("content", leaked[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Biscuit", leaked[0], StringComparison.Ordinal);
    }

    // ---- Product enums, not their numbers ----

    [Fact]
    public void The_evidence_reader_uses_the_products_enums_and_the_run_checks_the_new_evidence_in_every_scenario()
    {
        string run = ReadRunSources();

        Assert.DoesNotContain("AttemptStateSent = 3", run, StringComparison.Ordinal);
        Assert.DoesNotContain("AttemptStateSettledLow = 4", run, StringComparison.Ordinal);
        Assert.DoesNotContain("SweepPhaseCompleted = 6", run, StringComparison.Ordinal);
        Assert.Contains("(int)LongRestDispatchAttemptState.Sent", run, StringComparison.Ordinal);
        Assert.Contains("(int)LongRestDispatchAttemptState.Accepted", run, StringComparison.Ordinal);
        Assert.Contains("(int)LongRestSweepPhase.Completed", run, StringComparison.Ordinal);
        Assert.Contains("DescribeAdjudicationKind(decision.Code)", run, StringComparison.Ordinal);
        Assert.DoesNotContain("kind {decision.Code}", run, StringComparison.Ordinal);

        // Scenario B: the kill point can fail fast, recovery may not lean on an operator, and the provider's billed-but-lost answer is reconciled.
        Assert.Contains("LongRestQualificationKillPoint.AwaitAsync", run, StringComparison.Ordinal);
        Assert.DoesNotContain("killedSignal.Task.WaitAsync(SweepBudget", run, StringComparison.Ordinal);
        Assert.Contains("LongRestRetryScope.AfterWorkerReadmission", run, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationChecks.CrashParkFinding", run, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationChecks.ReadmittedBeforeAnyOperator", run, StringComparison.Ordinal);
        Assert.Contains("SetBeforeDeliveryHook", run, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationChecks.BilledButUndelivered", run, StringComparison.Ordinal);

        // The worker counts the crash park where it readmits the sweep, so the restarted host owes the park as well as the readmission.
        Assert.Contains("SumMetric(metrics, \"long_rest_sweep_parked\", \"resume_later\")", run, StringComparison.Ordinal);
        Assert.Contains("Check(parkedCount >= 1,", run, StringComparison.Ordinal);
        Assert.DoesNotContain("which the park counter does not cover", run, StringComparison.Ordinal);

        // The metrics exporter starts with the host, so a restart's first scrape must already carry what the host recorded: a late start is a
        // finding, and the run keeps no workaround scrape to hide it.
        Assert.Contains("LongRestQualificationChecks.ExporterStartFinding", run, StringComparison.Ordinal);
        Assert.DoesNotContain("temporary workaround", run, StringComparison.OrdinalIgnoreCase);

        // A seeding turn that failed before anything was remembered is told again, a bounded number of times, and never when it left memories behind.
        Assert.Contains("MaximumSeedingAttempts = 3", run, StringComparison.Ordinal);
        Assert.Contains("await Api.SagaCountAsync(cancellationToken) != before", run, StringComparison.Ordinal);

        // The runtime gate on the in-memory credential backend precedes the first host start.
        Assert.True(
            run.IndexOf("AssertInMemoryCredentialBackend", StringComparison.Ordinal)
                is var gate and >= 0
            && gate < run.IndexOf("LongRestPublishedHost.Start(_options.Executable, Installation, knownMasterApiKey: null)", StringComparison.Ordinal),
            "The in-memory credential gate must be proved before the published host first starts.");

        // The new evidence: summary bound, health and receipts.
        Assert.Contains("LongRestQualificationChecks.OversizedSummaries", run, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationChecks.HealthFinding", run, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationChecks.ReceiptProblems", run, StringComparison.Ordinal);
        Assert.Contains("GetReceiptAsync", run, StringComparison.Ordinal);
        Assert.Contains("GetHealthAsync", run, StringComparison.Ordinal);
    }

    // ---- The wrapper and the test share their names and their receipt ----

    [Fact]
    public void Wrapper_and_test_share_every_environment_name_default_and_the_exact_success_receipt()
    {
        string root = FindRepositoryRoot();

        string wrapper = File.ReadAllText(Path.Combine(root, "scripts", WrapperName));

        string qualification = File.ReadAllText(Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests", "Packaging", "PublishedArcanumOllamaLongRestQualificationTests.cs"));

        foreach (string variable in new[]
        {
            LongRestQualificationOptions.OptInVariable,
            LongRestQualificationOptions.ExecutableVariable,
            LongRestQualificationOptions.ChatModelVariable,
            LongRestQualificationOptions.DefaultModelVariable,
            LongRestQualificationOptions.EmbeddingModelVariable,
            LongRestQualificationOptions.EmbeddingWindowVariable,
            LongRestQualificationOptions.EmbeddingDimensionsVariable,
            LongRestQualificationOptions.EndpointVariable,
            LongRestQualificationOptions.ResultsVariable,
            LongRestQualificationOptions.ReceiptVariable,
            LongRestQualificationOptions.MutinyVariable,
        })
        {
            Assert.Contains(variable + "=", wrapper, StringComparison.Ordinal);
        }

        Assert.Contains($"{LongRestQualificationOptions.MutinyVariable}=\"$MUTINY\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("MUTINY=\"1\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("--no-mutiny)", wrapper, StringComparison.Ordinal);

        // One run for each installed embedding model: the wrapper asks Ollama which models embed and what their window and vector size are.
        Assert.Contains("--all-embedding-models)", wrapper, StringComparison.Ordinal);
        Assert.Contains("/api/show", wrapper, StringComparison.Ordinal);
        Assert.Contains(",embedding,", wrapper, StringComparison.Ordinal);

        // Each flag's value reaches the variable the test reads for it.
        Assert.Contains($"{LongRestQualificationOptions.ChatModelVariable}=\"$CHAT_MODEL\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.DefaultModelVariable}=\"$DEFAULT_MODEL\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.EmbeddingModelVariable}=\"$EMBEDDING_MODEL\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.EmbeddingWindowVariable}=\"$EMBEDDING_WINDOW\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.EmbeddingDimensionsVariable}=\"$EMBEDDING_DIMENSIONS\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.EndpointVariable}=\"$ENDPOINT\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.ResultsVariable}=\"$RESULTS_DIR_FOR_TEST\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.ReceiptVariable}=\"$QUALIFICATION_RECEIPT_FOR_TEST\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.ExecutableVariable}=\"$EXECUTABLE_FOR_TEST\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"{LongRestQualificationOptions.OptInVariable}=1", wrapper, StringComparison.Ordinal);

        // The defaults the wrapper documents are the defaults the test applies.
        Assert.Contains($"CHAT_MODEL=\"{LongRestQualificationOptions.DefaultChatModel}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"DEFAULT_MODEL=\"{LongRestQualificationOptions.DefaultDefaultModel}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"EMBEDDING_MODEL=\"{LongRestQualificationOptions.DefaultEmbeddingModel}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"EMBEDDING_WINDOW=\"{LongRestQualificationOptions.DefaultEmbeddingWindow}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"EMBEDDING_DIMENSIONS=\"{LongRestQualificationOptions.DefaultEmbeddingDimensions}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains($"ENDPOINT=\"{LongRestQualificationOptions.DefaultEndpoint}\"", wrapper, StringComparison.Ordinal);

        // The receipt: the wrapper's expected line is the test's constant, and the test writes that constant and one newline.
        Assert.Contains($"EXPECTED_QUALIFICATION_RECEIPT=\"{LongRestQualificationOptions.SuccessReceipt}\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationOptions.SuccessReceipt + global::System.Environment.NewLine", qualification, StringComparison.Ordinal);
        Assert.DoesNotContain("local-ollama-aot-qualification:v1", wrapper, StringComparison.Ordinal);

        // Options.Read really does read each variable it is given, so a renamed one on either side cannot silently fall back to a default.
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [LongRestQualificationOptions.ExecutableVariable] = "/published/arcanum",
            [LongRestQualificationOptions.ChatModelVariable] = "chat-x:1b",
            [LongRestQualificationOptions.DefaultModelVariable] = "default-x:2b",
            [LongRestQualificationOptions.EmbeddingModelVariable] = "embed-x",
            [LongRestQualificationOptions.EmbeddingWindowVariable] = "4096",
            [LongRestQualificationOptions.EmbeddingDimensionsVariable] = "1024",
            [LongRestQualificationOptions.EndpointVariable] = "http://127.0.0.1:12345/v1/",
        };

        LongRestQualificationOptions options = LongRestQualificationOptions.Read(name => environment.GetValueOrDefault(name));

        Assert.True(options.Mutiny);

        environment[LongRestQualificationOptions.MutinyVariable] = "0";

        Assert.False(LongRestQualificationOptions.Read(name => environment.GetValueOrDefault(name)).Mutiny);

        environment[LongRestQualificationOptions.MutinyVariable] = "maybe";

        Assert.Throws<InvalidOperationException>(() => LongRestQualificationOptions.Read(name => environment.GetValueOrDefault(name)));

        Assert.Equal("chat-x:1b", options.ChatModel);
        Assert.Equal("default-x:2b", options.DefaultModel);
        Assert.Equal("embed-x", options.EmbeddingModel);
        Assert.Equal(4096, options.EmbeddingWindow);
        Assert.Equal(1024, options.EmbeddingDimensions);
        Assert.Equal("http://127.0.0.1:12345/v1/", options.Endpoint.AbsoluteUri);
    }

    // ---- The owning document states what the harness now does ----

    [Fact]
    public void The_design_states_the_corpus_size_the_recovery_rule_the_exporter_start_and_the_mutiny_scenario_as_what_they_are()
    {
        string design = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "Arcanum.DESIGN.md"));

        int start = design.IndexOf("**The Long Rest counterpart.**", StringComparison.Ordinal);

        Assert.True(start >= 0, "The design no longer describes the Long Rest qualification.");

        int end = design.IndexOf("**Why Windows lanes exist.**", start, StringComparison.Ordinal);

        Assert.True(end > start, "The Long Rest qualification section no longer ends at the Windows qualification section.");

        string section = design[start..end];

        Assert.Contains(
            $"{SpellOut(LongRestQualificationCorpus.Facts.Count)} facts",
            section,
            StringComparison.Ordinal);

        Assert.DoesNotContain("about forty facts", section, StringComparison.Ordinal);
        Assert.Contains("starts with the host", section, StringComparison.Ordinal);
        Assert.DoesNotContain("known product defect", section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("to be able to compare the Long Rest counters at all", section, StringComparison.Ordinal);
        Assert.Contains("The Mutiny Scenario", section, StringComparison.Ordinal);
        Assert.Contains("--all-embedding-models", section, StringComparison.Ordinal);
        Assert.Contains("Background Floor", section, StringComparison.Ordinal);
        Assert.DoesNotContain("LexicalMinimumScore", section, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumVectorCosine", section, StringComparison.Ordinal);
        Assert.Contains("without an operator", section, StringComparison.Ordinal);
        Assert.Contains("LongRest.InvalidOutput", section, StringComparison.Ordinal);
        Assert.Contains("billed", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GET /api/health", section, StringComparison.Ordinal);
        Assert.Contains("receipt", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Embedding-Bounded Summary", section, StringComparison.Ordinal);
        Assert.Contains("--embedding-dimensions", section, StringComparison.Ordinal);
    }

    // ---- The embedding model's own vector size, and a seeding failure that says what the provider did ----

    [Fact]
    public void The_embedding_models_vector_size_must_equal_the_configured_dimensions_and_the_finding_names_the_flag()
    {
        Assert.Null(LongRestQualificationChecks.EmbeddingDimensionsFinding("qwen3-embedding:8b", 4096, 4096));

        // The default size configured for a second, smaller model: embeddinggemma returns 768.
        string? finding = LongRestQualificationChecks.EmbeddingDimensionsFinding("embeddinggemma", 4096, 768);

        Assert.NotNull(finding);
        Assert.Contains("embeddinggemma", finding, StringComparison.Ordinal);
        Assert.Contains("768", finding, StringComparison.Ordinal);
        Assert.Contains("4096", finding, StringComparison.Ordinal);
        Assert.Contains("--embedding-dimensions 768", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_answer_is_reported_by_its_first_line_and_never_echoed_whole()
    {
        Assert.Equal("first", LongRestQualificationChecks.NoValues("first\r\nsecond"));
        Assert.Equal(200, LongRestQualificationChecks.NoValues(new string('x', 5000)).Length);
        Assert.Equal(string.Empty, LongRestQualificationChecks.NoValues(string.Empty));
    }

    [Fact]
    public async Task The_dimension_probe_asks_for_one_vector_without_a_size_and_reads_its_length()
    {
        string? requestBody = null;

        await using StubUpstream upstream = await StubUpstream.StartAsync(async context =>
        {
            using StreamReader reader = new(context.Request.Body);

            requestBody = await reader.ReadToEndAsync();

            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync("{\"data\":[{\"embedding\":[0.1,0.2,0.3,0.4,0.5]}]}");
        });

        int measured = await LongRestQualificationPreflight.MeasureEmbeddingDimensionsAsync(upstream.Endpoint, "qwen3-embedding:8b", CancellationToken.None);

        Assert.Equal(5, measured);

        using JsonDocument body = JsonDocument.Parse(requestBody!);

        Assert.Equal("qwen3-embedding:8b", body.RootElement.GetProperty("model").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("input").GetString()));

        // As the host's own Saga writes ask: the provider's default size, never a requested one.
        Assert.False(body.RootElement.TryGetProperty("dimensions", out _));
    }

    [Fact]
    public async Task The_dimension_probe_fails_with_the_status_when_the_provider_refuses_the_model()
    {
        await using StubUpstream upstream = await StubUpstream.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return context.Response.WriteAsync("{\"error\":\"model not found\"}");
        });

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LongRestQualificationPreflight.MeasureEmbeddingDimensionsAsync(upstream.Endpoint, "missing-model", CancellationToken.None));

        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
        Assert.Contains("missing-model", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seeding_failure_lists_what_the_provider_answered_by_kind_and_status()
    {
        LongRestRecordedRequest[] traffic =
        [
            new() { Kind = LongRestProviderRequestKind.Chat, StatusCode = 200, Delivered = true },
            new() { Kind = LongRestProviderRequestKind.Embedding, StatusCode = 200, Delivered = true },
            new() { Kind = LongRestProviderRequestKind.Embedding, StatusCode = 500, Delivered = true },
            new() { Kind = LongRestProviderRequestKind.Embedding, StatusCode = 500, Delivered = true },
            new() { Kind = LongRestProviderRequestKind.Chat, ClientAborted = true },
            new() { Kind = LongRestProviderRequestKind.Models, StatusCode = 404, Delivered = true },
        ];

        string described = LongRestQualificationChecks.DescribeFailedRequests(traffic);

        Assert.Contains("Embedding 500 x2", described, StringComparison.Ordinal);
        Assert.Contains("Chat aborted x1", described, StringComparison.Ordinal);
        Assert.Contains("Models 404 x1", described, StringComparison.Ordinal);
        Assert.DoesNotContain("200", described, StringComparison.Ordinal);
        Assert.Equal("none", LongRestQualificationChecks.DescribeFailedRequests([]));
        Assert.Equal("none", LongRestQualificationChecks.DescribeFailedRequests([traffic[0]]));

        string seeding = ReadRunSources();

        Assert.Contains("DescribeFailedRequests(Proxy.All)", seeding, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static string SpellOut(int number)
    {
        string[] ones = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

        Assert.InRange(number, 20, 99);

        return number % 10 == 0 ? tens[number / 10] : $"{tens[number / 10]}-{ones[number % 10]}";
    }

    private static string ReadRunSources()
    {
        string packaging = Path.Combine(FindRepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "Packaging");

        return string.Concat(
            Directory.EnumerateFiles(packaging, "LongRestQualificationRun*.cs").Order(StringComparer.Ordinal).Select(File.ReadAllText));
    }

    private static HttpRequestMessage AdjudicationRequest(Uri endpoint, int memories)
    {
        string user = string.Concat(Enumerable.Range(1, memories).Select(static index => $"Memory {index:D2}\\nFact number {index}.\\n\\n"));

        HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, "chat/completions"))
        {
            Content = new StringContent(
                "{\"model\":\"gemma4:e4b\",\"reasoning_effort\":\"none\",\"messages\":[{\"role\":\"system\",\"content\":\"Memory 01\\nAn example in the instructions.\"},{\"role\":\"user\",\"content\":\""
                + user
                + "\"}],\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\""
                + LongRestPromptLayout.AdjudicationSchemaName
                + "\"}}}",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")),
        };

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-test-key");

        return request;
    }

    private static HttpRequestMessage SummaryRequest(Uri endpoint)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, "chat/completions"))
        {
            Content = new StringContent(
                "{\"model\":\"gemma4:e4b\",\"messages\":[{\"role\":\"user\",\"content\":\"summarise\"}],\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\""
                + LongRestPromptLayout.SummarySchemaName
                + "\"}}}",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")),
        };

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-test-key");

        return request;
    }

    private static Task StubChatResponse(HttpContext context)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"summary\\\":\\\"A beagle.\\\"}\"}}],"
            + "\"usage\":{\"prompt_tokens\":21,\"completion_tokens\":34,\"total_tokens\":55}}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>A loopback HTTP server standing in for Ollama, so the forwarder is exercised over real sockets.</summary>
    private sealed class StubUpstream : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private StubUpstream(WebApplication application, Uri endpoint)
        {
            _application = application;

            Endpoint = endpoint;
        }

        public Uri Endpoint { get; }

        public static async Task<StubUpstream> StartAsync(RequestDelegate handler)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });

            builder.Logging.ClearProviders();

            builder.WebHost.ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, 0));

            WebApplication application = builder.Build();

            application.Run(handler);

            await application.StartAsync();

            IServerAddressesFeature addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;

            Uri listening = new(addresses.Addresses.Single());

            return new StubUpstream(application, new Uri($"http://127.0.0.1:{listening.Port}/v1/"));
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();

            await _application.DisposeAsync();
        }
    }
}
