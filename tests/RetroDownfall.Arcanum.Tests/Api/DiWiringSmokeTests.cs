using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class DiWiringSmokeTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public DiWiringSmokeTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public void Host_ResolvesKeyRegisteredServices()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IServiceScope scope = _factory.Services.CreateScope();

        IServiceProvider services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<IGrimoireRepository>());

        Assert.NotNull(services.GetRequiredService<ICampaignRepository>());

        Assert.NotNull(services.GetRequiredService<IPromptRepository>());

        Assert.IsType<FakeIntelligenceProvider>(services.GetRequiredService<IArcanumIntelligenceProvider>());

        Assert.NotNull(services.GetRequiredService<IWard>());

        Assert.NotNull(services.GetRequiredService<ISanctumGuard>());

        Assert.NotNull(services.GetRequiredService<IChatClientFactory>());

        Assert.NotNull(services.GetRequiredService<ApiKeyEndpointFilter>());

        // The pre-binding auth middleware resolves this per request; an unregistered authenticator
        // would surface as a 500 on every gated route instead of a 401.
        Assert.NotNull(services.GetRequiredService<ApiKeyAuthenticator>());

        Assert.NotNull(services.GetRequiredService<IProvingGroundsArbiter>());

        Assert.NotNull(services.GetRequiredService<ProvingGroundsRunner>());

        Assert.NotNull(services.GetRequiredService<IMcpConnectionManager>());

        Assert.NotNull(services.GetRequiredService<ISanctumBreachRepository>());

        Assert.NotNull(services.GetRequiredService<IDataRetentionService>());

        Assert.NotNull(services.GetRequiredService<IDataRetentionPolicyStore>());

    }

    [SkippableFact]
    public async Task Host_ModelCallExecutorProviderFailure_UsesDiLoggerSafely()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "CANARY_DI_PROVIDER_RESPONSE_BODY";
        TestCapturingLogger<ModelCallExecutor> logger = new();
        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ModelCallExecutor>>(logger),
        };

        using IServiceScope scope = factory.Services.CreateScope();
        IModelCallExecutor executor = scope.ServiceProvider.GetRequiredService<IModelCallExecutor>();
        ProviderSettings provider = new()
        {
            Name = "test",
            Type = AiProviderKind.OpenAICompatible,
            Models = ["mistral:latest"],
        };

        ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
            new ThrowingChatClient(canary),
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(
                provider,
                "mistral:latest",
                ReservedAnswerTokens: 32,
                ReservedReasoningTokens: 0));

        Assert.True(outcome.IsFailure);
        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, entry.Message, StringComparison.Ordinal);

    }

    private sealed class ThrowingChatClient(string canary) : IChatClient
    {

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new InvalidOperationException(canary));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

    }

}
