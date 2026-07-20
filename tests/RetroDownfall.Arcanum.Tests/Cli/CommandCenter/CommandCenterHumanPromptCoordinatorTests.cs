using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterHumanPromptCoordinatorTests
{
    [Fact]
    public void BeginPrompt_SetsPending_And_MatchesCallIdAndPromptId()
    {
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ => OkTrue());
        HumanPromptRequest? shown = null;
        coordinator.SetUiCallbacks(
            onShow: (req, _) => shown = req,
            onHide: (_, _) => { },
            onStatus: _ => { });

        coordinator.BeginPrompt(new HumanPromptRequest("call-1", "prompt-1", "What is your name?"));

        Assert.NotNull(shown);
        Assert.Equal("call-1", shown!.CallId);
        Assert.Equal("prompt-1", shown.PromptId);
        Assert.True(coordinator.IsActive);
        Assert.True(coordinator.Matches("call-1", "prompt-1"));
        Assert.False(coordinator.Matches("call-1", "other"));
        Assert.False(coordinator.Matches("other", "prompt-1"));
        Assert.True(coordinator.MatchesCallId("call-1"));
        Assert.False(coordinator.MatchesCallId("other"));
    }

    [Fact]
    public async Task SubmitAnswerAsync_Accepted_Closes()
    {
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ => OkTrue());
        bool hidden = false;
        HumanPromptCloseReason? reason = null;
        coordinator.SetUiCallbacks(
            onShow: (_, _) => { },
            onHide: (r, _) =>
            {
                hidden = true;
                reason = r;
            },
            onStatus: _ => { });

        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));
        HumanPromptSubmitOutcome outcome = await coordinator.SubmitAnswerAsync("yes", CancellationToken.None);

        Assert.Equal(HumanPromptSubmitOutcome.Accepted, outcome);
        Assert.True(hidden);
        Assert.Equal(HumanPromptCloseReason.Submitted, reason);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task SubmitAnswerAsync_NotFound_ExpiresAndCloses()
    {
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ => NotFound());
        string? notice = null;
        coordinator.SetUiCallbacks(
            onShow: (_, _) => { },
            onHide: (_, n) => notice = n,
            onStatus: _ => { });

        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));
        HumanPromptSubmitOutcome outcome = await coordinator.SubmitAnswerAsync("yes", CancellationToken.None);

        Assert.Equal(HumanPromptSubmitOutcome.NotFound, outcome);
        Assert.False(coordinator.IsActive);
        Assert.Contains("expired", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAnswerAsync_TransientFailure_StaysActive_AllowsRetry()
    {
        int calls = 0;
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ =>
        {
            calls++;
            return calls == 1 ? HttpError() : OkTrue();
        });

        string? status = null;
        coordinator.SetUiCallbacks(
            onShow: (_, _) => { },
            onHide: (_, _) => { },
            onStatus: s => status = s);

        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));
        HumanPromptSubmitOutcome first = await coordinator.SubmitAnswerAsync("yes", CancellationToken.None);
        Assert.Equal(HumanPromptSubmitOutcome.TransientFailure, first);
        Assert.True(coordinator.IsActive);
        Assert.False(string.IsNullOrWhiteSpace(status));

        HumanPromptSubmitOutcome second = await coordinator.SubmitAnswerAsync("yes", CancellationToken.None);
        Assert.Equal(HumanPromptSubmitOutcome.Accepted, second);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task SubmitAnswerAsync_Empty_Rejected()
    {
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ => OkTrue());
        coordinator.SetUiCallbacks((_, _) => { }, (_, _) => { }, _ => { });
        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));

        HumanPromptSubmitOutcome outcome = await coordinator.SubmitAnswerAsync("   ", CancellationToken.None);
        Assert.Equal(HumanPromptSubmitOutcome.RejectedEmpty, outcome);
        Assert.True(coordinator.IsActive);
    }

    [Fact]
    public async Task SubmitAnswerAsync_InFlight_RejectsDuplicate()
    {
        TaskCompletionSource<HttpResponseMessage> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandCenterHumanPromptCoordinator coordinator = CreateAsyncCoordinator(_ => gate.Task);
        coordinator.SetUiCallbacks((_, _) => { }, (_, _) => { }, _ => { });
        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));

        Task<HumanPromptSubmitOutcome> first = coordinator.SubmitAnswerAsync("yes", CancellationToken.None);
        await Task.Yield();
        HumanPromptSubmitOutcome duplicate = await coordinator.SubmitAnswerAsync("no", CancellationToken.None);
        Assert.Equal(HumanPromptSubmitOutcome.AlreadyInFlight, duplicate);

        gate.SetResult(OkTrue());
        Assert.Equal(HumanPromptSubmitOutcome.Accepted, await first);
    }

    [Fact]
    public void TryClose_ClearsPending()
    {
        CommandCenterHumanPromptCoordinator coordinator = CreateCoordinator(_ => OkTrue());
        coordinator.SetUiCallbacks((_, _) => { }, (_, _) => { }, _ => { });
        coordinator.BeginPrompt(new HumanPromptRequest("c1", "p1", "Q?"));
        Assert.True(coordinator.TryClose(HumanPromptCloseReason.Cancelled));
        Assert.False(coordinator.IsActive);
        Assert.False(coordinator.TryClose(HumanPromptCloseReason.Expired));
    }

    [Fact]
    public void IsTimeoutText_DetectsLockedMessage()
    {
        Assert.True(CommandCenterHumanPromptCoordinator.IsTimeoutText(
            "No operator response was received before the human prompt timed out."));
        Assert.False(CommandCenterHumanPromptCoordinator.IsTimeoutText("ok"));
    }

    private static CommandCenterHumanPromptCoordinator CreateCoordinator(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArcanumApiClient client = new(
            new Factory(new SyncHandler(respond)),
            new FakeSecretStore());
        return new CommandCenterHumanPromptCoordinator(client);
    }

    private static CommandCenterHumanPromptCoordinator CreateAsyncCoordinator(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        ArcanumApiClient client = new(
            new Factory(new AsyncHandler(respond)),
            new FakeSecretStore());
        return new CommandCenterHumanPromptCoordinator(client);
    }

    private static HttpResponseMessage OkTrue()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean);
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static HttpResponseMessage NotFound()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<bool>(
                false,
                false,
                new Error(ErrorCodes.Intelligence.HumanPromptNotFound, "gone")),
            ArcanumJsonContext.Default.ApiResponseBoolean);
        HttpResponseMessage response = new(HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static HttpResponseMessage HttpError()
    {
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"success":false,"error":{"code":"Api.HttpError","message":"down"}}""",
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:9") };
    }

    private sealed class SyncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request);
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
}
