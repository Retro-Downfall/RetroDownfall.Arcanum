using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;

using Microsoft.Extensions.AI;

using OpenAI;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class PromptCacheSdkRetryTests
{
    // These are deliberately retry-enabled SDK stress tests, separate from the actual default
    // factory's zero-retry controls. Reuse its exact captured policy and binding stamps; no
    // production option or hook can turn retries on.
    internal sealed class RetryingSdkTestFactory(ChatClientFactory production, HttpMessageHandler handler)
    {
        internal async Task<ChatClientLease> ResolveClientAsync(string? model, CancellationToken cancellationToken)
        {
            using ChatClientLease captured = await production.ResolveClientAsync(model, cancellationToken);

            PromptCacheClientIdentity identity = Assert.IsType<PromptCacheClientIdentity>(captured.PromptCacheIdentity);

            PromptCacheDispatchPolicy dispatch = Assert.IsType<PromptCacheDispatchPolicy>(identity.DispatchPolicy);

            return ProviderSdkTestTransport.RetryChat(captured, handler);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_default_factory_installs_the_dispatch_policy_and_does_not_leak_its_scope(bool streaming)
    {
        using Fixture f = new();

        using ChatClientLease lease = await f.ProductionFactory.ResolveClientAsync(null, CancellationToken.None);

        PromptCacheDispatchPolicy policy = Assert.IsType<PromptCacheDispatchPolicy>(lease.PromptCacheIdentity!.DispatchPolicy);

        async Task Send()
        {
            if (streaming)
            {
                await foreach (ChatResponseUpdate _ in lease.ChatClient.GetStreamingResponseAsync([new(ChatRole.User, "synthetic request")])) { }
            }
            else
            {
                _ = await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "synthetic request")]);
            }
        }

        using (PromptCacheDispatchPolicy.Scope scope = policy.Enter(() =>
            ValueTask.FromResult<(Error, ModelCallRetryDirective)?>(
                (new(ErrorCodes.Covenant.IntegrityFailure, "synthetic refusal"), ModelCallRetryDirective.None))))
        {
            Exception? actual = await Record.ExceptionAsync(Send);

            Assert.NotNull(actual);

            Assert.NotNull(scope.Refusal);

            Assert.True(ReferenceEquals(actual, scope.Refusal)
                || actual is AggregateException aggregate && scope.IsTerminalRefusal(aggregate));

            Assert.Equal(0, f.Handler.Sends);
        }

        // The first real transport result is the fixture's503; actual defaults must not retry it.
        _ = await Assert.ThrowsAnyAsync<Exception>(Send);

        Assert.Equal(1, f.Handler.Sends);

        await Send();

        Assert.Equal(2, f.Handler.Sends);
    }

    [Fact]
    public async Task Retry_fixture_sse_retains_identical_bytes_and_headers_on_a_network_shaped_stream()
    {
        const string text = "data: synthetic\n\ndata: [DONE]\n\n";

        using HttpContent content = ProviderSdkTestTransport.Content(text, "text/event-stream");

        using Stream stream = await content.ReadAsStreamAsync();

        Assert.False(stream.CanSeek);

        Assert.True(stream.CanRead);

        using MemoryStream bytes = new();

        await stream.CopyToAsync(bytes);

        Assert.Equal(Encoding.UTF8.GetBytes(text), bytes.ToArray());

        Assert.Equal("text/event-stream", content.Headers.ContentType!.MediaType);

        Assert.Equal("utf-8", content.Headers.ContentType.CharSet);

        Assert.Equal(bytes.Length, content.Headers.ContentLength);
    }
}
