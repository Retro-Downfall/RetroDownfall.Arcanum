using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelCallExecutorTests
{

    [Fact]
    public async Task ExecuteBufferedAsync_ReturnsResponse_AndConsumesBudget()
    {
        ScriptingChatClient chat = new(text: "pong");

        TurnBudget budget = new(maxModelCalls: 2);

        ModelCallExecutor executor = new();

        Result<ModelCallResult> result = await executor.ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            budget,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("pong", result.Value.Response.Text);

        Assert.Equal(1, budget.RemainingModelCalls);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_FailsWhenBudgetExhausted()
    {
        ScriptingChatClient chat = new(text: "pong");

        TurnBudget budget = new(maxModelCalls: 1);

        ModelCallExecutor executor = new();

        Assert.True(budget.TryConsumeModelCall());

        Result<ModelCallResult> result = await executor.ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            budget,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.TurnBudgetExceeded, result.Error.Code);

        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_YieldsTextDeltas()
    {
        ScriptingChatClient chat = new(text: "ab");

        TurnBudget budget = new(maxModelCalls: 1);

        ModelCallExecutor executor = new();

        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            budget,
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Contains(updates, u => u is ModelCallTextDelta);

        Assert.Equal(0, budget.RemainingModelCalls);
    }

    private sealed class ScriptingChatClient(string text) : IChatClient
    {

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;

            foreach (char c in text)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, c.ToString());

                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

    }

}
