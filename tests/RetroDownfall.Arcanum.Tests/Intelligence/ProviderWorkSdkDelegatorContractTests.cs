using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderWorkSdkDelegatorContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pinned_sdk_delegators_store_without_callbacks_and_only_forward_cleanup(bool embedding)
    {
        CallbackTrap inner = new();

        IDisposable wrapper = embedding ? new EmbeddingDelegator(inner) : new ChatDelegator(inner);

        Assert.Equal(0, inner.Calls);

        Assert.Equal(0, inner.Disposals);

        wrapper.Dispose();

        Assert.Equal(0, inner.Calls);

        Assert.Equal(1, inner.Disposals);
    }

    private sealed class ChatDelegator(IChatClient inner) : DelegatingChatClient(inner);

    private sealed class EmbeddingDelegator(IEmbeddingGenerator<string, Embedding<float>> inner)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner);

    private sealed class CallbackTrap : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
    {
        internal int Calls;

        internal int Disposals;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new InvalidOperationException("The SDK delegator constructor must not invoke its stored provider.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new InvalidOperationException("The SDK delegator constructor must not invoke its stored provider.");
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new InvalidOperationException("The SDK delegator constructor must not invoke its stored provider.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            Calls++;

            throw new InvalidOperationException("The SDK delegator constructor must not invoke stored metadata callbacks.");
        }

        public void Dispose() => Disposals++;
    }
}
