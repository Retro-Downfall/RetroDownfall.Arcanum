using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class SessionLogBufferHistoryTests
{
    [Fact]
    public void ReplaceWithHistory_empty_shows_empty_state()
    {
        SessionLogBuffer buffer = new();
        buffer.Append(SessionLogEntryKind.User, "old");
        buffer.ReplaceWithHistory([], showOlderMessagesMarker: false);

        Assert.Contains(SessionLogBuffer.EmptySessionMessage, buffer.RenderPlainText(), StringComparison.Ordinal);
        Assert.DoesNotContain("old", buffer.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceWithHistory_older_marker_and_roles()
    {
        SessionLogBuffer buffer = new();
        buffer.ReplaceWithHistory(
            [
                (SessionLogEntryKind.User, "hi"),
                (SessionLogEntryKind.Assistant, "hello"),
            ],
            showOlderMessagesMarker: true);

        string text = buffer.RenderPlainText();
        Assert.Contains(SessionLogBuffer.OlderMessagesMarker, text, StringComparison.Ordinal);
        Assert.Contains("You:", text, StringComparison.Ordinal);
        Assert.Contains("Mage:", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user", nameof(SessionLogEntryKind.User))]
    [InlineData("assistant", nameof(SessionLogEntryKind.Assistant))]
    [InlineData("tool", nameof(SessionLogEntryKind.Tool))]
    [InlineData("system", nameof(SessionLogEntryKind.Status))]
    public void MapEntryRole_maps_known_roles(string role, string expected) =>
        Assert.Equal(Enum.Parse<SessionLogEntryKind>(expected), SessionLogBuffer.MapEntryRole(role));
}

public sealed class SessionWorkspaceServiceTests
{
    [Fact]
    public async Task Resume_success_replaces_transcript_chronologically()
    {
        Guid id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        DateTimeOffset t1 = DateTimeOffset.Parse("2026-07-19T10:00:00Z");
        DateTimeOffset t2 = DateTimeOffset.Parse("2026-07-19T10:01:00Z");

        // API returns newest-first.
        EntryDto[] apiEntries =
        [
            new(Guid.NewGuid(), id, "assistant", "second", null, null, t2),
            new(Guid.NewGuid(), id, "user", "first", null, null, t1),
        ];

        FakeSessionHttp handler = new(
            detail: new SessionDetailDto(id, null, "Demo", "Active", EntryCount: 2, t1, t2, null, 0),
            entries: apiEntries);
        SessionWorkspaceService workspace = CreateWorkspace(handler, out RecordingLastSessionStore store);
        CommandCenterState state = new(new SessionLogBuffer());
        state.Log.Append(SessionLogEntryKind.User, "prior-should-go");

        SessionResumeResult result = await workspace.ResumeSessionAsync(state, id, CancellationToken.None);

        Assert.Equal(SessionResumeOutcome.Success, result.Outcome);
        Assert.Equal(id, state.SessionId);
        Assert.Equal("Demo", state.SessionTitle);
        Assert.Equal(id, store.LastSaved);
        string text = state.Log.RenderPlainText();
        Assert.DoesNotContain("prior-should-go", text, StringComparison.Ordinal);
        int firstIdx = text.IndexOf("first", StringComparison.Ordinal);
        int secondIdx = text.IndexOf("second", StringComparison.Ordinal);
        Assert.True(firstIdx >= 0 && secondIdx > firstIdx);
    }

    [Fact]
    public async Task Resume_failure_preserves_prior_session_and_transcript()
    {
        Guid prior = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        FakeSessionHttp handler = new(failDetail: true);
        SessionWorkspaceService workspace = CreateWorkspace(handler, out _);
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(prior, "Prior", "Active", 1);
        state.Log.Append(SessionLogEntryKind.User, "keep-me");

        SessionResumeResult result = await workspace.ResumeSessionAsync(
            state,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            CancellationToken.None);

        Assert.Equal(SessionResumeOutcome.Failed, result.Outcome);
        Assert.Equal(prior, state.SessionId);
        Assert.Equal("Prior", state.SessionTitle);
        Assert.Contains("keep-me", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Contains("Error:", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Null(state.TransientStatus);
    }

    [Fact]
    public async Task Resume_with_entry_count_above_loaded_shows_older_marker()
    {
        Guid id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        EntryDto[] apiEntries =
        [
            new(Guid.NewGuid(), id, "user", "only", null, null, DateTimeOffset.UtcNow),
        ];
        FakeSessionHttp handler = new(
            detail: new SessionDetailDto(id, null, "Long", "Active", EntryCount: 500, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0),
            entries: apiEntries);
        SessionWorkspaceService workspace = CreateWorkspace(handler, out _);
        CommandCenterState state = new(new SessionLogBuffer());

        SessionResumeResult result = await workspace.ResumeSessionAsync(state, id, CancellationToken.None);

        Assert.Equal(SessionResumeOutcome.Success, result.Outcome);
        Assert.True(result.HadOlderMessages);
        Assert.Contains(SessionLogBuffer.OlderMessagesMarker, state.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_stale_falls_back_to_new_session_without_clearing_store()
    {
        RecordingLastSessionStore store = new() { LastId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee") };
        FakeSessionHttp handler = new(failDetail: true);
        ArcanumApiClient client = new(new FakeHttpClientFactory(handler), new FakeSecretStore());
        SessionWorkspaceService workspace = new(client, store, NullLogger<SessionWorkspaceService>.Instance);
        CommandCenterState state = new(new SessionLogBuffer());

        await workspace.RestoreStartupSessionAsync(state, CancellationToken.None);

        Assert.Null(state.SessionId);
        Assert.NotNull(store.LastId); // not cleared
        Assert.False(store.Cleared);
        Assert.Contains("New Session", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Contains("Last session unavailable", state.FooterHint ?? "", StringComparison.Ordinal);
    }

    private static SessionWorkspaceService CreateWorkspace(
        FakeSessionHttp handler,
        out RecordingLastSessionStore store)
    {
        store = new RecordingLastSessionStore();
        ArcanumApiClient client = new(new FakeHttpClientFactory(handler), new FakeSecretStore());
        return new SessionWorkspaceService(client, store, NullLogger<SessionWorkspaceService>.Instance);
    }

    private sealed class RecordingLastSessionStore : ILastSessionStore
    {
        public Guid? LastId { get; set; }

        public Guid? LastSaved { get; private set; }

        public bool Cleared { get; private set; }

        public Guid? GetLastSessionId() => LastId;

        public void SaveSessionId(Guid id)
        {
            LastSaved = id;
            LastId = id;
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:9") };
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeSessionHttp(
        SessionDetailDto? detail = null,
        EntryDto[]? entries = null,
        bool failDetail = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";

            if (path.Contains("/entries", StringComparison.Ordinal))
            {
                EntryDto[] payload = entries ?? [];
                ApiResponse<EntryDto[]> envelope = ApiResponse<EntryDto[]>.FromResult(
                    Result<EntryDto[]>.Success(payload));
                string json = JsonSerializer.Serialize(envelope, ArcanumJsonContext.Default.ApiResponseEntryDtoArray);
                return Task.FromResult(OkJson(json));
            }

            if (path.Contains("/sessions/", StringComparison.Ordinal) && !failDetail && detail is not null)
            {
                ApiResponse<SessionDetailDto> envelope = ApiResponse<SessionDetailDto>.FromResult(
                    Result<SessionDetailDto>.Success(detail));
                string json = JsonSerializer.Serialize(envelope, ArcanumJsonContext.Default.ApiResponseSessionDetailDto);
                return Task.FromResult(OkJson(json));
            }

            if (path.EndsWith("/sessions", StringComparison.Ordinal)
                || path.EndsWith("/sessions/", StringComparison.Ordinal))
            {
                ApiResponse<SessionQueryResult> envelope = ApiResponse<SessionQueryResult>.FromResult(
                    Result<SessionQueryResult>.Success(new SessionQueryResult([], null, false)));
                string json = JsonSerializer.Serialize(envelope, ArcanumJsonContext.Default.ApiResponseSessionQueryResult);
                return Task.FromResult(OkJson(json));
            }

            if (failDetail || path.Contains("/sessions/", StringComparison.Ordinal))
            {
                ApiResponse<SessionDetailDto> envelope = ApiResponse<SessionDetailDto>.FromResult(
                    Result<SessionDetailDto>.Failure(
                        new Error(ErrorCodes.Session.NotFound, "Session was not found.")));
                string json = JsonSerializer.Serialize(envelope, ArcanumJsonContext.Default.ApiResponseSessionDetailDto);
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    });
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"data":null,"isSuccess":false,"error":{"code":"x","message":"down"}}"""),
                });
        }

        private static HttpResponseMessage OkJson(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}