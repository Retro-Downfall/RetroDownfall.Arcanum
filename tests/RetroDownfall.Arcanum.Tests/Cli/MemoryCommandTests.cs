using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]

[Collection("GlobalConsole")]

public sealed class MemoryCommandTests
{
    [Theory]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("search", false)]
    [InlineData("search", true)]
    public void Lexicon_plain_inspection_identifies_retrieval_retirement_and_pin_state(string verb, bool retired)
    {
        LexiconEntryDto entry = InspectionEntry(retired);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<LexiconListDto>(new([entry]), true, null),
            ArcanumJsonContext.Default.ApiResponseLexiconListDto));

        CliTestResult result = RunCommand(handler,
            ["memory", "lexicon", verb, .. verb == "search" ? new[] { "Operator" } : [], "--plain"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains($"Retrieval: {(retired ? "Retired" : "Eligible")}", result.Output, StringComparison.Ordinal);

        Assert.Contains(retired ? "retired: 2026-09-20 12:34:56Z" : "retired: not retired", result.Output, StringComparison.Ordinal);

        Assert.Contains(retired ? "pinned: 2026-09-21 01:02:03Z" : "pinned: not pinned", result.Output, StringComparison.Ordinal);

        Assert.Contains("Current visible fact", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("search")]
    public void Lexicon_json_inspection_retains_the_complete_entry_array(string verb)
    {
        LexiconEntryDto[] entries = [InspectionEntry(true), InspectionEntry(false)];

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<LexiconListDto>(new(entries), true, null),
            ArcanumJsonContext.Default.ApiResponseLexiconListDto));

        CliTestResult result = RunCommand(handler,
            ["memory", "lexicon", verb, .. verb == "search" ? new[] { "Operator" } : [], "--json"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(JsonSerializer.Serialize(entries, ArcanumJsonContext.Default.LexiconEntryDtoArray), result.Output.Trim());

        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_plain_search_identifies_lexicon_lifecycle_without_changing_other_stores(bool retired)
    {
        LexiconEntryDto entry = InspectionEntry(retired);

        MemorySearchResponse payload = SearchInspection(entry);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "visible", "--plain"]);

        Assert.Equal(0, result.ExitCode);

        string lexicon = result.Output.Split("[saga]", StringSplitOptions.None)[0];

        Assert.Contains($"Retrieval: {(retired ? "Retired" : "Eligible")}", lexicon, StringComparison.Ordinal);

        Assert.Contains(retired ? "retired: 2026-09-20 12:34:56Z" : "retired: not retired", lexicon, StringComparison.Ordinal);

        Assert.Contains(retired ? "pinned: 2026-09-21 01:02:03Z" : "pinned: not pinned", lexicon, StringComparison.Ordinal);

        Assert.Equal("Visible Saga\n  Saga visible content\n  Provenance: Saga source\n  Retention: Saga retention",
            result.Output.Split("[saga] ", StringSplitOptions.None)[1].Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Generic_plain_search_renders_typed_store_specific_follow_ups()
    {
        MemorySearchResponse payload = new("visible", MemorySearchScope.All,
        [
            new(
                MemorySearchScope.Session,
                "Session",
                "session content",
                "session source",
                "session retention",
                "session-id"),
            new(
                MemorySearchScope.Saga,
                "Saga",
                "saga content",
                "saga source",
                "saga retention",
                "saga-id",
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowSagaMemory,
                    Saga: new MemorySagaTargetDto("saga-id"))),
            new(
                MemorySearchScope.Lexicon,
                "Lexicon",
                "lexicon content",
                "lexicon source",
                "lexicon retention",
                "lexicon-id",
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowLexiconEntry,
                    Lexicon: new MemoryLexiconTargetDto(
                        "Operator",
                        new LexiconCurationScope(LexiconScopeKind.Campaign, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"))))),
        ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "visible", "--plain"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Next action: show Saga memory", result.Output, StringComparison.Ordinal);

        Assert.Contains("Memory id: saga-id", result.Output, StringComparison.Ordinal);

        Assert.Contains(
            "Next action: show Lexicon entry",
            result.Output,
            StringComparison.Ordinal);

        Assert.Contains("Name: Operator", result.Output, StringComparison.Ordinal);

        Assert.Contains(
            "Scope: Campaign aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Next: arcanum", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("O'Brien")]
    [InlineData("Operator; continue")]
    [InlineData("$(whoami)")]
    public void Generic_plain_search_renders_lexicon_follow_up_names_as_data(string name)
    {
        Guid campaignId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        MemorySearchResponse payload = new("visible", MemorySearchScope.Lexicon,
        [
            new(
                MemorySearchScope.Lexicon,
                "Lexicon",
                "lexicon content",
                "lexicon source",
                "lexicon retention",
                "lexicon-id",
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowLexiconEntry,
                    Lexicon: new MemoryLexiconTargetDto(
                        name,
                        new LexiconCurationScope(LexiconScopeKind.Campaign, campaignId)))),
        ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "visible", "--plain"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Next action: show Lexicon entry", result.Output, StringComparison.Ordinal);

        Assert.Contains($"Name: {name}", result.Output, StringComparison.Ordinal);

        Assert.Contains($"Scope: Campaign {campaignId:D}", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain($"arcanum memory lexicon show {name}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_plain_search_does_not_infer_eligibility_from_missing_lexicon_metadata()
    {
        MemorySearchResponse payload = new("visible", MemorySearchScope.All,
            [new(MemorySearchScope.Lexicon, "Legacy Lexicon", "Current visible fact", "Lexicon source", "Lexicon retention", "legacy")]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "visible", "--plain"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Retrieval: unknown", result.Output, StringComparison.Ordinal);

        Assert.Contains("retired: unknown", result.Output, StringComparison.Ordinal);

        Assert.Contains("pinned: unknown", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Eligible", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_json_search_keeps_its_existing_complete_response_shape()
    {
        MemorySearchResponse payload = SearchInspection(InspectionEntry(true));

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "visible", "--json"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(JsonSerializer.Serialize(payload, ArcanumJsonContext.Default.MemorySearchResponse), result.Output.Trim());

        Assert.Empty(result.Error);
    }

    private static LexiconEntryDto InspectionEntry(bool retired) => new(
        Guid.Parse("68da8f04-a6f1-47cb-b6a6-64b9dff75457"), "Operator", "Person", ["Current visible fact"], DateTimeOffset.UnixEpoch,
        RetiredAtUtc: retired ? new DateTimeOffset(2026, 9, 20, 12, 34, 56, TimeSpan.Zero) : null,
        PinnedAtUtc: retired ? new DateTimeOffset(2026, 9, 21, 1, 2, 3, TimeSpan.Zero) : null,
        Eligibility: retired ? LexiconRetrievalEligibility.Retired : LexiconRetrievalEligibility.Eligible);

    private static MemorySearchResponse SearchInspection(LexiconEntryDto entry) => new("visible", MemorySearchScope.All,
        [
            new(MemorySearchScope.Lexicon, "Visible Lexicon", "Current visible fact", "Lexicon source", "Lexicon retention", "lexicon",
                LexiconLifecycle: new(entry.RetiredAtUtc, entry.PinnedAtUtc), LexiconEligibility: entry.Eligibility),
            new(MemorySearchScope.Saga, "Visible Saga", "Saga visible content", "Saga source", "Saga retention", "saga"),
        ]);

    [Theory]
    [InlineData("list", "")]
    [InlineData("search", "?q=ward%20policy")]
    public void Lexicon_list_and_search_keep_the_existing_read_contract(string verb, string expectedQuery)
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<LexiconListDto>(new([]), true, null),
            ArcanumJsonContext.Default.ApiResponseLexiconListDto));

        CliTestResult result = RunCommand(handler,
            ["memory", "lexicon", verb, .. verb == "search" ? new[] { "ward policy" } : []]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/memory/lexicon", request.RequestUri!.AbsolutePath);

        Assert.Equal(expectedQuery, request.RequestUri.Query);
    }

    [Fact]

    public void Memory_status_uses_active_session_and_renders_each_distinct_store()
    {
        Guid sessionId = Guid.NewGuid();

        MemoryStatusDto payload = new(
            sessionId,
            "Current task",
            [
                new MemoryStoreStatusDto("Session Entries", true, 4, "session", "Session lifetime"),
                new MemoryStoreStatusDto("Pinned Entries", true, 1, "session", "Until explicitly unpinned or deleted"),
                new MemoryStoreStatusDto("Campaign Summary", true, 1, "session", "Session lifetime"),
                new MemoryStoreStatusDto("Attachments", true, 2, "attachments", "Bound to the session"),
                new MemoryStoreStatusDto("Indexed Attachment Chunks", true, 7, "attachments", "Rebuilt from attachments"),
                new MemoryStoreStatusDto("Lexicon", true, 3, "lexicon", "Durable until explicit entity deletion"),
                new MemoryStoreStatusDto("Saga", true, 5, "saga", "Durable until explicit Saga deletion"),
                new MemoryStoreStatusDto("Workspace Index", true, 11, "workspace", "Rebuilt from workspace files"),
            ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemoryStatusDto>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto));

        CliTestResult result = RunCommand(
            handler,
            ["memory", "status", sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Session Entries", result.Output, StringComparison.Ordinal);

        Assert.Contains("Lexicon", result.Output, StringComparison.Ordinal);

        Assert.Contains("Workspace Index", result.Output, StringComparison.Ordinal);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/memory/status/{sessionId:D}", request.RequestUri!.AbsolutePath);
    }

    [Fact]

    public void Memory_search_defaults_to_all_and_clearly_displays_scope_provenance_and_retention()
    {
        MemorySearchResponse payload = new(
            "dark mode",
            MemorySearchScope.All,
            [
                new MemorySearchResultDto(
                    MemorySearchScope.Lexicon,
                    "Operator preferences",
                    "Prefers dark mode.",
                    "Lexicon entity: Operator",
                    "Durable until explicit entity deletion",
                    "operator"),
            ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "dark mode"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Scope: all", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Lexicon entity: Operator", result.Output, StringComparison.Ordinal);

        Assert.Contains("Durable until explicit entity deletion", result.Output, StringComparison.Ordinal);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        MemorySearchRequest? sent = JsonSerializer.Deserialize(
            ReadRequestBody(request),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.NotNull(sent);

        Assert.Equal(MemorySearchScope.All, sent.Scope);
    }

    [Fact]

    public void Memory_search_accepts_every_documented_scope_without_extra_enablement_switches()
    {
        foreach (string scope in new[] { "session", "attachments", "workspace", "saga", "lexicon", "all" })
        {
            RecordingHandler handler = new(_ => CreateResponse(
                new ApiResponse<MemorySearchResponse>(
                    new MemorySearchResponse("needle", Enum.Parse<MemorySearchScope>(scope, true), []),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

            CliTestResult result = RunCommand(
                handler,
                ["memory", "search", "needle", "--scope", scope]);

            Assert.Equal(0, result.ExitCode);

            Assert.Single(handler.Requests);
        }
    }

    [Fact]

    public void Memory_search_rejects_empty_query_without_calling_the_api()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["memory", "search", "   "]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]

    public void Memory_lexicon_delete_is_explicit_and_calls_item_scoped_endpoint_after_yes()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(
            handler,
            ["--yes", "memory", "lexicon", "delete", "Operator"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/memory/lexicon/Operator", request.RequestUri!.AbsolutePath);
    }

    [Fact]

    public void Memory_has_no_generic_delete_command()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["memory", "delete", "anything"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    private static byte[] ReadRequestBody(HttpRequestMessage request) =>
        request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();

    private static CliTestResult RunCommand(RecordingHandler handler, string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(json),
        };
    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {
                byte[] body = request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                snapshot.Content = new ByteArrayContent(body);
            }

            Requests.Add(snapshot);

            return Task.FromResult(
                responder is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : responder(request));
        }
    }
}
