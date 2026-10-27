using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestExecutorFenceRefusalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Only_coded_before_send_errors_preserve_their_code_and_neither_arm_calls_the_SDK(bool embedding, bool coded)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embedding ? LongRestProviderPayloadKind.Embedding : LongRestProviderPayloadKind.Summary);

        ModelCallExecutor executor = new(f.Estimator, f.Options, f.Log);

        Error error = new(ErrorCodes.LongRest.StaleInput, "private-fence-reason");

        int fences = 0;

        int paid = 0;

        Task Before(CancellationToken token)
        {
            fences++;

            throw coded ? new LongRestFenceRefusal(error) : new InvalidOperationException("LongRest.StaleInput private-fence-reason");
        }

        Task Record(LongRestProviderUsage _) { paid++; return Task.CompletedTask; }

        LongRestBeforeSendRefusal refusal;

        if (embedding)
        {
            LongRestProviderSelection selected = f.Epoch.Capture().ResolveEmbedding().Value;

            using EmbeddingGeneratorLease lease = (await f.ClientFactory.ResolveEmbeddingAsync(selected, CancellationToken.None)).Value;

            refusal = await Assert.ThrowsAsync<LongRestBeforeSendRefusal>(() => executor.ExecuteLongRestEmbeddingAsync(lease.Generator,
                f.Payload, UnrestrictedTurnBudget.Instance, Before, Record, f.Admission(), CancellationToken.None));
        }
        else
        {
            LongRestProviderSelection selected = f.Epoch.Capture().ResolveChat().Value;

            using ChatClientLease lease = (await f.ClientFactory.ResolveChatAsync(selected, CancellationToken.None)).Value;

            refusal = await Assert.ThrowsAsync<LongRestBeforeSendRefusal>(() => executor.ExecuteLongRestChatAsync(lease.ChatClient,
                f.Payload, UnrestrictedTurnBudget.Instance, Before, Record, f.Admission(), CancellationToken.None));
        }

        Assert.Equal(coded ? ErrorCodes.LongRest.StaleInput : ErrorCodes.Hub.Error, refusal.Error.Code);

        if (coded) Assert.Equal(error, refusal.Error);

        Assert.Equal(1, fences);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0, paid);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.DoesNotContain(f.Log.Messages, message => message.Contains("private-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SDK_marker_type_never_receives_before_send_provenance(bool embedding)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embedding ? LongRestProviderPayloadKind.Embedding : LongRestProviderPayloadKind.Summary);

        LongRestBeforeSendRefusal forged = new(new(ErrorCodes.LongRest.StaleInput, "private-sdk-body"), "private-sdk-type");

        int calls = 0;

        int paid = 0;

        ThrowingBoundary client = new(forged, () => calls++);

        ModelCallExecutor executor = new(f.Estimator, f.Options, f.Log);

        Task Record(LongRestProviderUsage _) { paid++; return Task.CompletedTask; }

        Result<LongRestProviderResult> result = embedding
            ? await executor.ExecuteLongRestEmbeddingAsync(client, f.Payload, UnrestrictedTurnBudget.Instance, _ => Task.CompletedTask, Record, f.Admission(), CancellationToken.None)
            : await executor.ExecuteLongRestChatAsync(client, f.Payload, UnrestrictedTurnBudget.Instance, _ => Task.CompletedTask, Record, f.Admission(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, result.Error.Code);

        Assert.Equal(1, calls);

        Assert.Equal(0, paid);

        Assert.DoesNotContain(f.Log.Messages, message => message.Contains("private-", StringComparison.Ordinal));
    }

    private sealed class ThrowingBoundary(Exception failure, Action sent) : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            sent();

            throw failure;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            sent();

            throw failure;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
