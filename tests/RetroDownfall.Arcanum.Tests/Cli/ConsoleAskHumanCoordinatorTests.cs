using System.Net;
using System.Net.Http.Headers;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class ConsoleAskHumanCoordinatorTests
{
    /// <summary>
    /// The non-interactive branch is exactly the redirected/<c>--json</c> case, so its submit-failure
    /// diagnostic must not join the answer on stdout (Arcanum.Command.Reference: diagnostics on stderr).
    /// </summary>
    [Fact]
    public async Task SubmitFailure_writes_the_diagnostic_to_stderr_not_the_answer_stream()
    {
        ArcanumApiClient client = CreateApiClient((_, _, _) => false);

        TestConsole payloadConsole = new();

        IAnsiConsole priorConsole = AnsiConsole.Console;

        TextWriter priorError = Console.Error;

        StringWriter capturedError = new();

        AnsiConsole.Console = payloadConsole;

        Console.SetError(capturedError);

        try
        {
            ConsoleAskHumanCoordinator coordinator = new(client, new FakePalette());

            AskHumanResult result = await coordinator.TryBeginAsync(
                AskHumanToolCall("call-1", "prompt-1", "Who?"),
                unattended: true,
                isInteractive: false,
                CancellationToken.None);

            Assert.Equal(AskHumanResult.SubmitFailed, result);

            Assert.True(
                string.IsNullOrEmpty(payloadConsole.Output),
                $"Expected no payload-stream output, got: {payloadConsole.Output}");

            Assert.Contains(
                "Failed to submit response to Daemon",
                capturedError.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            AnsiConsole.Console = priorConsole;

            Console.SetError(priorError);
        }
    }

    [Fact]
    public async Task StreamContinues_WhileInputPending_ThenSubmitOnAnswer()
    {
        TaskCompletionSource<string?> inputGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int submitCount = 0;
        ArcanumApiClient client = CreateApiClient((_, _, _) =>
        {
            submitCount++;
            return true;
        });

        ConsoleAskHumanCoordinator coordinator = new(
            client,
            new FakePalette(),
            CancellableRead(inputGate));

        IntelligenceEvent toolCall = AskHumanToolCall("call-1", "prompt-1", "Who?");
        AskHumanResult begin = await coordinator.TryBeginAsync(
            toolCall,
            unattended: false,
            isInteractive: true,
            CancellationToken.None);

        Assert.Equal(AskHumanResult.PendingInput, begin);
        Assert.True(coordinator.HasPending);

        // Simulate stream continuing while input pending.
        coordinator.ObserveStreamEvent(new IntelligenceEvent(IntelligenceEventType.Token, "chunk", "x"));
        Assert.True(coordinator.HasPending);

        inputGate.SetResult("operator-answer");
        AskHumanResult? settled = await coordinator.DrainAsync();
        Assert.Equal(AskHumanResult.Handled, settled);
        Assert.Equal(1, submitCount);
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public async Task ToolError_DismissesPending_WithoutSubmit()
    {
        TaskCompletionSource<string?> inputGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int submitCount = 0;
        ArcanumApiClient client = CreateApiClient((_, _, _) =>
        {
            submitCount++;
            return true;
        });

        ConsoleAskHumanCoordinator coordinator = new(
            client,
            new FakePalette(),
            CancellableRead(inputGate));

        Assert.Equal(
            AskHumanResult.PendingInput,
            await coordinator.TryBeginAsync(
                AskHumanToolCall("call-1", "prompt-1", "Who?"),
                unattended: false,
                isInteractive: true,
                CancellationToken.None));

        coordinator.ObserveStreamEvent(
            new IntelligenceEvent(
                IntelligenceEventType.ToolError,
                "ask_human",
                "failed",
                ToolCall: new IntelligenceToolCallEvent("call-1", "ask_human", "{}")));

        AskHumanResult? settled = await coordinator.DrainAsync();
        Assert.Equal(AskHumanResult.Handled, settled);
        Assert.Equal(0, submitCount);
    }

    [Fact]
    public async Task TurnCancel_PreventsAnswerSubmit()
    {
        TaskCompletionSource<string?> inputGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int submitCount = 0;
        ArcanumApiClient client = CreateApiClient((_, _, _) =>
        {
            submitCount++;
            return true;
        });

        using CancellationTokenSource cts = new();
        ConsoleAskHumanCoordinator coordinator = new(
            client,
            new FakePalette(),
            CancellableRead(inputGate));

        Assert.Equal(
            AskHumanResult.PendingInput,
            await coordinator.TryBeginAsync(
                AskHumanToolCall("call-1", "prompt-1", "Who?"),
                unattended: false,
                isInteractive: true,
                cts.Token));

        coordinator.Cancel();
        AskHumanResult? settled = await coordinator.DrainAsync();
        Assert.Equal(AskHumanResult.Handled, settled);
        Assert.Equal(0, submitCount);

        // Abandoned input completing later must not submit.
        inputGate.TrySetResult("abandoned");
        await Task.Yield();
        Assert.Equal(0, submitCount);
    }

    [Fact]
    public async Task SecondHitl_CannotAcquireConsoleInputConcurrently()
    {
        TaskCompletionSource<string?> inputGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArcanumApiClient client = CreateApiClient((_, _, _) => true);
        ConsoleAskHumanCoordinator coordinator = new(
            client,
            new FakePalette(),
            CancellableRead(inputGate));

        Assert.Equal(
            AskHumanResult.PendingInput,
            await coordinator.TryBeginAsync(
                AskHumanToolCall("call-1", "prompt-1", "First?"),
                unattended: false,
                isInteractive: true,
                CancellationToken.None));

        AskHumanResult second = await coordinator.TryBeginAsync(
            AskHumanToolCall("call-2", "prompt-2", "Second?"),
            unattended: false,
            isInteractive: true,
            CancellationToken.None);

        Assert.Equal(AskHumanResult.SubmitFailed, second);

        coordinator.Cancel();
        _ = await coordinator.DrainAsync();
    }

    [Fact]
    public async Task AbandonedInput_DoesNotConsumeAsNextAnswer_AfterDismiss()
    {
        TaskCompletionSource<string?> firstInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int submitCount = 0;
        string? submittedAnswer = null;
        ArcanumApiClient client = CreateApiClient((_, answer, _) =>
        {
            submitCount++;
            submittedAnswer = answer;
            return true;
        });

        ConsoleAskHumanCoordinator coordinator = new(
            client,
            new FakePalette(),
            CancellableRead(firstInput));

        Assert.Equal(
            AskHumanResult.PendingInput,
            await coordinator.TryBeginAsync(
                AskHumanToolCall("call-1", "prompt-1", "Q?"),
                unattended: false,
                isInteractive: true,
                CancellationToken.None));

        coordinator.ObserveStreamEvent(
            new IntelligenceEvent(IntelligenceEventType.Result, "done", null));

        AskHumanResult? settled = await coordinator.DrainAsync();
        Assert.Equal(AskHumanResult.Handled, settled);
        Assert.Equal(0, submitCount);
        Assert.Null(submittedAnswer);
    }

    private static Func<string, bool, CancellationToken, Task<string?>> CancellableRead(
        TaskCompletionSource<string?> gate) =>
        async (_, _, ct) =>
        {
            try
            {
                return await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        };

    private static IntelligenceEvent AskHumanToolCall(string callId, string promptId, string question) =>
        new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            null,
            ToolCall: new IntelligenceToolCallEvent(
                callId,
                "ask_human",
                $"{{\"question\":\"{question}\",\"promptId\":\"{promptId}\"}}"));

    private static ArcanumApiClient CreateApiClient(
        Func<string, string, CancellationToken, bool> submitValidator)
    {
        DelegatingHandler handler = new SubmitHandler(submitValidator);
        return new ArcanumApiClient(new HttpClientFactoryStub(handler), new SecretStoreStub());
    }

    private sealed class HttpClientFactoryStub(DelegatingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("http://localhost:5000/") };
    }

    private sealed class SecretStoreStub : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Guid.NewGuid().ToString("N"));

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Guid.NewGuid().ToString("N")));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class SubmitHandler(Func<string, string, CancellationToken, bool> submitValidator) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? promptId = null;
            string? answer = null;
            if (request.Content is not null)
            {
                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).Result;
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
                promptId = doc.RootElement.GetProperty("promptId").GetString();
                answer = doc.RootElement.GetProperty("answer").GetString();
            }

            bool allowed = submitValidator(promptId ?? string.Empty, answer ?? string.Empty, cancellationToken);
            HttpResponseMessage response = new(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(
                System.Text.Encoding.UTF8.GetBytes(
                    allowed
                        ? "{\"isSuccess\":true,\"data\":true}"
                        : "{\"isSuccess\":false,\"error\":{\"code\":\"Api.Error\",\"message\":\"failed\"}}"));
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return Task.FromResult(response);
        }
    }

    private sealed class FakePalette : IThemePalette
    {
        public Color Text { get; } = Color.White;
        public Color Heading { get; } = Color.White;
        public Color Highlight { get; } = Color.White;
        public Color Error { get; } = Color.Red;
        public Color Muted { get; } = Color.Grey;
    }
}
