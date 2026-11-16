using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderWorkMetadataLookupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduling_metadata_lookup_does_not_invoke_an_arbitrary_client_callback(bool streaming)
    {
        ProviderSettings provider = new() { Name = "fixture", Models = ["fixture-model"] };

        ArcanumSettings settings = new() { Providers = [provider], DefaultModel = "fixture-model" };

        using MetadataTrap client = new();

        ModelCallExecutor executor = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            new TestOptionsMonitor<ArcanumSettings>(settings));

        ModelCallContext context = new(provider, "fixture-model", 0, 0);

        try
        {
            if (streaming)
            {
                await foreach (var _ in executor.ExecuteStreamingAsync(client, [new(ChatRole.User, "A request.")], new(),
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context)) { }
            }
            else await executor.ExecuteBufferedAsync(client, [new(ChatRole.User, "A request.")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context);
        }
        catch (InvalidOperationException) { }

        Assert.Equal(0, client.MetadataCalls);

        Assert.Equal(1, client.ProviderCalls);
    }

    private sealed class MetadataTrap : IChatClient
    {
        internal int MetadataCalls;

        internal int ProviderCalls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ProviderCalls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProviderCalls++;

            await Task.CompletedTask;

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            MetadataCalls++;

            throw new InvalidOperationException("A custom metadata callback must not run before provider admission.");
        }

        public void Dispose() { }
    }
}
