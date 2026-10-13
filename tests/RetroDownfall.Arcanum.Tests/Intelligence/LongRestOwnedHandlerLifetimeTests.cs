using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// Post-signature-change only: uses the internal owned-handler constructor.
public sealed class LongRestOwnedHandlerLifetimeTests
{
    [Fact]
    public async Task Adapter_lease_disposal_leaves_the_scope_pool_usable_and_owner_disposes_it_once()
    {
        CountingHandler handler = new();

        using LongRestProviderClientFactory factory = new(new Keys(), handler);

        using (ChatClientLease chat = (await factory.ResolveChatAsync(Selection(false), CancellationToken.None)).Value)
        {
            ChatResponse response = await chat.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")]);

            Assert.Equal("ok", response.Text);
        }

        Assert.Equal(1, handler.Sends);

        Assert.Equal(0, handler.Disposals);

        using (EmbeddingGeneratorLease embedding = (await factory.ResolveEmbeddingAsync(Selection(true), CancellationToken.None)).Value)
        {
            var response = await embedding.Generator.GenerateAsync(["probe"], new EmbeddingGenerationOptions { Dimensions = 64 });

            Assert.Single(response);

            Assert.Equal(64, response[0].Vector.Length);
        }

        Assert.Equal(2, handler.Sends);

        Assert.Equal(0, handler.Disposals);

        using (ChatClientLease chat = (await factory.ResolveChatAsync(Selection(false), CancellationToken.None)).Value)
        {
            ChatResponse response = await chat.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")]);

            Assert.Equal("ok", response.Text);
        }

        Assert.Equal(3, handler.Sends);

        Assert.Equal(0, handler.Disposals);

        factory.Dispose();

        factory.Dispose();

        Assert.Equal(1, handler.Disposals);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            _ = await factory.ResolveChatAsync(Selection(false), CancellationToken.None);
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            _ = await factory.ResolveEmbeddingAsync(Selection(true), CancellationToken.None);
        });

        Assert.Equal(3, handler.Sends);

        Assert.Equal(1, handler.Disposals);
    }

    private static LongRestProviderSelection Selection(bool embedding) => new(
        new ProviderSettings { Name = "test", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.invalid/v1" },
        embedding ? "embedding" : "model", 64, new ModelPricingEntry());

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Sends;

        internal int Disposals;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;

            string body = request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)
                ? "{\"object\":\"list\",\"model\":\"embedding\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":\"" + Convert.ToBase64String(new byte[64 * sizeof(float)]) + "\"}],\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                : "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;

            base.Dispose(disposing);
        }
    }
}
