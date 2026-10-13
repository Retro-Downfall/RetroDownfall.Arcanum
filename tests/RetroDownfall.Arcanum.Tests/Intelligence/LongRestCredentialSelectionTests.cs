using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LongRestCredentialSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_resolver_mutation_cannot_change_the_accepted_request_or_provider(bool embedding)
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        settings.Providers[0].CredentialEnvironmentVariable = "ACCEPTED_PROVIDER_KEY";

        settings.Providers[0].HiddenModels = ["hidden-model"];

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        LongRestConfigurationSnapshot snapshot = epoch.Capture();

        LongRestProviderSelection selected = embedding ? snapshot.ResolveEmbedding().Value : snapshot.ResolveChat().Value;

        MutatingKeys keys = new();

        RequestHandler handler = new();

        using LongRestProviderClientFactory factory = new(keys, handler);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        ProviderSettings? leasedProvider = null;

        Task operation = embedding ? Embedding() : Chat();

        await keys.Entered.Task.WaitAsync(deadline.Token);

        Assert.False(operation.IsCompleted);

        Assert.Equal(0, handler.Sends);

        keys.Release.TrySetResult();

        await operation;

        Assert.Equal(1, handler.Sends);

        Assert.Equal(embedding ? "https://provider.test/v1/embeddings" : "https://provider.test/v1/chat/completions", handler.RequestUri);

        Assert.Equal(embedding ? "embedding" : "model", handler.Model);

        if (leasedProvider is not null) AssertAcceptedProvider(leasedProvider);

        AssertAcceptedProvider(selected.Provider);

        AssertAcceptedProvider(snapshot.ResolveChat().Value.Provider);

        async Task Chat()
        {
            var resolved = await factory.ResolveChatAsync(selected, deadline.Token);

            Assert.True(resolved.IsSuccess);

            using ChatClientLease lease = resolved.Value;

            ChatResponse response = await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token);

            Assert.Equal("ok", response.Text);

            Assert.Equal("model", lease.ResolvedModel);

            leasedProvider = lease.Provider;
        }

        async Task Embedding()
        {
            var resolved = await factory.ResolveEmbeddingAsync(selected, deadline.Token);

            Assert.True(resolved.IsSuccess);

            using EmbeddingGeneratorLease lease = resolved.Value;

            var response = await lease.Generator.GenerateAsync(["probe"], new EmbeddingGenerationOptions { Dimensions = 64 }, deadline.Token);

            Assert.Single(response);

            Assert.Equal(64, response[0].Vector.Length);
        }
    }

    private static void AssertAcceptedProvider(ProviderSettings provider)
    {
        Assert.Equal("provider", provider.Name);

        Assert.Equal("https://provider.test/v1", provider.Endpoint);

        Assert.Equal("ACCEPTED_PROVIDER_KEY", provider.CredentialEnvironmentVariable);

        Assert.Equal("model", provider.Models[0].Name);

        Assert.Equal("hidden-model", Assert.Single(provider.HiddenModels));
    }

    private sealed class MutatingKeys : IProviderApiKeyResolver
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {
            provider.Name = "changed-provider";

            provider.Endpoint = "https://changed.test/v2";

            provider.CredentialEnvironmentVariable = "CHANGED_PROVIDER_KEY";

            provider.Models[0].Name = "changed-model";

            provider.HiddenModels[0] = "changed-hidden-model";

            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);

            return "test-key";
        }
    }

    private sealed class RequestHandler : HttpMessageHandler
    {
        internal int Sends;

        internal string? RequestUri;

        internal string? Model;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;

            RequestUri = request.RequestUri!.AbsoluteUri;

            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            Model = payload.RootElement.GetProperty("model").GetString();

            string body = request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)
                ? "{\"object\":\"list\",\"model\":\"embedding\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":\"" + Convert.ToBase64String(new byte[64 * sizeof(float)]) + "\"}],\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                : "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
