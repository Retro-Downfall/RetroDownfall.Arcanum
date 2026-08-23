using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class SessionCrossGenerationWriterTests
{

    private static readonly Guid PriorSessionId =
        Guid.Parse("61616161-6161-6161-6161-616161616161");

    private static readonly Guid RemoteSessionId =
        Guid.Parse("62626262-6262-6262-6262-626262626262");

    [Fact]
    public async Task Resume_does_not_persist_or_switch_to_a_session_missing_at_client_admission()
    {

        MutableSessionHandler host = new(RemoteSessionId);

        (SessionWorkspaceService workspace, FakeContextStore store, RecordingArcanumClientMutationBoundary boundary) =
            CreateWorkspace(host);

        CommandCenterState state = PriorState();

        SessionResumeResult result = await workspace.ResumeSessionAsync(
            state,
            RemoteSessionId,
            CancellationToken.None);

        Assert.Equal(SessionResumeOutcome.Failed, result.Outcome);

        Assert.Equal(PriorSessionId, state.SessionId);

        Assert.Contains("retain prior transcript", state.Log.RenderPlainText(), StringComparison.Ordinal);

        Assert.Equal(PriorSessionId, store.Load().SessionId);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Equal(2, host.RemoteDetailRequests);

    }

    [Fact]
    public async Task Fork_does_not_persist_or_switch_to_a_branch_missing_at_client_admission()
    {

        MutableSessionHandler host = new(RemoteSessionId)
        {
            ForkSourceId = PriorSessionId,
        };

        (SessionWorkspaceService workspace, FakeContextStore store, RecordingArcanumClientMutationBoundary boundary) =
            CreateWorkspace(host);

        CommandCenterState state = PriorState();

        SessionForkResult result = await workspace.ForkSessionAsync(
            state,
            new ForkSessionRequest(),
            CancellationToken.None);

        Assert.Equal(SessionForkOutcome.Failed, result.Outcome);

        Assert.Equal(PriorSessionId, state.SessionId);

        Assert.Contains("retain prior transcript", state.Log.RenderPlainText(), StringComparison.Ordinal);

        Assert.Equal(PriorSessionId, store.Load().SessionId);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Equal(2, host.RemoteDetailRequests);

    }

    [Fact]
    public async Task Bound_session_does_not_persist_or_switch_when_missing_at_client_admission()
    {

        MutableSessionHandler host = new(RemoteSessionId)
        {
            Available = false,
        };

        (SessionWorkspaceService workspace, FakeContextStore store, RecordingArcanumClientMutationBoundary boundary) =
            CreateWorkspace(host, disableOnAdmission: false);

        CommandCenterState state = PriorState();

        bool persisted = await workspace.PersistBoundSessionAsync(
            state,
            RemoteSessionId,
            CancellationToken.None);

        Assert.False(persisted);

        Assert.Equal(PriorSessionId, state.SessionId);

        Assert.Equal(PriorSessionId, store.Load().SessionId);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Equal(1, host.RemoteDetailRequests);

    }

    private static (
        SessionWorkspaceService Workspace,
        FakeContextStore Store,
        RecordingArcanumClientMutationBoundary Boundary) CreateWorkspace(
            MutableSessionHandler host,
            bool disableOnAdmission = true)
    {

        FakeContextStore store = new(
            CliContextDocument.Empty with { SessionId = PriorSessionId });

        RecordingArcanumClientMutationBoundary boundary = new();

        if (disableOnAdmission)
        {

            boundary.BeforeMutation = () => host.Available = false;

        }

        CliSessionManager manager = new(
            new ConsoleDispatcher(new CliInvocationContext()),
            NullLogger<CliSessionManager>.Instance,
            store,
            boundary);

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(host),
            new FakeSecretStore());

        SessionWorkspaceService workspace = new(
            client,
            new CliLastSessionStore(manager),
            NullLogger<SessionWorkspaceService>.Instance);

        return (workspace, store, boundary);

    }

    private static CommandCenterState PriorState()
    {

        CommandCenterState state = new(new SessionLogBuffer());

        state.ApplySessionMeta(PriorSessionId, "Prior", "Active", 1);

        state.Log.Append(SessionLogEntryKind.User, "retain prior transcript");

        return state;

    }

    private sealed class FakeContextStore(
        CliContextDocument document) :
        ICliContextStore,
        ICliContextExclusiveWriter
    {

        private CliContextDocument _document = document;

        internal int ExclusiveSaves { get; private set; }

        public string FilePath => "/tmp/session-generation-context.json";

        public CliContextDocument Load() => _document;

        public void SaveUnderExclusive(CliContextDocument document)
        {

            ExclusiveSaves++;

            _document = document;

        }

    }

    private sealed class MutableSessionHandler(
        Guid remoteSessionId) : HttpMessageHandler
    {

        internal bool Available { get; set; } = true;

        internal Guid? ForkSourceId { get; init; }

        internal int RemoteDetailRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post
                && path == $"/api/sessions/{PriorSessionId:D}/fork")
            {

                return Task.FromResult(
                    SessionResponse(
                        Result<SessionDetailDto>.Success(Detail()),
                        HttpStatusCode.Created));

            }

            if (path.EndsWith("/attachments", StringComparison.Ordinal))
            {

                ApiResponse<SessionAttachmentDto[]> envelope =
                    ApiResponse<SessionAttachmentDto[]>.FromResult(
                        Result<SessionAttachmentDto[]>.Success([]));

                return Task.FromResult(
                    Json(
                        JsonSerializer.Serialize(
                            envelope,
                            ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray)));

            }

            if (path.EndsWith("/entries", StringComparison.Ordinal))
            {

                ApiResponse<EntryDto[]> envelope =
                    ApiResponse<EntryDto[]>.FromResult(
                        Result<EntryDto[]>.Success([]));

                return Task.FromResult(
                    Json(
                        JsonSerializer.Serialize(
                            envelope,
                            ArcanumJsonContext.Default.ApiResponseEntryDtoArray)));

            }

            if (path == $"/api/sessions/{remoteSessionId:D}")
            {

                RemoteDetailRequests++;

                Result<SessionDetailDto> result = Available
                    ? Result<SessionDetailDto>.Success(Detail())
                    : Result<SessionDetailDto>.Failure(
                        new Error(
                            ErrorCodes.Session.NotFound,
                            "Session was not found in the replacement host."));

                return Task.FromResult(
                    SessionResponse(
                        result,
                        Available ? HttpStatusCode.OK : HttpStatusCode.NotFound));

            }

            if (path is "/api/sessions" or "/api/sessions/")
            {

                ApiResponse<SessionQueryResult> envelope =
                    ApiResponse<SessionQueryResult>.FromResult(
                        Result<SessionQueryResult>.Success(
                            new SessionQueryResult([], null, false)));

                return Task.FromResult(
                    Json(
                        JsonSerializer.Serialize(
                            envelope,
                            ArcanumJsonContext.Default.ApiResponseSessionQueryResult)));

            }

            throw new InvalidOperationException($"Unexpected request to {path}.");

        }

        private SessionDetailDto Detail() =>
            new(
                remoteSessionId,
                null,
                "Remote",
                "Active",
                0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null,
                0,
                ForkSourceId);

        private static HttpResponseMessage SessionResponse(
            Result<SessionDetailDto> result,
            HttpStatusCode status) =>
            Json(
                JsonSerializer.Serialize(
                    ApiResponse<SessionDetailDto>.FromResult(result),
                    ArcanumJsonContext.Default.ApiResponseSessionDetailDto),
                status);

        private static HttpResponseMessage Json(
            string payload,
            HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(
            string encryptionSecret) => Task.CompletedTask;

    }

}
