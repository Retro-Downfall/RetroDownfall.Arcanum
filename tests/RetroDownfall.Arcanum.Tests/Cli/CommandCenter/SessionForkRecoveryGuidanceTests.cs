using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class SessionForkRecoveryGuidanceTests
{
    [Theory]
    [InlineData("detail", false)]
    [InlineData("entries", false)]
    [InlineData("attachments", false)]
    [InlineData("persistence", false)]
    [InlineData("exception", false)]
    [InlineData("detail", true)]
    [InlineData("entries", true)]
    [InlineData("attachments", true)]
    [InlineData("persistence", true)]
    [InlineData("exception", true)]
    public async Task Failure_after_creation_preserves_the_branch_identity_and_recovery_action(string failure, bool listFails)
    {
        using Harness h = new(failure, listFails);
        SessionForkResult result = await h.Workspace.ForkSessionAsync(h.State, new(), CancellationToken.None);
        Assert.Equal(SessionForkOutcome.Failed, result.Outcome);
        Assert.Equal(Harness.Fork, result.ForkSessionId);
        AssertRecovery(result.ErrorMessage);
        Assert.Equal(result.ErrorMessage, h.State.LastError);
        Assert.Contains("original failure", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog failure", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Attachment copy failed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(Harness.Source, h.State.SessionId);
        Assert.Contains("source transcript", h.State.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Equal(1, h.Handler.ForkPosts);
        Assert.Equal(1, h.Handler.CatalogReads);
        if (!listFails) Assert.Contains(h.State.Sessions, row => row.Id == Harness.Fork);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("create")]
    public async Task Refusal_before_creation_makes_no_creation_claim(string failure)
    {
        using Harness h = new(failure, false);
        SessionForkResult result = await h.Workspace.ForkSessionAsync(h.State, new(), CancellationToken.None);
        Assert.Equal(SessionForkOutcome.Failed, result.Outcome);
        Assert.Null(result.ForkSessionId);
        Assert.DoesNotContain("was created", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, h.Handler.CatalogReads);
        Assert.Equal(Harness.Source, h.State.SessionId);
    }

    [Theory]
    [InlineData("cancel", 0)]
    [InlineData("cancel-catalog", 1)]
    public async Task Cancellation_after_creation_retains_guidance_and_starts_no_uncancelled_refresh(string failure, int catalogReads)
    {
        using Harness h = new(failure, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Workspace.ForkSessionAsync(h.State, new(), h.Cancellation.Token));
        AssertRecovery(h.State.LastError);
        AssertRecovery(h.State.Log.RenderPlainText());
        Assert.Equal(Harness.Source, h.State.SessionId);
        Assert.Equal(1, h.Handler.ForkPosts);
        Assert.Equal(catalogReads, h.Handler.CatalogReads);
        Assert.Null(h.State.TransientStatus);
    }

    private static void AssertRecovery(string? message)
    {
        Assert.Contains("was created", message, StringComparison.Ordinal);
        Assert.Contains($"/resume {Harness.Fork:D}", message, StringComparison.Ordinal);
    }

    private sealed class Harness : IDisposable
    {
        internal static readonly Guid Source = Guid.Parse("11111111-1111-4111-8111-111111111111");
        internal static readonly Guid Fork = Guid.Parse("22222222-2222-4222-8222-222222222222");
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Handler Handler { get; }
        internal CommandCenterState State { get; } = new(new SessionLogBuffer());
        internal SessionWorkspaceService Workspace { get; }

        internal Harness(string failure, bool listFails)
        {
            Handler = new(failure == "cancel-catalog" ? "detail" : failure, listFails);
            if (failure == "cancel-catalog") Handler.BeforeCatalog = Cancellation.Cancel;
            ArcanumApiClient client = new(new Factory(Handler), ArcanumApiCredentialLeaseTestFactory.Create("test-key"));
            Workspace = new(client, new Store(failure, Cancellation), NullLogger<SessionWorkspaceService>.Instance);
            State.ApplySessionMeta(Source, "Source", "Active", 1);
            State.Log.Append(SessionLogEntryKind.User, "source transcript");
        }

        public void Dispose()
        {
            Handler.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class Store(string failure, CancellationTokenSource cancellation) : ILastSessionStore
    {
        public Guid? GetLastSessionId() => Harness.Source;

        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync, CancellationToken cancellationToken)
        {
            if (failure == "cancel") cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure == "exception") throw new IOException("original failure");
            return Task.FromResult(failure == "persistence"
                ? ArcanumClientMutationResult<CliContextDocument>.Blocked(new("Data.ResetInProgress", "original failure"))
                : ArcanumClientMutationResult<CliContextDocument>.Completed(CliContextDocument.Empty with { SessionId = id }));
        }
    }

    private sealed class Handler(string failure, bool listFails) : HttpMessageHandler
    {
        internal int ForkPosts { get; private set; }
        internal int CatalogReads { get; private set; }
        internal Action? BeforeCatalog { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            SessionDetailDto detail = new(Harness.Fork, null, "Fork", "Active", 0, now, now, null, 0, Harness.Source);
            if (path.EndsWith("/fork/preview", StringComparison.Ordinal))
                return Reply(new SessionForkPreviewDto(0, 0, "empty"), ArcanumJsonContext.Default.ApiResponseSessionForkPreviewDto, failure == "preview");
            if (request.Method == HttpMethod.Post && path.EndsWith("/fork", StringComparison.Ordinal))
            {
                ForkPosts++;
                return Reply(detail, ArcanumJsonContext.Default.ApiResponseSessionDetailDto, failure == "create", HttpStatusCode.Created);
            }

            if (path.EndsWith("/entries", StringComparison.Ordinal))
                return Reply(Array.Empty<EntryDto>(), ArcanumJsonContext.Default.ApiResponseEntryDtoArray, failure == "entries");
            if (path.EndsWith("/attachments", StringComparison.Ordinal))
                return Reply(Array.Empty<SessionAttachmentDto>(), ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray,
                    failure == "attachments", message: "original failure loading attachment metadata");
            if (path == "/api/sessions")
            {
                CatalogReads++;
                BeforeCatalog?.Invoke();
                token.ThrowIfCancellationRequested();
                SessionSummaryDto summary = new(Harness.Fork, null, "Fork", "Active", 0, now, now, Harness.Source);
                return Reply(new SessionQueryResult([summary], null, false), ArcanumJsonContext.Default.ApiResponseSessionQueryResult, listFails, message: "catalog failure");
            }
            Assert.Equal($"/api/sessions/{Harness.Fork:D}", path);
            return Reply(detail, ArcanumJsonContext.Default.ApiResponseSessionDetailDto, failure == "detail");
        }

        private static Task<HttpResponseMessage> Reply<T>(T value, JsonTypeInfo<ApiResponse<T>> info, bool fails,
            HttpStatusCode status = HttpStatusCode.OK, string message = "original failure") =>
            Task.FromResult(new HttpResponseMessage(fails ? HttpStatusCode.ServiceUnavailable : status)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(ApiResponse<T>.FromResult(
                    fails ? Result<T>.Failure(new("Test.Failure", message)) : Result<T>.Success(value)), info)),
            });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("http://localhost:5001/") };
    }
}
