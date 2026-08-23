using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumApiClientTests
{

    [Fact]

    public async Task PlanFactoryResetDataAsync_posts_the_typed_planning_request()
    {

        DataRetentionWorkspaceBinding workspace = new(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "/workspace");

        InstallationResetDataPlanRequest request = new(
            InstallationResetDataScope.Workspace,
            workspace);

        DataRetentionPlan expected = new(
            "plan-test",
            new DataRetentionRequest(
                DataRetentionOperation.ResetWorkspace,
                Workspace: workspace),
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            [],
            [],
            [],
            Rows: 2,
            Files: 1,
            EstimatedBytes: 512,
            DerivedRecords: 3,
            CandidateIds: ["candidate"],
            RequiresConfirmation: true);

        string? sentJson = null;

        RecordingHandler handler = new(requestMessage =>
        {

            sentJson = requestMessage.Content!
                .ReadAsStringAsync()
                .GetAwaiter()
                .GetResult();

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<DataRetentionPlan>(expected, true, null),
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<DataRetentionPlan> result = await client.PlanFactoryResetDataAsync(request);

        Assert.True(result.IsSuccess);

        Assert.Equal(expected.PlanId, result.Value?.PlanId);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, sent.Method);

        Assert.Equal("/api/data/factory-reset/plan", sent.RequestUri!.AbsolutePath);

        Assert.Equal(
            "{\"scope\":\"Workspace\",\"workspace\":{\"campaignId\":\"44444444-4444-4444-4444-444444444444\",\"workspaceRoot\":\"/workspace\"}}",
            sentJson);

    }

    [Fact]
    public async Task ResetDataMemoryAsync_posts_the_preview_plan_id()
    {

        string? sentJson = null;

        DataRetentionApplyResult expected = new(
            Guid.Parse("53535353-5353-5353-5353-535353535353"),
            "memory-plan-53",
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            DerivedRecordsDeleted: 0,
            Reconciled: true,
            Blockers: [],
            Conflicts: []);

        RecordingHandler handler = new(requestMessage =>
        {

            sentJson = requestMessage.Content!
                .ReadAsStringAsync()
                .GetAwaiter()
                .GetResult();

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<DataRetentionApplyResult>(expected, true, null),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<DataRetentionApplyResult> result = await client.ResetDataMemoryAsync(
            new MemoryResetRequest(
                MemoryResetScope.Covenant,
                ExpectedPlanId: "memory-plan-53"));

        Assert.True(result.IsSuccess);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, sent.Method);

        Assert.Equal("/api/data/memory/reset", sent.RequestUri!.AbsolutePath);

        Assert.Equal(
            "{\"scope\":\"Covenant\",\"expectedPlanId\":\"memory-plan-53\"}",
            sentJson);

    }

    [Theory]
    [InlineData(false, "{\"confirmation\":\"factory-reset\"}")]
    [InlineData(true, "{\"confirmation\":\"factory-reset\",\"expectedPlanId\":\"factory-plan-128\",\"requestedOperationId\":\"53535353-5353-4353-8353-535353535353\"}")]
    public async Task FactoryResetDataAsync_emits_named_fields_together_and_omits_nulls(
        bool named,
        string expectedJson)
    {

        string? sentJson = null;

        DataRetentionApplyResult expected = new(
            Guid.Parse("54545454-5454-4454-8454-545454545454"),
            "factory-plan-128",
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            DerivedRecordsDeleted: 0,
            Reconciled: true,
            Blockers: [],
            Conflicts: [],
            RequestedOperationId: named
                ? Guid.Parse("53535353-5353-4353-8353-535353535353")
                : null);

        RecordingHandler handler = new(requestMessage =>
        {

            sentJson = requestMessage.Content!
                .ReadAsStringAsync()
                .GetAwaiter()
                .GetResult();

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<DataRetentionApplyResult>(expected, true, null),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        FactoryResetRequest request = named
            ? new FactoryResetRequest(
                "factory-reset",
                ExpectedPlanId: "factory-plan-128",
                RequestedOperationId: Guid.Parse("53535353-5353-4353-8353-535353535353"))
            : new FactoryResetRequest("factory-reset");

        Result<DataRetentionApplyResult> result = await client.FactoryResetDataAsync(request);

        Assert.True(result.IsSuccess);

        Assert.Equal(expectedJson, sentJson);

    }

    [Fact]
    public async Task Installation_reset_online_validator_carries_the_matching_online_plan()
    {

        DataRetentionPlan onlinePlan = CreateDataRetentionPlan(
            "factory-plan-71",
            new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 2,
                DisclosureCountKind: CovenantDisclosureCountKind.LowerBound));

        RecordingHandler handler = new(_ => CreateDataRetentionPlanResponse(
            new ApiResponse<DataRetentionPlan>(onlinePlan, true, null)));

        InstallationResetOnlinePlanValidator validator = new(
            CreateClient(handler, apiKey: "test-key"));

        Result<InstallationResetOnlinePlanValidation> result = await validator.ValidateAsync(
            CreateInstallationResetPlan("factory-plan-71"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("factory-plan-71", result.Value.Plan?.PlanId);

        Assert.Equal(onlinePlan.Covenant, result.Value.Plan?.Covenant);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/data/factory-reset/plan", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public async Task Installation_reset_online_validator_returns_unreachable_failure()
    {

        RecordingHandler handler = new(_ => throw new HttpRequestException("unreachable"));

        InstallationResetOnlinePlanValidator validator = new(
            CreateClient(handler, apiKey: "test-key"));

        Result<InstallationResetOnlinePlanValidation> result = await validator.ValidateAsync(
            CreateInstallationResetPlan("factory-plan-72"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Connection.Unreachable, result.Error.Code);

        Assert.Single(handler.Requests);

    }

    [Fact]
    public async Task Installation_reset_online_validator_returns_missing_api_key_failure()
    {

        RecordingHandler handler = new(_ => throw new InvalidOperationException(
            "The request must not be sent without an API key."));

        InstallationResetOnlinePlanValidator validator = new(
            CreateClient(handler, apiKey: null));

        Result<InstallationResetOnlinePlanValidation> result = await validator.ValidateAsync(
            CreateInstallationResetPlan("factory-plan-73"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Security.MissingApiKey, result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task Installation_reset_online_validator_propagates_other_host_failure()
    {

        RecordingHandler handler = new(_ => CreateDataRetentionPlanResponse(
            new ApiResponse<DataRetentionPlan>(
                null,
                false,
                new Error("Test.HostFailure", "The host rejected the plan.")),
            HttpStatusCode.Conflict));

        InstallationResetOnlinePlanValidator validator = new(
            CreateClient(handler, apiKey: "test-key"));

        Result<InstallationResetOnlinePlanValidation> result = await validator.ValidateAsync(
            CreateInstallationResetPlan("factory-plan-74"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Test.HostFailure", result.Error.Code);

    }

    [Fact]
    public async Task Installation_reset_online_validator_accepts_a_different_authenticated_plan_id()
    {

        DataRetentionPlan onlinePlan = CreateDataRetentionPlan("other-plan-75", covenant: null);

        RecordingHandler handler = new(_ => CreateDataRetentionPlanResponse(
            new ApiResponse<DataRetentionPlan>(
                onlinePlan,
                true,
                null)));

        InstallationResetOnlinePlanValidator validator = new(
            CreateClient(handler, apiKey: "test-key"));

        Result<InstallationResetOnlinePlanValidation> result = await validator.ValidateAsync(
            CreateInstallationResetPlan("factory-plan-75"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(onlinePlan.PlanId, result.Value.Plan?.PlanId);

        Assert.NotEqual("factory-plan-75", result.Value.Plan?.PlanId);

        Assert.Equivalent(onlinePlan, result.Value.Plan, strict: true);

    }

    [Fact]
    public async Task ListLoreAsync_rejects_a_non_advancing_page_instead_of_using_a_total_page_limit()
    {

        RecordingHandler handler = new(_ =>
        {

            ListPageResult<LoreDto> page = new(
                [new LoreDto("key", "value", DateTime.UtcNow)],
                HasMore: true,
                NextOffset: 0);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<ListPageResult<LoreDto>>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseListPageResultLoreDto);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<List<LoreDto>> result = await client.ListLoreAsync();

        Assert.False(result.IsSuccess);

        Assert.Equal("Api.PaginationNoProgress", result.Error.Code);

        Assert.Single(handler.Requests);

    }

    [Fact]
    public async Task GetSpellCatalogPageAsync_sends_paged_filters_and_cursor()
    {

        SpellCatalogPage expected = new(
            [new SpellSummary(
                "catalog-spell",
                "description",
                SpellSource.Workspace,
                [])],
            true,
            "opaque-next",
            "continue");

        RecordingHandler handler = new(_ =>
        {

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<SpellCatalogPage>(expected, true, null),
                ArcanumJsonContext.Default.ApiResponseSpellCatalogPage);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<SpellCatalogPage> result = await client.GetSpellCatalogPageAsync(
            workspace: "/workspace root",
            cursor: "opaque prior",
            query: "needle",
            tag: "review",
            tool: "read_file",
            source: SpellSource.Workspace,
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("opaque-next", result.Value?.NextCursor);

        Uri requestUri = Assert.Single(handler.Requests).RequestUri!;

        Assert.Equal("/api/spells", requestUri.AbsolutePath);

        Assert.Contains("paged=true", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("workspace=%2Fworkspace%20root", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("cursor=opaque%20prior", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("q=needle", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("tag=review", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("tool=read_file", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains("source=Workspace", requestUri.Query, StringComparison.Ordinal);

    }

    [Fact]

    public async Task PreviewContextAsync_posts_typed_read_only_request()

    {

        ContextPreviewResult preview = ContextPreviewTestData.Create();

        string? sentJson = null;

        RecordingHandler handler = new(request =>

        {

            sentJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)

            {

                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(

                    new ApiResponse<ContextPreviewResult>(preview, true, null),

                    ArcanumJsonContext.Default.ApiResponseContextPreviewResult)),

            };

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        ContextPreviewRequest request = new(

            Prompt: "hello",

            ShowContent: true,

            NoRetrieval: true);

        Result<ContextPreviewResult> result = await client.PreviewContextAsync(request);

        Assert.True(result.IsSuccess);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal("/api/intelligence/context/inspect", sent.RequestUri!.AbsolutePath);

        Assert.Contains("\"showContent\":true", sentJson, StringComparison.Ordinal);

        Assert.Contains("\"noRetrieval\":true", sentJson, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Configuration_methods_use_config_api_contracts()
    {

        RecordingHandler handler = new(request =>
        {

            if (request.Method == HttpMethod.Get)
            {

                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                    new ApiResponse<ArcanumSettings>(new ArcanumSettings(), true, null),
                    ArcanumJsonContext.Default.ApiResponseArcanumSettings);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(payload),

                };

            }

            return CreateBooleanResponse(new ApiResponse<bool>(true, true, null));

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        ArcanumSettings settings = new();

        Result<ArcanumSettings> read = await client.GetConfigurationAsync();

        Result<bool> validation = await client.ValidateConfigurationAsync(settings);

        Result<bool> write = await client.UpdateConfigurationAsync(settings);

        Assert.True(read.IsSuccess);

        Assert.True(validation.IsSuccess);

        Assert.True(write.IsSuccess);

        Assert.Collection(
            handler.Requests,
            request =>
            {

                Assert.Equal(HttpMethod.Get, request.Method);

                Assert.Equal("/api/config", request.RequestUri!.AbsolutePath);

            },
            request =>
            {

                Assert.Equal(HttpMethod.Post, request.Method);

                Assert.Equal("/api/config/validate", request.RequestUri!.AbsolutePath);

            },
            request =>
            {

                Assert.Equal(HttpMethod.Put, request.Method);

                Assert.Equal("/api/config", request.RequestUri!.AbsolutePath);

            });

    }

    [Fact]
    public async Task AskAsync_returns_failure_when_api_key_missing()
    {

        RecordingHandler handler = new();

        ArcanumApiClient client = CreateClient(handler, apiKey: null);

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Security.MissingApiKey", result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task AskAsync_posts_ping_request_with_api_key_header()
    {

        RecordingHandler handler = new(_ => CreatePromptResponse(
            new ApiResponse<PromptResponseDto>(
                new PromptResponseDto("pong", null),
                true,
                null)));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("pong", result.Value);

        Assert.Single(handler.Requests);

        HttpRequestMessage request = handler.Requests[0];

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/intelligence/ping", request.RequestUri!.AbsolutePath);

        Assert.True(request.Headers.TryGetValues(ArcanumApiHeaders.ApiKey, out IEnumerable<string>? keys));

        Assert.Equal("test-key", keys!.Single());

    }

    [Fact]
    public async Task AskAsync_returns_envelope_error_on_http_failure()
    {

        Error apiError = new("Api.RateLimited", "Too many requests.");

        RecordingHandler handler = new(_ => CreatePromptResponse(
            new ApiResponse<PromptResponseDto>(null, false, apiError),
            HttpStatusCode.TooManyRequests));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(apiError, result.Error);

    }

    [Fact]
    public async Task AskAsync_returns_invalid_response_when_body_is_not_json()
    {

        // A non-JSON body (e.g. proxy HTML on a 401/429, or a truncated response) must not
        // escape as an unhandled JsonException; it should map to Api.InvalidResponse.

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>bad</html>")),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.InvalidResponse", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_response_too_large_when_declared_content_length_exceeds_cap()
    {

        // A misbehaving/compromised local API declaring an oversized Content-Length must be rejected
        // before any buffering is attempted — the fake declared length here (100 MiB) exceeds
        // ArcanumApiClient's cap without this test needing to actually transfer that many bytes.
        RecordingHandler handler = new(_ =>
        {

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };

            response.Content.Headers.ContentLength = 100 * 1024 * 1024;

            return response;

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.ResponseTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_connection_error_when_handler_throws_http_request_exception()
    {

        RecordingHandler handler = new(_ => throw new HttpRequestException("connection refused"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Unreachable", result.Error.Code);

        Assert.Contains("unreachable", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AskAsync_returns_disconnected_error_when_handler_throws_io_exception()
    {

        RecordingHandler handler = new(_ => throw new IOException("connection reset by peer"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Unreachable", result.Error.Code);

        Assert.Contains("lost before the response completed", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AskAsync_returns_unexpected_error_result_when_handler_throws_unanticipated_exception()
    {

        // Any exception type not explicitly mapped (OperationCanceledException, HttpRequestException,
        // IOException) must still surface as a controlled Result.Failure — never propagate past the
        // client to a command's caller / ConsoleAppFramework's generic top-level exception handler.
        RecordingHandler handler = new(_ => throw new InvalidOperationException("handler misconfigured"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.UnexpectedError", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_timeout_when_bounded_client_exceeds_deadline()
    {

        RecordingHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            return CreatePromptResponse(
                new ApiResponse<PromptResponseDto>(
                    new PromptResponseDto("late", null),
                    true,
                    null));
        });

        ArcanumApiClient client = CreateClient(
            handler,
            apiKey: "test-key",
            requestTimeout: TimeSpan.FromMilliseconds(100));

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Timeout", result.Error.Code);

    }

    [Fact]
    public async Task AskStreamAsync_yields_error_when_stream_disconnects_mid_read()
    {

        IntelligenceEvent token = new(IntelligenceEventType.Token, "partial");

        byte[] firstLine = JsonSerializer.SerializeToUtf8Bytes(token, ArcanumJsonContext.Default.IntelligenceEvent);

        DisconnectingStreamHandler handler = new(firstLine);

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in client.AskStreamAsync(body, CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(2, events.Count);

        Assert.Equal(IntelligenceEventType.Token, events[0].Type);

        Assert.Equal(IntelligenceEventType.Error, events[1].Type);

        Assert.Contains("lost before the stream completed", events[1].Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AskStreamAsync_yields_reasoning_skips_unknown_type_and_continues()
    {
        IntelligenceEvent reasoning = new(
            IntelligenceEventType.Reasoning,
            "client-safe summary",
            Reasoning: new ReasoningContentSegment(
                "client-safe summary",
                ReasoningOutputMode.Summary));
        IntelligenceEvent token = new(IntelligenceEventType.Token, string.Empty, "answer");
        IntelligenceEvent result = new(IntelligenceEventType.Result, "answer", "answer");

        string ndjson = string.Join(
            '\n',
            JsonSerializer.Serialize(reasoning, ArcanumJsonContext.Default.IntelligenceEvent),
            """{"type":"futureThought","message":"ignore me","data":"secret"}""",
            JsonSerializer.Serialize(token, ArcanumJsonContext.Default.IntelligenceEvent),
            JsonSerializer.Serialize(result, ArcanumJsonContext.Default.IntelligenceEvent))
            + "\n";
        RecordingHandler handler = new(_ => CreateNdjsonResponse(ndjson));
        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.AskStreamAsync(new PingRequest("hello"), CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(
            [
                IntelligenceEventType.Reasoning,
                IntelligenceEventType.Token,
                IntelligenceEventType.Result,
            ],
            events.Select(static evt => evt.Type));
        Assert.Equal("client-safe summary", events[0].Reasoning?.Text);
        Assert.DoesNotContain(events, static evt => evt.Message.Contains("Malformed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskStreamAsync_reports_malformed_or_missing_type_and_continues()
    {
        IntelligenceEvent token = new(IntelligenceEventType.Token, string.Empty, "answer");
        string ndjson = string.Join(
            '\n',
            """{"type":""",
            """{"message":"missing"}""",
            """{"type":42,"message":"invalid"}""",
            JsonSerializer.Serialize(token, ArcanumJsonContext.Default.IntelligenceEvent))
            + "\n";
        RecordingHandler handler = new(_ => CreateNdjsonResponse(ndjson));
        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.AskStreamAsync(new PingRequest("hello"), CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(3, events.Count(static evt =>
            evt.Type == IntelligenceEventType.Status
            && evt.Message == "Malformed data received from server. Skipping frame."));
        Assert.Equal(IntelligenceEventType.Token, events[^1].Type);
        Assert.Equal("answer", events[^1].Data);
    }

    [Fact]
    public async Task AskStreamAsync_fragmented_frames_mirror_strict_type_semantics_and_continue()
    {
        string ndjson = string.Join(
            '\n',
            """{"type":"TOKEN","message":"","data":"upper"}""",
            """{"type":"futureThought","message":"skip"}""",
            """{"type":" token ","message":"padded"}""",
            """{"type":"","message":"blank"}""",
            """{"type":"   ","message":"whitespace"}""",
            """{"message":"missing"}""",
            """{"type":42,"message":"numeric"}""",
            """{"type":"token","message":"","data":"tail"}""")
            + "\n";
        FragmentingStreamHandler handler = new(Encoding.UTF8.GetBytes(ndjson), maxChunkBytes: 2);
        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.AskStreamAsync(
                           new PingRequest("hello"),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(IntelligenceEventType.Token, events[0].Type);
        Assert.Equal("upper", events[0].Data);
        Assert.Equal(
            5,
            events.Count(static evt =>
                evt.Type == IntelligenceEventType.Status
                && evt.Message == "Malformed data received from server. Skipping frame."));
        Assert.Equal(IntelligenceEventType.Token, events[^1].Type);
        Assert.Equal("tail", events[^1].Data);
        Assert.Equal(7, events.Count);
    }

    [Fact]
    public async Task AskStreamAsync_reassembles_split_multibyte_utf8_characters()
    {
        const string multibyteText = "before 😀 漢字 after";
        IntelligenceEvent token = new(
            IntelligenceEventType.Token,
            string.Empty,
            multibyteText);
        string ndjson = JsonSerializer.Serialize(
            token,
            ArcanumJsonContext.Default.IntelligenceEvent) + "\n";
        FragmentingStreamHandler handler = new(Encoding.UTF8.GetBytes(ndjson), maxChunkBytes: 1);
        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.AskStreamAsync(
                           new PingRequest("hello"),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        IntelligenceEvent parsed = Assert.Single(events);
        Assert.Equal(multibyteText, parsed.Data);
    }

    [Fact]
    public void IntelligenceEvent_source_generated_deserialization_remains_strict_for_unknown_type()
    {
        const string json = """{"type":"futureThought","message":"ignore me"}""";

        _ = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.IntelligenceEvent));
    }

    [Fact]
    public void IntelligenceEvent_discriminator_recognizes_every_defined_wire_value()
    {
        foreach (IntelligenceEventType type in Enum.GetValues<IntelligenceEventType>())
        {
            string json = JsonSerializer.Serialize(
                new IntelligenceEvent(type, "test"),
                ArcanumJsonContext.Default.IntelligenceEvent);

            Assert.Equal(
                IntelligenceEventDiscriminatorResult.Known,
                IntelligenceEventDiscriminator.Inspect(json));
        }
    }

    [Fact]
    public async Task StreamApprenticeChronicleAsync_parses_sse_frames_via_source_generated_context()
    {

        string sse =
            "data: {\"type\":\"tool_result\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"result\":\"did the thing\"}\n\n" +
            "data: {\"type\":\"status\"}\n\n" +
            "data: [DONE]\n\n";

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<ChronicleFrame> frames = [];

        await foreach (ChronicleFrame frame in client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);

        Assert.Equal("tool_result", frames[0].Type);

        Assert.Equal("did the thing", frames[0].Message);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), frames[0].Timestamp);

        Assert.Equal("status", frames[1].Type);

        Assert.Equal("status", frames[1].Message);

        Assert.Null(frames[1].Timestamp);

    }

    [Fact]
    public async Task StreamApprenticeChronicleAsync_skips_frames_with_malformed_json()
    {

        string sse =
            "data: not valid json\n\n" +
            "data: {\"type\":\"status\"}\n\n" +
            "data: [DONE]\n\n";

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<ChronicleFrame> frames = [];

        await foreach (ChronicleFrame frame in client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Single(frames);

        Assert.Equal("status", frames[0].Type);

    }

    /// <summary>
    /// <c>arcanum watch session</c> streams through <c>WatchSseAsync</c>/<c>WatchSseParser</c>. A
    /// second hand-rolled SSE reader on the same client is unreachable duplication that silently
    /// drifts from the tested parser, so the client must expose exactly one session-stream reader.
    /// </summary>
    [Fact]
    public void The_client_exposes_no_second_session_stream_reader()
    {

        string[] declared = [.. typeof(ArcanumApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.Contains("WatchSseAsync", declared);

        Assert.DoesNotContain("WatchSessionAsync", declared);

    }

    [Fact]
    public async Task ResearchWebAsync_surfaces_the_api_error_envelope_on_a_non_success_status()
    {

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<string>(
                null,
                false,
                new Error("Auth.Unauthorized", "Invalid or missing API key.")),
            ArcanumJsonContext.Default.ApiResponseString);

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new ByteArrayContent(payload),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<WebResearchStreamFrame> frames = [];

        await foreach (WebResearchStreamFrame frame in client.ResearchWebAsync(
            new WebResearchWorkflowRequest { Question = "why" },
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        WebResearchStreamFrame only = Assert.Single(frames);

        Assert.Equal(WebResearchStreamFrameType.Error, only.Type);

        Assert.Equal("Auth.Unauthorized", only.Code);

        Assert.Equal("Invalid or missing API key.", only.Message);

    }

    [Fact]
    public async Task ResearchWebAsync_falls_back_to_the_status_line_when_the_body_is_not_an_envelope()
    {

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>proxy failure</html>"),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<WebResearchStreamFrame> frames = [];

        await foreach (WebResearchStreamFrame frame in client.ResearchWebAsync(
            new WebResearchWorkflowRequest { Question = "why" },
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        WebResearchStreamFrame only = Assert.Single(frames);

        Assert.Equal(WebResearchStreamFrameType.Error, only.Type);

        Assert.Equal("Api.HttpError", only.Code);

        Assert.Contains("502", only.Message);

    }

    /// <summary>
    /// Issue #33 — <c>POST /api/web/research</c> is an unbounded multi-pass progress stream, so a
    /// <c>serve</c> restart mid-research is routine. Every sibling stream on this client maps that to
    /// the transport copy; research read its lines bare, so the disconnect escaped the async iterator
    /// and the operator was told "An unexpected CLI error occurred." instead.
    /// </summary>
    [Fact]
    public async Task ResearchWebAsync_yields_an_error_frame_when_the_stream_disconnects_mid_read()
    {

        WebResearchStreamFrame progress = new()
        {
            Type = WebResearchStreamFrameType.Progress,
            Stage = "search",
            Message = "Searching the web.",
        };

        byte[] firstLine = JsonSerializer.SerializeToUtf8Bytes(
            progress,
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        DisconnectingStreamHandler handler = new(firstLine);

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<WebResearchStreamFrame> frames = [];

        await foreach (WebResearchStreamFrame frame in client.ResearchWebAsync(
            new WebResearchWorkflowRequest { Question = "why" },
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);

        Assert.Equal(WebResearchStreamFrameType.Progress, frames[0].Type);

        Assert.Equal(WebResearchStreamFrameType.Error, frames[1].Type);

        Assert.Equal(ErrorCodes.Connection.Unreachable, frames[1].Code);

        Assert.Contains(
            "lost before the stream completed",
            frames[1].Message,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task SubmitHumanResponseAsync_returns_success_when_envelope_data_is_true()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<bool> result = await client.SubmitHumanResponseAsync("prompt-1", "answer", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value);

        Assert.Equal("/api/intelligence/human-response", handler.Requests[0].RequestUri!.AbsolutePath);

    }

    [Fact]
    public async Task GetSessionAttachmentsAsync_deserializes_bound_rows()
    {

        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                1,
                $"{sessionId:N}/notes/v1/notes.txt",
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z")),
        ];

        RecordingHandler handler = new(_ =>
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<SessionAttachmentDto[]>(payload, true, null),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(json),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            return response;
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<SessionAttachmentDto[]> result = await client.GetSessionAttachmentsAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Single(result.Value!);

        Assert.Equal(attachmentId, result.Value![0].Id);

        Assert.Equal("notes", result.Value[0].LogicalKey);

        Assert.Equal(SessionAttachmentKind.Text, result.Value[0].Kind);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);

        Assert.Equal($"/api/sessions/{sessionId:D}/attachments", handler.Requests[0].RequestUri!.AbsolutePath);

    }

    [Fact]
    public async Task GetSessionAttachmentsAsync_returns_not_found_on_404()
    {

        Guid sessionId = Guid.NewGuid();

        RecordingHandler handler = new(_ =>
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<SessionAttachmentDto[]>(
                    null,
                    false,
                    new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

            HttpResponseMessage response = new(HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(json),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            return response;
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<SessionAttachmentDto[]> result = await client.GetSessionAttachmentsAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);

    }

    private static InstallationResetPlan CreateInstallationResetPlan(string dataPlanId) =>
        new(
            "installation-plan-71",
            InstallationResetScope.Global,
            Workspace: null,
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            DataInventoryAvailable: true,
            CredentialInventoryAvailable: true,
            Targets: [],
            Credentials: [],
            PreservedBackups: [],
            Exclusions: [],
            Blockers: [],
            Rows: 0,
            Files: 0,
            EstimatedBytes: 0,
            new InstallationResetAcceptedBinding(
                "binding-71",
                SelectedRoots: [],
                ExcludedRoots: [],
                PreservedBackups: [],
                CredentialAccounts: [],
                DataPlanIds: [dataPlanId]));

    private static DataRetentionPlan CreateDataRetentionPlan(
        string planId,
        DataRetentionCovenantInventory? covenant) =>
        new(
            planId,
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            Items: [],
            Blockers: [],
            Conflicts: [],
            Rows: 0,
            Files: 0,
            EstimatedBytes: 0,
            DerivedRecords: 0,
            CandidateIds: [],
            RequiresConfirmation: true,
            Covenant: covenant);

    private static HttpResponseMessage CreateDataRetentionPlanResponse(
        ApiResponse<DataRetentionPlan> envelope,
        HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        HttpResponseMessage response = new(status)
        {

            Content = new ByteArrayContent(payload),

        };

        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private static ArcanumApiClient CreateClient(HttpMessageHandler handler, string? apiKey)
    {

        return CreateClient(handler, apiKey, requestTimeout: null);

    }

    private static ArcanumApiClient CreateClient(
        HttpMessageHandler handler,
        string? apiKey,
        TimeSpan? requestTimeout)
    {

        FakeHttpClientFactory factory = new(handler, requestTimeout);

        FakeSecretStore secretStore = new() { ApiKey = apiKey };

        return new ArcanumApiClient(factory, secretStore);

    }

    private static HttpResponseMessage CreatePromptResponse(ApiResponse<PromptResponseDto> envelope, HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        HttpResponseMessage response = new(status)
        {
            Content = new ByteArrayContent(json),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private static HttpResponseMessage CreateBooleanResponse(ApiResponse<bool> envelope, HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseBoolean);

        HttpResponseMessage response = new(status)
        {
            Content = new ByteArrayContent(json),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private static HttpResponseMessage CreateNdjsonResponse(string ndjson)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
        };

        return response;
    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public string? ApiKey { get; set; }

    public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(ApiKey)
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(ApiKey!));

    public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler, TimeSpan? requestTimeout) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name)
        {

            HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

            if (string.Equals(name, ArcanumApiClient.RequestHttpClientName, StringComparison.Ordinal))
            {
                client.Timeout = requestTimeout ?? Timeout.InfiniteTimeSpan;
            }
            else
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            }

            return client;

        }

    }

    private sealed class RecordingHandler : HttpMessageHandler
    {

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            _responder = responder is null
                ? (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : (request, _) => Task.FromResult(responder(request));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri)
            {
                Content = request.Content,
            };

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            Requests.Add(snapshot);

            return await _responder(request, cancellationToken).ConfigureAwait(false);

        }

    }

    private sealed class DisconnectingStreamHandler(byte[] firstLineBytes) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            byte[] payload = new byte[firstLineBytes.Length + 1];

            firstLineBytes.CopyTo(payload, 0);

            payload[^1] = (byte)'\n';

            DisconnectingResponseStream stream = new(payload);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");

            return Task.FromResult(response);

        }

    }

    private sealed class DisconnectingResponseStream(byte[] firstChunk) : Stream
    {

        private bool _firstChunkDelivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {

            throw new NotSupportedException();

        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {

            if (!_firstChunkDelivered)
            {
                int copied = Math.Min(buffer.Length, firstChunk.Length);

                firstChunk.AsSpan(0, copied).CopyTo(buffer);

                _firstChunkDelivered = true;

                return copied;
            }

            throw new IOException("Simulated mid-stream disconnect.");

        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            await Task.Yield();

            return Read(buffer.Span);

        }

    }

    private sealed class FragmentingStreamHandler(byte[] payload, int maxChunkBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FragmentingResponseStream(payload, maxChunkBytes)),
            });
    }

    private sealed class FragmentingResponseStream(byte[] payload, int maxChunkBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= payload.Length)
            {
                return 0;
            }

            int count = Math.Min(Math.Min(buffer.Length, maxChunkBytes), payload.Length - _position);
            payload.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer.Span);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
