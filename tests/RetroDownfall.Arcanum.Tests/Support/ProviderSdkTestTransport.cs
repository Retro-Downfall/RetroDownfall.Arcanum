using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;

using Microsoft.Extensions.AI;

using OpenAI;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Support;

// Deliberate retry stress belongs in tests. Actual default-factory controls separately prove
// zero retries and installed physical policies. No production option can enable this path.
internal static class ProviderSdkTestTransport
{
    internal static ChatClientLease RetryChat(ChatClientLease captured, HttpMessageHandler handler,
        ITapestryProviderValidator? validator = null)
    {
        HttpClient http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            OpenAIClientOptions options = Options(captured.Provider, http);

            options.AddPolicy(new OutputTokenLimitWirePolicy(captured.Provider.OutputTokenLimitWireDialect), PipelinePosition.PerCall);

            options.AddPolicy(new TapestryProviderPolicy(validator, captured.Provider.Name, captured.ResolvedModel, embedding: false), PipelinePosition.BeforeTransport);

            options.AddPolicy(new TapestryRetrievalDispatchPolicy(captured.Provider.Name, captured.ResolvedModel), PipelinePosition.BeforeTransport);

            PromptCacheClientIdentity? identity = captured.PromptCacheIdentity;

            if (identity?.DispatchPolicy is { } dispatch) options.AddPolicy(dispatch, PipelinePosition.BeforeTransport);

            OpenAI.Chat.ChatClient sdk = new(captured.ResolvedModel, new ApiKeyCredential("synthetic-retry-fixture-key"), options);

            IChatClient client = sdk.AsIChatClient();

            PromptCacheClientIdentity? bound = identity is null ? null : new(client, captured.Provider, captured.ResolvedModel,
                identity.ConfigurationStamp, identity.CredentialStamp, identity.DispatchPolicy);

            return new(client, captured.Provider, captured.ResolvedModel, http, bound);
        }
        catch
        {
            http.Dispose();

            throw;
        }
    }

    internal static (EmbeddingGeneratorLease Lease, HttpClient Transport) RetryEmbedding(
        EmbeddingGeneratorLease captured, ProviderSettings provider, HttpMessageHandler handler, ITapestryProviderValidator? validator)
    {
        EmbeddingProvenance identity = Assert.IsType<EmbeddingProvenance>(captured.Provenance);

        Assert.Equal(provider.Name, identity.Provider);

        HttpClient http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            OpenAIClientOptions options = Options(provider, http);

            options.AddPolicy(new TapestryProviderPolicy(validator, identity.Provider, identity.Model, embedding: true), PipelinePosition.BeforeTransport);

            OpenAI.Embeddings.EmbeddingClient sdk = new(identity.Model, new ApiKeyCredential("synthetic-retry-fixture-key"), options);

            return (new(sdk.AsIEmbeddingGenerator(), ownsGenerator: true, identity), http);
        }
        catch
        {
            http.Dispose();

            throw;
        }
    }

    private static OpenAIClientOptions Options(ProviderSettings provider, HttpClient http) => new()
    {
        Endpoint = new Uri(provider.Endpoint),
        Transport = new HttpClientPipelineTransport(http),
        NetworkTimeout = Timeout.InfiniteTimeSpan,
        RetryPolicy = new ClientRetryPolicy(1),
    };

    // Real HTTP response streams do not seek. The pinned SDK attempts to rebuffer consumed
    // StringContent streams on infinite-timeout disposal. Match real transport shape while
    // retaining byte-identical bodies, media type, charset and length, including error bodies.
    internal static HttpContent Content(string text, string mediaType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        StreamContent content = new(new WireStream(bytes));

        content.Headers.ContentType = new(mediaType) { CharSet = "utf-8" };

        content.Headers.ContentLength = bytes.Length;

        return content;
    }

    private sealed class WireStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => _inner.ReadAsync(buffer, token);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
